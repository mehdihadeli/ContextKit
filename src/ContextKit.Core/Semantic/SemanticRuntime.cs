using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GroundKit.Core.Contracts;

[assembly: TypeForwardedTo(typeof(GroundKit.Semantic.SemanticSettings))]
[assembly: TypeForwardedTo(typeof(GroundKit.Semantic.SemanticAsset))]
[assembly: TypeForwardedTo(typeof(GroundKit.Semantic.SemanticModelProfile))]
[assembly: TypeForwardedTo(typeof(GroundKit.Semantic.ISemanticEmbeddingProvider))]

namespace GroundKit.Semantic;

public sealed record SemanticProviderManifest(
    int ProtocolVersion,
    string Version,
    string RuntimeIdentifier,
    string Sha256,
    long Size,
    Dictionary<string, string> Files
);

public sealed class SemanticRuntime : ISemanticEmbeddingProvider, ISemanticSearchContext, IDisposable
{
    public const int ProtocolVersion = 1;
    public const string WorkerAssembly = "ContextKit.Semantic.Onnx.dll";
    private const long MaxBundleBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions WireOptions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private Process? _worker;
    private Task<string>? _workerErrors;
    private string? _workerModel;

    public SemanticRuntime(string? rootPath = null, HttpClient? client = null)
    {
        RootPath = rootPath ?? Path.Combine(
            Environment.GetEnvironmentVariable("GROUNDKIT_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".groundkit"),
            "semantic"
        );
        _httpClient = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsClient = client is null;
    }

    public string RootPath { get; }
    public string ProviderPath => Path.Combine(RootPath, "providers", "onnx");
    public string CachePath => Path.Combine(RootPath, "indexes");
    public SearchMode Mode => Settings.Mode;
    public SemanticModelProfile Model => GetModel(Settings.Model);
    public SemanticSettings Settings => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<SemanticSettings>(File.ReadAllText(SettingsPath), JsonOptions)
            ?? new()
        : new();
    private string SettingsPath => Path.Combine(RootPath, "settings.json");

    public static SemanticModelProfile DefaultModel { get; } = CreateDefaultModel();
    public static IReadOnlyList<SemanticModelProfile> Models { get; } = [DefaultModel];

    public static SearchMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "lexical" => SearchMode.Lexical,
        "semantic" => SearchMode.Semantic,
        "hybrid" => SearchMode.Hybrid,
        _ => throw new ArgumentException("Search mode must be lexical, semantic, or hybrid."),
    };

    public static SemanticModelProfile GetModel(string id) => Models.FirstOrDefault(
        model => model.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
    ) ?? throw new ArgumentException($"Unsupported model '{id}'. Run ck semantic model list.");

    public string ModelPath(SemanticModelProfile model) => Path.Combine(RootPath, "models", model.Id, model.Revision);

    public async Task InstallModelAsync(string id, CancellationToken cancellationToken = default)
    {
        var model = GetModel(id);
        var target = ModelPath(model);
        var staging = target + "." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var asset in model.Assets)
            {
                await DownloadAsync(asset.Url, Path.Combine(staging, asset.Name), asset.Size, asset.Sha256, cancellationToken);
            }
            await _gate.WaitAsync(cancellationToken);
            try
            {
                StopWorker();
                Activate(staging, target);
            }
            finally { _gate.Release(); }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public async Task InstallProviderAsync(string? bundleDirectory = null, CancellationToken cancellationToken = default)
    {
        var staging = ProviderPath + "." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            if (bundleDirectory is not null)
            {
                var source = Path.GetFullPath(bundleDirectory);
                var manifest = ReadManifest(Path.Combine(source, "provider.json"));
                ValidateManifest(manifest);
                foreach (var file in manifest.Files)
                {
                    var relativePath = SafeRelativePath(file.Key);
                    var sourcePath = Path.Combine(source, relativePath);
                    await VerifyAsync(sourcePath, file.Value, cancellationToken);
                    var destination = Path.Combine(staging, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(sourcePath, destination);
                }
                File.Copy(Path.Combine(source, "provider.json"), Path.Combine(staging, "provider.json"));
            }
            else
            {
                var metadata = typeof(SemanticRuntime).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
                var version = metadata.FirstOrDefault(attribute => attribute.Key == "GroundKitProviderVersion")?.Value
                    ?? typeof(SemanticRuntime).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                    ?? throw new InvalidOperationException("GroundKit release version is unavailable.");
                var releaseTag = metadata.FirstOrDefault(attribute => attribute.Key == "GroundKitProviderReleaseTag")?.Value ?? "v" + version;
                var rid = RuntimeInformation.RuntimeIdentifier;
                var baseUrl = ProviderBaseUrl(version, rid, releaseTag);
                using var response = await _httpClient.GetAsync(baseUrl + ".json", cancellationToken);
                response.EnsureSuccessStatusCode();
                var manifestBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (manifestBytes.Length > 1024 * 1024) throw new InvalidDataException("Provider manifest is too large.");
                var manifest = JsonSerializer.Deserialize<SemanticProviderManifest>(manifestBytes, JsonOptions)
                    ?? throw new InvalidDataException("Provider manifest is empty.");
                ValidateManifest(manifest);
                if (manifest.Version != version) throw new InvalidDataException("Provider release version mismatch.");
                var archive = Path.Combine(staging, "bundle.zip");
                await DownloadAsync(baseUrl + ".zip", archive, manifest.Size, manifest.Sha256, cancellationToken);
                using (var zip = ZipFile.OpenRead(archive))
                {
                    long extractedBytes = 0;
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith('/')) continue;
                        var relativePath = SafeRelativePath(entry.FullName);
                        if (!manifest.Files.ContainsKey(entry.FullName)) throw new InvalidDataException("Unlisted provider file.");
                        extractedBytes = checked(extractedBytes + entry.Length);
                        if (extractedBytes > MaxBundleBytes) throw new InvalidDataException("Provider archive exceeds size limit.");
                        var destination = Path.Combine(staging, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        entry.ExtractToFile(destination);
                    }
                }
                File.Delete(archive);
                await File.WriteAllBytesAsync(Path.Combine(staging, "provider.json"), manifestBytes, cancellationToken);
            }
            await ValidateProviderAsync(staging, cancellationToken);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                StopWorker();
                Activate(staging, ProviderPath);
            }
            finally { _gate.Release(); }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public async Task EnableAsync(string? modelId = null, SearchMode mode = SearchMode.Hybrid, CancellationToken cancellationToken = default)
    {
        if (mode is not (SearchMode.Semantic or SearchMode.Hybrid)) throw new ArgumentException("Enable mode must be semantic or hybrid.");
        var model = GetModel(modelId ?? Settings.Model);
        await EmbedAsync(model, ["GroundKit embedding compatibility check"], cancellationToken);
        SaveSettings(new SemanticSettings(mode, model.Id));
    }

    public void Disable() => SaveSettings(Settings with { Mode = SearchMode.Lexical });

    public async Task UseModelAsync(string id, CancellationToken cancellationToken = default)
    {
        var model = GetModel(id);
        await ValidateModelAsync(model, cancellationToken);
        if (Settings.Mode != SearchMode.Lexical) await EmbedAsync(model, ["GroundKit model check"], cancellationToken);
        SaveSettings(Settings with { Model = model.Id });
    }

    public async Task<float[][]> EmbedAsync(SemanticModelProfile model, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];
        if (texts.Count > 32) throw new ArgumentException("Embedding batches are limited to 32 texts.");
        await _gate.WaitAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            if (_worker is null || _worker.HasExited || _workerModel != model.Identity)
            {
                StopWorker();
                await ValidateProviderAsync(ProviderPath, timeout.Token);
                await ValidateModelAsync(model, timeout.Token);
                var start = new ProcessStartInfo("dotnet")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = ProviderPath,
                };
                start.ArgumentList.Add(Path.Combine(ProviderPath, WorkerAssembly));
                start.ArgumentList.Add(ModelPath(model));
                _worker = Process.Start(start) ?? throw new InvalidOperationException("Cannot start ONNX provider.");
                _workerErrors = ReadErrorsAsync(_worker.StandardError);
                _workerModel = model.Identity;
            }
            var request = JsonSerializer.Serialize(new { ProtocolVersion, Texts = texts }, WireOptions);
            await _worker.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
            await _worker.StandardInput.FlushAsync(timeout.Token);
            var responseLine = await _worker.StandardOutput.ReadLineAsync(timeout.Token);
            if (responseLine is null)
            {
                var details = await _workerErrors!.WaitAsync(timeout.Token);
                throw new InvalidOperationException("ONNX provider exited without a response. " + details);
            }
            var response = JsonSerializer.Deserialize<EmbeddingReply>(responseLine, WireOptions)
                ?? throw new InvalidDataException("Invalid embedding response.");
            if (response.Version != ProtocolVersion) throw new InvalidDataException("Embedding protocol mismatch.");
            if (response.Error is not null) throw new InvalidOperationException(response.Error);
            var vectors = response.Vectors ?? throw new InvalidDataException("Missing embedding vectors.");
            if (vectors.Length != texts.Count) throw new InvalidDataException("Embedding count mismatch.");
            foreach (var vector in vectors)
            {
                if (vector.Length != model.Dimensions || vector.Any(value => !float.IsFinite(value)))
                    throw new InvalidDataException("Invalid embedding dimensions or values.");
                var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
                if (norm <= 0) throw new InvalidDataException("Embedding vector has zero length.");
                for (var index = 0; index < vector.Length; index++) vector[index] = (float)(vector[index] / norm);
            }
            return vectors;
        }
        catch
        {
            StopWorker();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task ValidateModelAsync(SemanticModelProfile model, CancellationToken cancellationToken = default)
    {
        foreach (var asset in model.Assets)
        {
            var path = Path.Combine(ModelPath(model), asset.Name);
            if (!File.Exists(path)) throw new InvalidOperationException($"Model missing. Run ck semantic model install {model.Id}.");
            if (new FileInfo(path).Length != asset.Size) throw new InvalidDataException("Model asset size mismatch.");
            await VerifyAsync(path, asset.Sha256, cancellationToken);
        }
    }

    private static async Task ValidateProviderAsync(string directory, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, "provider.json");
        if (!File.Exists(manifestPath)) throw new InvalidOperationException("ONNX provider missing. Run ck semantic provider install onnx.");
        var manifest = ReadManifest(manifestPath);
        ValidateManifest(manifest);
        foreach (var file in manifest.Files) await VerifyAsync(Path.Combine(directory, SafeRelativePath(file.Key)), file.Value, cancellationToken);
    }

    private static SemanticProviderManifest ReadManifest(string path) =>
        JsonSerializer.Deserialize<SemanticProviderManifest>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("Invalid provider manifest.");

    public static string ProviderBaseUrl(string version, string runtimeIdentifier, string? releaseTag = null) =>
        $"https://github.com/mehdihadeli/groundkit/releases/download/{Uri.EscapeDataString(releaseTag ?? "v" + version)}/groundkit-semantic-onnx-{Uri.EscapeDataString(runtimeIdentifier)}";

    private static void ValidateManifest(SemanticProviderManifest manifest)
    {
        if (manifest.ProtocolVersion != ProtocolVersion || manifest.RuntimeIdentifier != RuntimeInformation.RuntimeIdentifier)
            throw new InvalidDataException("Provider protocol or OS/architecture mismatch.");
        if (manifest.Size <= 0 || manifest.Size > MaxBundleBytes || manifest.Files is null || !manifest.Files.ContainsKey(WorkerAssembly))
            throw new InvalidDataException("Invalid provider bundle manifest.");
        foreach (var file in manifest.Files)
        {
            SafeRelativePath(file.Key);
            if (file.Value.Length != 64 || !file.Value.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid provider file checksum.");
        }
    }

    private static string SafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':')
            || path.Split('/').Any(segment => segment is ".." or "." or ""))
            throw new InvalidDataException("Unsafe provider asset path.");
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private async Task DownloadAsync(string url, string destination, long size, string sha256, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Semantic downloads require HTTPS.");
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(destination))
        {
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += count;
                if (total > size) throw new InvalidDataException("Semantic download exceeds declared size.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (total != size) throw new InvalidDataException("Semantic download size mismatch.");
        }
        await VerifyAsync(destination, sha256, cancellationToken);
    }

    private static async Task VerifyAsync(string path, string sha256, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Checksum mismatch: {Path.GetFileName(path)}.");
    }

    private void SaveSettings(SemanticSettings settings)
    {
        Directory.CreateDirectory(RootPath);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, SettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Activate(string staging, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var backup = target + ".old." + Guid.NewGuid().ToString("N");
        if (Directory.Exists(target)) Directory.Move(target, backup);
        try { Directory.Move(staging, target); }
        catch
        {
            if (Directory.Exists(backup)) Directory.Move(backup, target);
            throw;
        }
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
    }

    private void StopWorker()
    {
        if (_worker is not null)
        {
            if (!_worker.HasExited) _worker.Kill(entireProcessTree: true);
            _worker.Dispose();
            _worker = null;
        }
        _workerModel = null;
        _workerErrors = null;
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        var text = new StringBuilder();
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
            {
                text.Append(buffer, 0, count);
                if (text.Length > 4096) text.Remove(0, text.Length - 4096);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        return text.ToString();
    }

    public void Dispose()
    {
        StopWorker();
        _gate.Dispose();
        if (_ownsClient) _httpClient.Dispose();
    }

    private sealed record EmbeddingReply([property: JsonPropertyName("ProtocolVersion")] int Version, float[][]? Vectors, string? Error);

    private static SemanticModelProfile CreateDefaultModel()
    {
        const string revision = "3edf6d7de0faa426b09780416fe61009f26ae589";
        const string baseUrl = "https://huggingface.co/TaylorAI/bge-micro-v2/resolve/" + revision + "/";
        return new("bge-micro-v2", revision, 384, 512, "MIT", [
            new("model.onnx", baseUrl + "onnx/model_quantized.onnx", "ed65e36025aa94cb74207dab863c85452919ec0ab7df3512092932aa22c9a33a", 17409774),
            new("vocab.txt", baseUrl + "vocab.txt", "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3", 231508),
            new("LICENSE", baseUrl + "LICENSE", "64dd9288bb910aeec27deb6f1c170f869eae95dd44e3a42fdc5640635decfdce", 1073),
        ]);
    }
}