using GroundKit.Core.Contracts;
using GroundKit.Ingestion.Services;
using GroundKit.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroundKit.Storage.Sqlite.Tests.Integration;

public sealed class PackageLifecycleTests : IDisposable
{
    private readonly string _tempRoot;

    public PackageLifecycleTests()
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
    public async Task Should_Build_Save_And_Query_Local_Directory_Package()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "intro.md"),
            "# Introduction\n\nGroundKit is a local-first documentation MCP.",
            TestContext.Current.CancellationToken
        );

        var builder = new DocumentPackageBuilder(
            new SourceDetector(),
            new TestHttpClientFactory(),
            NullLogger<DocumentPackageBuilder>.Instance
        );
        var buildResult = await builder.BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "packages")),
            NullLogger<SqlitePackageStore>.Instance
        );
        var packagePath = await store.SaveAsync(buildResult, TestContext.Current.CancellationToken);

        var response = await store.QueryAsync(
            new DocsQueryRequest(buildResult.Manifest.PackageId, "local-first"),
            TestContext.Current.CancellationToken
        );

        File.Exists(packagePath).ShouldBeTrue();
        response.Hits.ShouldNotBeEmpty();
        response.Hits.ShouldContain(hit =>
            hit.Content.Contains("local-first", StringComparison.OrdinalIgnoreCase)
        );

        // A hit is only citable if it says which document it came from, so the path must survive the
        // whole trip from ingestion through the FTS query into the response.
        response.Hits.ShouldAllBe(hit => !string.IsNullOrWhiteSpace(hit.Path));
        response.Hits.ShouldContain(hit =>
            hit.Path != null && hit.Path.Contains("intro.md", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task Should_Persist_Warning_Count_In_Package_Summary()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "readme.md"),
            "# Readme\n\nDocumentation.",
            TestContext.Current.CancellationToken
        );

        var builder = new DocumentPackageBuilder(
            new SourceDetector(),
            new TestHttpClientFactory(),
            NullLogger<DocumentPackageBuilder>.Instance
        );
        var buildResult = await builder.BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "warning-packages")),
            NullLogger<SqlitePackageStore>.Instance
        );

        await store.SaveAsync(buildResult, TestContext.Current.CancellationToken);
        var summary = (await store.ListAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        summary.WarningCount.ShouldBe(buildResult.Warnings.Count);
        summary.WarningCount.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("local-first")]
    [InlineData("local first")]
    [InlineData("\"local-first\"")]
    public async Task Should_Match_Topics_That_Differ_From_Content_Only_By_Separators(
        string topic
    )
    {
        var docsPath = Path.Combine(_tempRoot, "separators");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "intro.md"),
            "# Introduction\n\nGroundKit is a local-first documentation MCP.",
            TestContext.Current.CancellationToken
        );

        var builder = new DocumentPackageBuilder(
            new SourceDetector(),
            new TestHttpClientFactory(),
            NullLogger<DocumentPackageBuilder>.Instance
        );
        var buildResult = await builder.BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "separator-packages")),
            NullLogger<SqlitePackageStore>.Instance
        );
        await store.SaveAsync(buildResult, TestContext.Current.CancellationToken);

        var response = await store.QueryAsync(
            new DocsQueryRequest(buildResult.Manifest.PackageId, topic),
            TestContext.Current.CancellationToken
        );

        // The FTS5 tokenizer splits on every non-alphanumeric character, so "local-first" is indexed as
        // the two tokens "local" and "first". A query that merely reformats the separator must still match.
        response.Hits.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("\"request interceptors\"", "exact.md")]
    [InlineData("\"request-interceptors\"", "exact.md")]
    [InlineData("request_interceptors", "exact.md")]
    [InlineData("OR", "operators.md")]
    [InlineData("\"OR\"", "operators.md")]
    [InlineData("\"request", "exact.md", "separated.md")]
    [InlineData("\u8ba4\u8bc1", "unicode.md")]
    [InlineData("R", "single.md")]
    public async Task Should_Search_Literal_Terms_And_Preserve_Quoted_Phrases(
        string topic,
        params string[] expectedFiles
    )
    {
        var docsPath = Path.Combine(_tempRoot, "literal-docs");
        Directory.CreateDirectory(docsPath);
        var documents = new Dictionary<string, string>
        {
            ["exact.md"] = "# Guide\n\nUse request interceptors for authentication.",
            ["separated.md"] = "# Guide\n\nA request passes through several interceptors.",
            ["operators.md"] = "# Guide\n\nOR is a reserved query operator.",
            ["unicode.md"] = "# Guide\n\n\u8ba4\u8bc1 \u914d\u7f6e",
            ["single.md"] = "# Guide\n\nR language support.",
        };
        foreach (var (path, content) in documents)
        {
            await File.WriteAllTextAsync(
                Path.Combine(docsPath, path),
                content,
                TestContext.Current.CancellationToken
            );
        }

        var builder = new DocumentPackageBuilder(
            new SourceDetector(),
            new TestHttpClientFactory(),
            NullLogger<DocumentPackageBuilder>.Instance
        );
        var buildResult = await builder.BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "literal-packages")),
            NullLogger<SqlitePackageStore>.Instance
        );
        await store.SaveAsync(buildResult, TestContext.Current.CancellationToken);

        var response = await store.QueryAsync(
            new DocsQueryRequest(
                buildResult.Manifest.PackageId,
                topic,
                new RetrievalOptions(MaxHits: 20, RelativeScoreCutoff: 0.1)
            ),
            TestContext.Current.CancellationToken
        );

        var expectedContents = expectedFiles.Select(path => documents[path]).Order().ToArray();
        response.Hits
            .Select(hit => hit.Content.Replace("\r\n", "\n"))
            .Order()
            .ToArray()
            .ShouldBe(expectedContents);
    }

    [Fact]
    public async Task Should_Query_Exact_Package_Version_By_Name_And_Version()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "intro.md"),
            "# Intro\n\nVersioned docs.",
            TestContext.Current.CancellationToken
        );

        var builder = new DocumentPackageBuilder(
            new SourceDetector(),
            new TestHttpClientFactory(),
            NullLogger<DocumentPackageBuilder>.Instance
        );
        var buildResult = await builder.BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var versionOne = buildResult with
        {
            Manifest = buildResult.Manifest with
            {
                Version = "1.0.0",
                BuiltAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            },
        };
        var versionTwo = buildResult with
        {
            Manifest = buildResult.Manifest with
            {
                Version = "2.0.0",
                BuiltAt = DateTimeOffset.UtcNow,
            },
        };

        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "packages")),
            NullLogger<SqlitePackageStore>.Instance
        );
        await store.SaveAsync(versionOne, TestContext.Current.CancellationToken);
        await store.SaveAsync(versionTwo, TestContext.Current.CancellationToken);

        var latest = await store.QueryAsync(
            new DocsQueryRequest(buildResult.Manifest.PackageId, "Versioned"),
            TestContext.Current.CancellationToken
        );
        var exact = await store.QueryAsync(
            new DocsQueryRequest($"{buildResult.Manifest.PackageId}@1.0.0", "Versioned"),
            TestContext.Current.CancellationToken
        );

        latest.Version.ShouldBe("2.0.0");
        exact.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task Should_Round_Trip_Exported_And_Imported_Package()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "page.md"),
            "# Page\n\nContent.",
            TestContext.Current.CancellationToken
        );

        var builder = new DocumentPackageBuilder(
            new SourceDetector(),
            new TestHttpClientFactory(),
            NullLogger<DocumentPackageBuilder>.Instance
        );
        var buildResult = await builder.BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var sourceStore = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "source-packages")),
            NullLogger<SqlitePackageStore>.Instance
        );
        await sourceStore.SaveAsync(buildResult, TestContext.Current.CancellationToken);

        var exportPath = Path.Combine(_tempRoot, "exports", "exported.db");
        await sourceStore.ExportAsync(
            buildResult.Manifest.PackageId,
            exportPath,
            TestContext.Current.CancellationToken
        );

        var destinationStore = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "destination-packages")),
            NullLogger<SqlitePackageStore>.Instance
        );
        var importedPath = await destinationStore.ImportAsync(
            exportPath,
            TestContext.Current.CancellationToken
        );

        var packages = await destinationStore.ListAsync(TestContext.Current.CancellationToken);
        packages.ShouldHaveSingleItem();
        File.Exists(importedPath).ShouldBeTrue();

        var response = await destinationStore.QueryAsync(
            new DocsQueryRequest(buildResult.Manifest.PackageId, "Content"),
            TestContext.Current.CancellationToken
        );
        response.Hits.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Should_Remove_Package_File()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "page.md"),
            "# Page\n\nContent.",
            TestContext.Current.CancellationToken
        );

        var builder = new DocumentPackageBuilder(
            new SourceDetector(),
            new TestHttpClientFactory(),
            NullLogger<DocumentPackageBuilder>.Instance
        );
        var buildResult = await builder.BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "packages")),
            NullLogger<SqlitePackageStore>.Instance
        );
        var packagePath = await store.SaveAsync(buildResult, TestContext.Current.CancellationToken);

        var removed = await store.RemoveAsync(
            buildResult.Manifest.PackageId,
            cancellationToken: TestContext.Current.CancellationToken
        );

        removed.ShouldBe(1);
        File.Exists(packagePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_Fill_Hit_Limit_With_Relevant_Chunks_That_Fit_The_Budget()
    {
        var docsPath = Path.Combine(_tempRoot, "budget-docs");
        Directory.CreateDirectory(docsPath);
        foreach (var path in new[] { "a.md", "b.md", "c.md" })
        {
            await File.WriteAllTextAsync(
                Path.Combine(docsPath, path),
                "# Budget\n\nBudget guidance.",
                TestContext.Current.CancellationToken
            );
        }

        var builder = new DocumentPackageBuilder(
            new SourceDetector(),
            new TestHttpClientFactory(),
            NullLogger<DocumentPackageBuilder>.Instance
        );
        var buildResult = await builder.BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );
        buildResult = buildResult with
        {
            Chunks = buildResult.Chunks
                .Select(chunk => chunk with { TokenEstimate = chunk.Path == "b.md" ? 100 : 10 })
                .ToArray(),
        };
        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "budget-packages")),
            NullLogger<SqlitePackageStore>.Instance
        );
        await store.SaveAsync(buildResult, TestContext.Current.CancellationToken);

        var response = await store.QueryAsync(
            new DocsQueryRequest(
                buildResult.Manifest.PackageId,
                "budget",
                new RetrievalOptions(MaxTokens: 20, MaxHits: 2)
            ),
            TestContext.Current.CancellationToken
        );

        response.Hits.Select(hit => hit.Path).ToArray().ShouldBe(new[] { "a.md", "c.md" });
        response.TotalTokens.ShouldBe(20);
    }

    [Fact]
    public async Task Should_Trim_Query_Hits_To_Token_Budget()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "long.md"),
            "# Section 1\n\n" + new string('a', 400) + "\n\n# Section 2\n\n" + new string('b', 400),
            TestContext.Current.CancellationToken
        );

        var builder = new DocumentPackageBuilder(
            new SourceDetector(),
            new TestHttpClientFactory(),
            NullLogger<DocumentPackageBuilder>.Instance
        );
        var buildResult = await builder.BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "packages")),
            NullLogger<SqlitePackageStore>.Instance
        );
        await store.SaveAsync(buildResult, TestContext.Current.CancellationToken);

        var response = await store.QueryAsync(
            new DocsQueryRequest(
                buildResult.Manifest.PackageId,
                "section",
                new RetrievalOptions(MaxTokens: 150, MaxHits: 10, RelativeScoreCutoff: 0.1)
            ),
            TestContext.Current.CancellationToken
        );

        response.Hits.Count.ShouldBeGreaterThanOrEqualTo(1);
        response.TotalTokens.ShouldBeLessThanOrEqualTo(150);
    }

    [Fact]
    public async Task Should_Return_The_Best_Hit_Even_When_It_Exceeds_The_Token_Budget()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "long.md"),
            "# Oversized\n\n" + new string('a', 2_000),
            TestContext.Current.CancellationToken
        );

        var builder = new DocumentPackageBuilder(
            new SourceDetector(),
            new TestHttpClientFactory(),
            NullLogger<DocumentPackageBuilder>.Instance
        );
        var buildResult = await builder.BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_tempRoot, "packages")),
            NullLogger<SqlitePackageStore>.Instance
        );
        await store.SaveAsync(buildResult, TestContext.Current.CancellationToken);

        // The only section is far larger than the budget. Stopping before it would answer with nothing at
        // all, so the best hit is returned whole and the reported total shows the overspend.
        var response = await store.QueryAsync(
            new DocsQueryRequest(
                buildResult.Manifest.PackageId,
                "oversized",
                new RetrievalOptions(MaxTokens: 10, MaxHits: 8, RelativeScoreCutoff: 0.1)
            ),
            TestContext.Current.CancellationToken
        );

        response.Hits.ShouldHaveSingleItem();
        response.TotalTokens.ShouldBeGreaterThan(10);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient(new TestMessageHandler());
    }

    private sealed class TestMessageHandler : HttpMessageHandler
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
