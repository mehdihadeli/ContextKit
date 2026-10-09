using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GroundKit.Core.Contracts;
using GroundKit.Ingestion.Services;

namespace GroundKit.Core.Tests.Unit;

public sealed class ContextRegistryClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "groundkit-catalog-tests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] PackageBytes = [1, 2, 3, 4];

    public ContextRegistryClientTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Should_Search_Static_Catalog_By_Identity_And_Version()
    {
        var client = CreateClient(new RegistryCatalog(1, [Entry("9.0.0"), Entry("10.0.0"),
            Entry("1.0.0") with { Name = "other" }]));

        var matches = await client.SearchAsync("npm", "react", cancellationToken: TestContext.Current.CancellationToken);
        matches.Select(package => package.Version).ShouldBe(["10.0.0", "9.0.0"]);
        var exact = await client.SearchAsync("npm", "react", "9.0.0", TestContext.Current.CancellationToken);
        exact.Single().Version.ShouldBe("9.0.0");
        (await client.SearchAsync("pip", "react", cancellationToken: TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("npm", "@trpc/server")]
    [InlineData("go", "github.com/spf13/cobra")]
    public async Task Should_Resolve_Metadata_For_Nested_Names(string registry, string name)
    {
        var client = CreateClient(new RegistryCatalog(1, [Entry() with { Registry = registry, Name = name, SourceCommit = "abc123" }]));
        var metadata = await client.GetMetadataAsync(registry, name, "1.0.0", TestContext.Current.CancellationToken);
        metadata.ShouldBe(new RegistryPackageMetadata(registry, name, "1.0.0", "abc123"));
    }

    [Fact]
    public async Task Should_Download_And_Verify_Static_Package()
    {
        var client = CreateClient(new RegistryCatalog(1, [Entry()]));
        var destination = Path.Combine(_root, "package.db");
        await client.DownloadAsync("npm", "react", "1.0.0", destination, TestContext.Current.CancellationToken);
        (await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken)).ShouldBe(PackageBytes);
        Directory.GetFiles(_root).Length.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_Reject_Corrupt_Download_Without_Replacing_Destination(bool wrongHash)
    {
        var entry = wrongHash ? Entry() with { Sha256 = new string('0', 64) } : Entry() with { Size = 99 };
        var client = CreateClient(new RegistryCatalog(1, [entry]));
        var destination = Path.Combine(_root, "package.db");
        await File.WriteAllBytesAsync(destination, [99], TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidDataException>(() => client.DownloadAsync("npm", "react", "1.0.0",
            destination, TestContext.Current.CancellationToken));
        (await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken)).ShouldBe(new byte[] { 99 });
        Directory.GetFiles(_root).Length.ShouldBe(1);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task Should_Reject_Unsupported_Or_Duplicate_Catalog(int schema, bool duplicate)
    {
        var client = CreateClient(new RegistryCatalog(schema, duplicate ? [Entry(), Entry()] : [Entry()]));
        await Should.ThrowAsync<InvalidDataException>(() => client.SearchAsync("npm", "react",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_Report_Missing_Exact_Version()
    {
        var client = CreateClient(new RegistryCatalog(1, [Entry()]));
        var exception = await Should.ThrowAsync<HttpRequestException>(() => client.DownloadAsync("npm", "react", "2.0.0",
            Path.Combine(_root, "package.db"), TestContext.Current.CancellationToken));
        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Directory.GetFiles(_root).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_Preserve_Api_Search_Metadata_And_Download_Routes()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath switch
            {
                "/search" => JsonResponse(new[] { new RegistryPackage("react", "npm", "1.0.0", null, 4) }),
                "/packages/npm/react/1.0.0" => JsonResponse(new RegistryPackageMetadata("npm", "react", "1.0.0", null)),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PackageBytes) },
            };
        })) { BaseAddress = new Uri("https://api.example.test/") };
        var client = new ContextRegistryClient(http);
        (await client.SearchAsync("npm", "react", "1.0.0", TestContext.Current.CancellationToken)).Single().Name.ShouldBe("react");
        (await client.GetMetadataAsync("npm", "react", "1.0.0", TestContext.Current.CancellationToken)).Version.ShouldBe("1.0.0");
        var destination = Path.Combine(_root, "package.db");
        await client.DownloadAsync("npm", "react", "1.0.0", destination, TestContext.Current.CancellationToken);
        requests.ShouldBe(["/search?registry=npm&name=react&version=1.0.0",
            "/packages/npm/react/1.0.0", "/packages/npm/react/1.0.0/download"]);
        File.ReadAllBytes(destination).ShouldBe(PackageBytes);
    }

    private static RegistryCatalogEntry Entry(string version = "1.0.0") => new("npm", "react", version,
        "React", "https://assets.example.test/package.db", PackageBytes.Length,
        Convert.ToHexString(SHA256.HashData(PackageBytes)));

    private static ContextRegistryClient CreateClient(RegistryCatalog catalog) => new(new HttpClient(new Handler(request =>
        request.RequestUri!.AbsolutePath == "/index.json" ? JsonResponse(catalog)
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PackageBytes) }))
        { BaseAddress = new Uri("https://catalog.example.test/index.json/") });

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
