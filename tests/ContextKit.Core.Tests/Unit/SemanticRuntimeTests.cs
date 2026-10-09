using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GroundKit.Core.Contracts;
using GroundKit.Semantic;

namespace GroundKit.Tests.Unit;

public sealed class SemanticRuntimeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "groundkit-semantic-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Should_Default_To_Lexical_Without_Creating_Assets()
    {
        using var runtime = new SemanticRuntime(root);
        runtime.Settings.Mode.ShouldBe(SearchMode.Lexical);
        runtime.Settings.Model.ShouldBe("bge-micro-v2");
        Directory.Exists(root).ShouldBeFalse();
    }

    [Theory]
    [InlineData("lexical", SearchMode.Lexical)]
    [InlineData("SEMANTIC", SearchMode.Semantic)]
    [InlineData("hybrid", SearchMode.Hybrid)]
    public void Should_Parse_Only_Named_Search_Modes(string value, SearchMode expected) =>
        SemanticRuntime.ParseMode(value).ShouldBe(expected);

    [Theory]
    [InlineData("1")]
    [InlineData("auto")]
    [InlineData("")]
    public void Should_Reject_Unsupported_Search_Modes(string value) =>
        Should.Throw<ArgumentException>(() => SemanticRuntime.ParseMode(value));

    [Fact]
    public void Should_List_Only_Pinned_Model_Profiles()
    {
        var model = SemanticRuntime.Models.ShouldHaveSingleItem();
        model.Id.ShouldBe("bge-micro-v2");
        model.Dimensions.ShouldBe(384);
        model.Assets.Select(asset => asset.Name).ShouldBe(["model.onnx", "vocab.txt", "LICENSE"]);
        model.Assets.ShouldAllBe(asset => asset.Sha256.Length == 64 && asset.Url.Contains(model.Revision));
        Should.Throw<ArgumentException>(() => SemanticRuntime.GetModel("unverified-model"));
    }

    [Theory]
    [InlineData("1.0.0", null, "v1.0.0")]
    [InlineData("1.0.0-preview.13.26282.42", null, "v1.0.0-preview.13.26282.42")]
    [InlineData("1.0.0-rc.1.26282.42", "v1.0.0-rc.1", "v1.0.0-rc.1")]
    public void Should_Resolve_Provider_Assets_From_Actual_Release_Tag(string version, string? tag, string expectedTag)
    {
        SemanticRuntime.ProviderBaseUrl(version, "win-x64", tag).ShouldBe($"https://github.com/mehdihadeli/groundkit/releases/download/{expectedTag}/groundkit-semantic-onnx-win-x64");
    }

    [Fact]
    public async Task Should_Reject_Enable_Without_Provider_And_Not_Save_Mode()
    {
        using var runtime = new SemanticRuntime(root);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => runtime.EnableAsync(cancellationToken: TestContext.Current.CancellationToken));
        error.Message.ShouldContain("provider install onnx");
        runtime.Settings.Mode.ShouldBe(SearchMode.Lexical);
        File.Exists(Path.Combine(root, "settings.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_Reject_Missing_Model_Without_Changing_Selection()
    {
        using var runtime = new SemanticRuntime(root);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => runtime.UseModelAsync("bge-micro-v2", TestContext.Current.CancellationToken));
        error.Message.ShouldContain("model install bge-micro-v2");
        runtime.Settings.Mode.ShouldBe(SearchMode.Lexical);
    }

    [Fact]
    public void Should_Persist_Lexical_Disable_And_Retain_Assets()
    {
        using var runtime = new SemanticRuntime(root);
        Directory.CreateDirectory(runtime.ProviderPath);
        var retainedFile = Path.Combine(runtime.ProviderPath, "keep.txt");
        File.WriteAllText(retainedFile, "retained");
        runtime.Disable();
        using var restarted = new SemanticRuntime(root);
        restarted.Settings.Mode.ShouldBe(SearchMode.Lexical);
        File.ReadAllText(retainedFile).ShouldBe("retained");
    }

    [Fact]
    public async Task Should_Reject_Tampered_Download_And_Keep_Existing_Model()
    {
        var bytes = new byte[SemanticRuntime.DefaultModel.Assets[0].Size];
        using var client = new HttpClient(new BytesHandler(bytes));
        using var runtime = new SemanticRuntime(root, client);
        var modelPath = runtime.ModelPath(SemanticRuntime.DefaultModel);
        Directory.CreateDirectory(modelPath);
        var existing = Path.Combine(modelPath, "model.onnx");
        await File.WriteAllTextAsync(existing, "previous model", TestContext.Current.CancellationToken);
        var error = await Should.ThrowAsync<InvalidDataException>(() => runtime.InstallModelAsync("bge-micro-v2", TestContext.Current.CancellationToken));
        error.Message.ShouldContain("Checksum mismatch");
        (await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken)).ShouldBe("previous model");
        Directory.GetDirectories(Path.GetDirectoryName(modelPath)!).ShouldBe([modelPath]);
    }

    [Fact]
    public async Task Should_Verify_Provider_Files_Before_Activation()
    {
        using var runtime = new SemanticRuntime(root);
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        var bytes = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(Path.Combine(source, SemanticRuntime.WorkerAssembly), bytes, TestContext.Current.CancellationToken);
        await WriteManifest(source, new Dictionary<string, string>
        {
            [SemanticRuntime.WorkerAssembly] = Convert.ToHexString(SHA256.HashData(bytes)),
        });
        await runtime.InstallProviderAsync(source, TestContext.Current.CancellationToken);
        File.ReadAllBytes(Path.Combine(runtime.ProviderPath, SemanticRuntime.WorkerAssembly)).ShouldBe(bytes);
        await File.WriteAllBytesAsync(Path.Combine(source, SemanticRuntime.WorkerAssembly), [9], TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidDataException>(() => runtime.InstallProviderAsync(source, TestContext.Current.CancellationToken));
        File.ReadAllBytes(Path.Combine(runtime.ProviderPath, SemanticRuntime.WorkerAssembly)).ShouldBe(bytes);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/escape.dll")]
    [InlineData("C:/escape.dll")]
    [InlineData("sub\\escape.dll")]
    public async Task Should_Reject_Unsafe_Provider_Paths(string path)
    {
        using var runtime = new SemanticRuntime(root);
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await WriteManifest(source, new Dictionary<string, string>
        {
            [SemanticRuntime.WorkerAssembly] = new string('0', 64),
            [path] = new string('0', 64),
        });
        await Should.ThrowAsync<InvalidDataException>(() => runtime.InstallProviderAsync(source, TestContext.Current.CancellationToken));
        Directory.Exists(runtime.ProviderPath).ShouldBeFalse();
    }

    private static Task WriteManifest(string directory, Dictionary<string, string> files) => File.WriteAllTextAsync(
        Path.Combine(directory, "provider.json"),
        JsonSerializer.Serialize(new SemanticProviderManifest(1, "test", RuntimeInformation.RuntimeIdentifier, new string('0', 64), 1, files)),
        TestContext.Current.CancellationToken
    );

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}