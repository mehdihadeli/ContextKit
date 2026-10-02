using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroundKit.Storage.Sqlite.Tests.Unit;

public sealed class SqlitePackageStoreTests : IDisposable
{
    private readonly string _rootPath;

    public SqlitePackageStoreTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_rootPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task Should_Create_Package_File_When_Saving()
    {
        var store = CreateStore();
        var buildResult = CreateBuildResult("test-package");

        var path = await store.SaveAsync(buildResult, TestContext.Current.CancellationToken);

        File.Exists(path).ShouldBeTrue();
        path.ShouldContain(_rootPath);
    }

    [Fact]
    public async Task Should_List_Saved_Package()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateBuildResult("test-package"), TestContext.Current.CancellationToken);

        var packages = await store.ListAsync(TestContext.Current.CancellationToken);

        packages.ShouldHaveSingleItem();
        packages[0].PackageId.ShouldBe("test-package");
    }

    [Fact]
    public async Task Should_Return_Summary_For_Existing_Package()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateBuildResult("test-package"), TestContext.Current.CancellationToken);

        var package = await store.GetPackageAsync("test-package", TestContext.Current.CancellationToken);

        package.ShouldNotBeNull();
        package.PackageId.ShouldBe("test-package");
    }

    [Fact]
    public async Task Should_Return_Null_For_Missing_Package()
    {
        var store = CreateStore();

        var package = await store.GetPackageAsync("missing", TestContext.Current.CancellationToken);

        package.ShouldBeNull();
    }

    [Fact]
    public async Task Should_Delete_Existing_Package_File()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateBuildResult("test-package"), TestContext.Current.CancellationToken);

        var removed = await store.RemoveAsync(
            "test-package",
            cancellationToken: TestContext.Current.CancellationToken
        );

        removed.ShouldBe(1);
        (await store.ListAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_Delete_Only_Requested_Package_Version()
    {
        var store = CreateStore();
        await store.SaveAsync(
            CreateBuildResult("test-package", version: "1.0.0"),
            TestContext.Current.CancellationToken
        );
        await store.SaveAsync(
            CreateBuildResult("test-package", version: "2.0.0"),
            TestContext.Current.CancellationToken
        );

        var removed = await store.RemoveAsync(
            "test-package",
            "1.0.0",
            TestContext.Current.CancellationToken
        );

        removed.ShouldBe(1);
        var remaining = await store.ListAsync(TestContext.Current.CancellationToken);
        remaining.ShouldHaveSingleItem();
        remaining[0].Version.ShouldBe("2.0.0");
    }

    [Fact]
    public async Task Should_Copy_Package_File_When_Exporting()
    {
        var store = CreateStore();
        var path = await store.SaveAsync(
            CreateBuildResult("test-package"),
            TestContext.Current.CancellationToken
        );
        var destination = Path.Combine(_rootPath, "exports");

        var exportPath = await store.ExportAsync(
            "test-package",
            destination,
            TestContext.Current.CancellationToken
        );

        File.Exists(exportPath).ShouldBeTrue();
        Path.GetFileName(exportPath).ShouldBe(Path.GetFileName(path));
    }

    [Fact]
    public async Task Should_Copy_Imported_Package_Into_Store()
    {
        var store = CreateStore();
        var path = await store.SaveAsync(
            CreateBuildResult("test-package"),
            TestContext.Current.CancellationToken
        );
        var importSource = Path.Combine(_rootPath, "import", Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(importSource)!);
        File.Copy(path, importSource);

        var importedPath = await store.ImportAsync(
            importSource,
            TestContext.Current.CancellationToken
        );

        File.Exists(importedPath).ShouldBeTrue();
        importedPath.ShouldContain(_rootPath);
    }

    [Fact]
    public async Task Should_Return_Hits_For_Existing_Package_Query()
    {
        var store = CreateStore();
        await store.SaveAsync(
            CreateBuildResult("test-package", "# Getting Started\n\nUse refresh to rebuild."),
            TestContext.Current.CancellationToken
        );

        var response = await store.QueryAsync(
            new DocsQueryRequest("test-package", "refresh"),
            TestContext.Current.CancellationToken
        );

        response.Hits.ShouldNotBeEmpty();
        response.TotalTokens.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_Reject_Query_For_Missing_Package()
    {
        var store = CreateStore();

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.QueryAsync(
                new DocsQueryRequest("missing", "topic"),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task Should_Return_Source_For_Existing_Package()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateBuildResult("test-package"), TestContext.Current.CancellationToken);

        var source = await store.GetSourceAsync("test-package", TestContext.Current.CancellationToken);

        source.ShouldNotBeNull();
        source.Kind.ShouldBe(SourceKind.LocalDirectory);
    }

    private SqlitePackageStore CreateStore() =>
        new(new PackageStoreOptions(_rootPath), NullLogger<SqlitePackageStore>.Instance);

    private static BuildResult CreateBuildResult(
        string packageId,
        string content = "# Title\n\nBody.",
        string version = "dev"
    )
    {
        var source = new DocumentationSource(
            SourceKind.LocalDirectory,
            packageId,
            packageId,
            "C:/test"
        );
        var document = new DocumentRecord("doc-1", "Title", "title.md", content);
        var chunk = new ChunkRecord(
            "chunk-1",
            "doc-1",
            "Title",
            "Title",
            "title.md",
            content,
            10,
            false,
            "hash-1",
            0
        );

        return new BuildResult(
            source,
            new PackageManifest(
                packageId,
                packageId,
                version,
                SourceKind.LocalDirectory,
                "C:/test",
                packageId,
                "fingerprint",
                DateTimeOffset.UtcNow,
                "1.0.0",
                1,
                1,
                0
            ),
            [],
            [document],
            [chunk]
        );
    }
}
