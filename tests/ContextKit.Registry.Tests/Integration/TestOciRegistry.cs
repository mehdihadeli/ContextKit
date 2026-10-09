using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GroundKit.Configuration;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Hosting;
using GroundKit.Ingestion.Services;
using GroundKit.Oci;
using GroundKit.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace GroundKit.Registry.Tests.Integration;

/// <summary>
/// An in-memory OCI registry: bearer token endpoint, blob uploads, and manifest storage. It lets the
/// publish and install paths run against the real clients without reaching <see cref="TtlShRegistryHelper.Host"/>.
/// </summary>
/// <remarks>
/// The handler deliberately challenges with a bearer token even though ttl.sh answers anonymously,
/// because the authenticated path is what every other registry requires and is otherwise untested.
/// </remarks>
internal sealed class FakeOciRegistry : HttpMessageHandler
{
    internal const string Host = TtlShRegistryHelper.Host;

    private const string ValidToken = "test-token";

    private readonly Dictionary<string, string> _uploads = new(StringComparer.Ordinal);

    public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Manifests { get; } = new(StringComparer.Ordinal);

    public int TokenRequests { get; private set; }

    public int AuthorizedRequests { get; private set; }

    public HttpClient CreateClient() => new(this, disposeHandler: false);

    /// <summary>
    /// Corrupts a stored blob without changing its length, so the size check still passes and only
    /// the SHA-256 verification can catch it.
    /// </summary>
    public void Tamper(string ociReference)
    {
        var reference = OciReference.Parse(ociReference);
        var manifest = JsonNode.Parse(Manifests[$"{reference.Repository}:{reference.Reference}"])!;
        var digest = manifest["layers"]![0]!["digest"]!.GetValue<string>();
        var corrupted = (byte[])Blobs[digest].Clone();
        corrupted[^1] ^= 0xFF;
        Blobs[digest] = corrupted;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var path = request.RequestUri!.AbsolutePath;

        if (path == "/token")
        {
            TokenRequests++;
            return Json(new JsonObject { ["token"] = ValidToken });
        }

        if (request.Headers.Authorization?.Parameter != ValidToken)
        {
            var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            unauthorized.Headers.WwwAuthenticate.Add(
                new AuthenticationHeaderValue(
                    "Bearer",
                    $"realm=\"https://{Host}/token\",service=\"{Host}\""
                )
            );
            return unauthorized;
        }

        AuthorizedRequests++;

        if (request.Method == HttpMethod.Head)
        {
            var digest = path[(path.LastIndexOf('/') + 1)..];
            return new HttpResponseMessage(
                Blobs.ContainsKey(digest) ? HttpStatusCode.OK : HttpStatusCode.NotFound
            );
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/blobs/uploads/", StringComparison.Ordinal))
        {
            var uploadId = Guid.NewGuid().ToString("N");
            _uploads[uploadId] = path;
            var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
            accepted.Headers.Location = new Uri($"https://{Host}/{path.TrimStart('/')}/{uploadId}");
            return accepted;
        }

        if (request.Method == HttpMethod.Put && path.Contains("/blobs/uploads/", StringComparison.Ordinal))
        {
            var digest = request.RequestUri.Query.TrimStart('?')
                .Split('&')
                .Select(part => part.Split('=', 2))
                .First(part => part[0] == "digest")[1];
            Blobs[Uri.UnescapeDataString(digest)] =
                await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        if (path.Contains("/manifests/", StringComparison.Ordinal))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // /v2/<repository...>/manifests/<reference>
            var referenceIndex = parts.Length - 1;
            var repository = string.Join('/', parts[1..(referenceIndex - 1)]);
            var reference = parts[referenceIndex];
            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                var payload = Encoding.UTF8.GetString(body);
                var digest = OciNames.Digest(body);
                Manifests[$"{repository}:{reference}"] = payload;
                // Real registries also resolve a manifest by its own content digest.
                Manifests[$"{repository}:{digest}"] = payload;
                var created = new HttpResponseMessage(HttpStatusCode.Created);
                created.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
                return created;
            }

            return Manifests.TryGetValue($"{repository}:{reference}", out var stored)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes(stored)),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (request.Method == HttpMethod.Get && path.Contains("/blobs/", StringComparison.Ordinal))
        {
            var digest = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
            return Blobs.TryGetValue(digest, out var blob)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(blob) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"),
        };
}

/// <summary>Serves a static catalog from a fixed byte payload.</summary>
internal sealed class CatalogHandler(byte[] catalog) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(catalog) }
        );
}

/// <summary>Routes every named client onto the supplied handler.</summary>
internal sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>
/// The real consumer pipeline the <c>ck install</c> command builds: a catalog-backed
/// <see cref="ContextRegistryClient"/>, a <see cref="PackageDownloadService"/>, and a
/// <see cref="GroundKit.Cli.CliApplication"/>. Package bytes come from <paramref name="ociHandler"/>,
/// which is either the in-memory registry or a live HTTP handler.
/// </summary>
internal sealed class TestConsumerContext : IDisposable
{
    private readonly ServiceProvider _provider;

    private TestConsumerContext(
        ServiceProvider provider,
        GroundKit.Cli.CliApplication cli,
        SqlitePackageStore store,
        HttpClient catalogClient
    )
    {
        _provider = provider;
        Cli = cli;
        Store = store;
        CatalogClient = catalogClient;
    }

    public GroundKit.Cli.CliApplication Cli { get; }

    public SqlitePackageStore Store { get; }

    public HttpClient CatalogClient { get; }

    public static TestConsumerContext Create(
        byte[] catalog,
        HttpMessageHandler ociHandler,
        string storeRoot
    )
    {
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        services.AddSingleton<IHttpClientFactory>(new TestHttpClientFactory(ociHandler));
        var provider = services.BuildServiceProvider();
        var catalogClient = new HttpClient(new CatalogHandler(catalog))
        {
            BaseAddress = new Uri("https://catalog.test/registry/index.json/"),
        };
        var store = new SqlitePackageStore(
            new PackageStoreOptions(storeRoot),
            NullLogger<SqlitePackageStore>.Instance
        );
        var registryClient = new ContextRegistryClient(
            catalogClient,
            provider.GetRequiredService<IHttpClientFactory>()
        );
        var packageDownloadService = new PackageDownloadService(
            registryClient,
            store,
            provider.GetRequiredService<IDocumentPackageBuilder>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            new GroundKitOptions(),
            NullLogger<PackageDownloadService>.Instance
        );
        var cli = new GroundKit.Cli.CliApplication(
            provider.GetRequiredService<IDocumentPackageBuilder>(),
            store,
            registryClient,
            packageDownloadService
        );

        return new TestConsumerContext(provider, cli, store, catalogClient);
    }

    /// <summary>
    /// Runs a CLI command while capturing console output, because
    /// <see cref="AnsiConsole.Console"/> is process-wide global state.
    /// </summary>
    public async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var previous = AnsiConsole.Console;
        var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(
            new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(writer),
            }
        );

        try
        {
            var exitCode = await Cli.RunAsync(args);
            return (exitCode, writer.ToString());
        }
        finally
        {
            AnsiConsole.Console = previous;
        }
    }

    public void Dispose()
    {
        CatalogClient.Dispose();
        _provider.Dispose();
        // SQLite pools file handles, which otherwise keep the temp tree undeletable on Windows.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}

/// <summary>Scaffolding shared by the OCI scenario tests.</summary>
internal static class TestOciScenario
{
    public static void WriteDefinition(string registryRoot, string name, string manager = "npm")
    {
        var directory = Path.Combine(registryRoot, manager);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{name}.yaml"),
            $"""
            name: {name}
            description: {name} documentation
            source:
              type: git
              url: "https://github.com/example/{name}"
              docs_path: docs
            """
        );
    }

    /// <summary>
    /// Writes a definition pinned to one explicit version. Time-limited registries such as ttl.sh
    /// derive the artifact tag, and therefore the expiry, from the version string.
    /// </summary>
    public static void WriteVersionedDefinition(
        string registryRoot,
        string name,
        string version,
        string manager = "npm"
    )
    {
        var directory = Path.Combine(registryRoot, manager);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{name}.yaml"),
            $"""
            name: {name}
            description: {name} documentation
            versions:
              - version: "{version}"
                source:
                  type: git
                  url: "https://github.com/example/{name}"
                  docs_path: docs
            """
        );
    }

    public static async Task<RegistryCatalog> ReadCatalogAsync(string catalogPath) =>
        JsonSerializer.Deserialize<RegistryCatalog>(
            await File.ReadAllTextAsync(catalogPath, TestContext.Current.CancellationToken)
        )
        ?? throw new InvalidDataException($"Catalog '{catalogPath}' could not be read.");

    /// <summary>
    /// Runs a registry command against the in-memory OCI registry, capturing console output.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunRegistryAsync(
        FakeOciRegistry registry,
        params string[] args
    )
    {
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        using var provider = services.BuildServiceProvider();
        var application = new RegistryApplication(
            provider.GetRequiredService<IDocumentPackageBuilder>(),
            NullLoggerFactory.Instance,
            provider.GetRequiredService<IHttpClientFactory>(),
            registry.CreateClient()
        );
        return await RunCapturedAsync(application, args);
    }

    /// <summary>
    /// Runs a registry command against a real registry host. Credentials come from
    /// <c>GROUNDKIT_OCI_USERNAME</c>/<c>GROUNDKIT_OCI_TOKEN</c>, the same variables the CLI reads.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunRegistryLiveAsync(params string[] args)
    {
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        using var provider = services.BuildServiceProvider();
        var application = new RegistryApplication(
            provider.GetRequiredService<IDocumentPackageBuilder>(),
            NullLoggerFactory.Instance,
            provider.GetRequiredService<IHttpClientFactory>()
        );
        return await RunCapturedAsync(application, args);
    }

    private static async Task<(int ExitCode, string Output)> RunCapturedAsync(
        RegistryApplication application,
        string[] args
    )
    {
        var previous = AnsiConsole.Console;
        var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(
            new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(writer),
            }
        );

        try
        {
            var exitCode = await application.RunAsync(args);
            return (exitCode, writer.ToString());
        }
        finally
        {
            AnsiConsole.Console = previous;
        }
    }
}
