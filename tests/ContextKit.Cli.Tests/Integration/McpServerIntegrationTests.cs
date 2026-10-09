using System.Text.Json;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Ingestion.Services;
using GroundKit.Mcp;
using GroundKit.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroundKit.Tests.Integration;

public sealed class McpServerIntegrationTests : IDisposable
{
    private readonly string _tempRoot;

    public McpServerIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Should_Return_Hit_From_Built_Package_End_To_End()
    {
        var packageStore = await CreatePopulatedStoreAsync();
        var responseAgent = new GroundKitToolResponseAgent(
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider()
        );

        var result = await GroundKitMcpTools.QueryDocsAsync(
            "docs",
            "local-first",
            packageStore,
            responseAgent,
            CancellationToken.None,
            maxTokens: 500,
            maxHits: 5,
            relativeScoreCutoff: 0.1
        );

        result.IsError.ShouldNotBe(true);
        var structured = result.StructuredContent.ShouldBeOfType<JsonElement>();
        structured.GetProperty("packageId").GetString().ShouldBe("docs");
        structured.GetProperty("hits").GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Should_Return_Installed_Package_End_To_End()
    {
        var packageStore = await CreatePopulatedStoreAsync();
        var responseAgent = new GroundKitToolResponseAgent(
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider()
        );

        var result = await GroundKitMcpTools.ResolveSourceAsync(
            "docs",
            packageStore,
            responseAgent,
            CancellationToken.None,
            maxResults: 5
        );

        result.IsError.ShouldNotBe(true);
        var structured = result.StructuredContent.ShouldBeOfType<JsonElement>();
        structured.GetProperty("packages").GetArrayLength().ShouldBe(1);
    }

    private async Task<IPackageStore> CreatePopulatedStoreAsync()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "intro.md"),
            "# Introduction\n\nGroundKit is a local-first documentation MCP."
        );

        var builder = new DocumentPackageBuilder(
            new SourceDetector(),
            new TestHttpClientFactory(),
            NullLogger<DocumentPackageBuilder>.Instance
        );
        var buildResult = await builder.BuildAsync(docsPath);

        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "packages")),
            NullLogger<SqlitePackageStore>.Instance
        );
        await store.SaveAsync(buildResult);
        return store;
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient(new TestMessageHandler());
    }

    private sealed class TestMessageHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
