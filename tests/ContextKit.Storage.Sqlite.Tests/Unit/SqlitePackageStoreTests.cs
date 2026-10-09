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
    public async Task Should_Name_Imported_Package_After_Its_Identity_Not_The_Source_File()
    {
        var store = CreateStore();
        var path = await store.SaveAsync(
            CreateBuildResult("test-package", version: "1.2.3"),
            TestContext.Current.CancellationToken
        );
        var importSource = Path.Combine(_rootPath, "incoming", $"groundkit-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(importSource)!);
        File.Copy(path, importSource);

        var importedPath = await store.ImportAsync(
            importSource,
            TestContext.Current.CancellationToken
        );

        Path.GetFileName(importedPath).ShouldBe("test-package@1.2.3.db");
        Path.GetDirectoryName(importedPath).ShouldBe(_rootPath);
    }

    [Fact]
    public async Task Should_Keep_One_Entry_When_Importing_Same_Package_Twice()
    {
        var store = CreateStore();
        var path = await store.SaveAsync(
            CreateBuildResult("test-package"),
            TestContext.Current.CancellationToken
        );
        var incoming = Path.Combine(_rootPath, "incoming");
        Directory.CreateDirectory(incoming);
        var firstSource = Path.Combine(incoming, $"groundkit-{Guid.NewGuid():N}.db");
        var secondSource = Path.Combine(incoming, $"groundkit-{Guid.NewGuid():N}.db");
        File.Copy(path, firstSource);
        File.Copy(path, secondSource);

        var firstImport = await store.ImportAsync(
            firstSource,
            TestContext.Current.CancellationToken
        );
        var secondImport = await store.ImportAsync(
            secondSource,
            TestContext.Current.CancellationToken
        );

        firstImport.ShouldBe(secondImport);
        var packages = await store.ListAsync(TestContext.Current.CancellationToken);
        packages.ShouldHaveSingleItem();
        packages[0].PackageId.ShouldBe("test-package");
    }

    [Fact]
    public async Task Should_Keep_Distinct_Entries_When_Importing_Different_Versions()
    {
        var store = CreateStore();
        var first = await store.SaveAsync(
            CreateBuildResult("test-package", version: "1.0.0"),
            TestContext.Current.CancellationToken
        );
        var second = await store.SaveAsync(
            CreateBuildResult("test-package", version: "2.0.0"),
            TestContext.Current.CancellationToken
        );
        var incoming = Path.Combine(_rootPath, "incoming");
        Directory.CreateDirectory(incoming);
        var firstSource = Path.Combine(incoming, $"groundkit-{Guid.NewGuid():N}.db");
        var secondSource = Path.Combine(incoming, $"groundkit-{Guid.NewGuid():N}.db");
        File.Copy(first, firstSource);
        File.Copy(second, secondSource);

        await store.ImportAsync(firstSource, TestContext.Current.CancellationToken);
        await store.ImportAsync(secondSource, TestContext.Current.CancellationToken);

        var packages = await store.ListAsync(TestContext.Current.CancellationToken);
        packages.Count.ShouldBe(2);
        packages.Select(package => package.Version).ShouldBe(["2.0.0", "1.0.0"], ignoreOrder: true);
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

    [Theory]
    [InlineData("19")]
    [InlineData("19.2")]
    [InlineData("v19.2.17")]
    [InlineData("^19")]
    public async Task Should_Resolve_A_Partial_Version_To_A_Stored_Package(string requested)
    {
        var store = CreateStore();
        await store.SaveAsync(
            CreateBuildResult("angular", version: "19.2.17"),
            TestContext.Current.CancellationToken
        );
        await store.SaveAsync(
            CreateBuildResult("angular", version: "21.0.3"),
            TestContext.Current.CancellationToken
        );

        var package = await store.GetPackageAsync(
            $"angular@{requested}",
            TestContext.Current.CancellationToken
        );

        package.ShouldNotBeNull();
        package.Version.ShouldBe("19.2.17");
    }

    [Fact]
    public async Task Should_Not_Resolve_A_Partial_Version_That_Matches_Nothing_Stored()
    {
        var store = CreateStore();
        await store.SaveAsync(
            CreateBuildResult("angular", version: "19.2.17"),
            TestContext.Current.CancellationToken
        );

        var package = await store.GetPackageAsync(
            "angular@18",
            TestContext.Current.CancellationToken
        );

        // Null is what lets the command fall through to the registry instead of guessing.
        package.ShouldBeNull();
    }

    [Fact]
    public async Task Should_Keep_The_Most_Recently_Built_Package_For_A_Bare_Id()
    {
        var store = CreateStore();
        await store.SaveAsync(
            CreateBuildResult("angular", version: "19.2.17"),
            TestContext.Current.CancellationToken
        );
        await Task.Delay(10, TestContext.Current.CancellationToken);
        await store.SaveAsync(
            CreateBuildResult("angular", version: "21.0.3"),
            TestContext.Current.CancellationToken
        );

        var package = await store.GetPackageAsync("angular", TestContext.Current.CancellationToken);

        package.ShouldNotBeNull();
        package.Version.ShouldBe("21.0.3");
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
