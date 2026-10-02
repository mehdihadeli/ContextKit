using GroundKit.Cli;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GroundKit.Tests.Unit;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task Should_Show_Help_Without_Arguments()
    {
        var application = CreateApplication();

        var exitCode = await application.RunAsync([]);

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Should_Reject_Unknown_Command()
    {
        var application = CreateApplication();

        var exitCode = await application.RunAsync(["unknown"]);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Should_Reject_Add_Without_Source()
    {
        var application = CreateApplication();

        var exitCode = await application.RunAsync(["add"]);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Should_Build_And_Save_Package()
    {
        var source = new DocumentationSource(SourceKind.LocalDirectory, "docs", "Docs", "C:/docs");
        var buildResult = CreateBuildResult(source);
        var builder = new RecordingPackageBuilder(buildResult);
        var store = new RecordingPackageStore(source, "C:/packages/docs@dev.db");
        var application = new CliApplication(builder, store);

        var exitCode = await application.RunAsync(["add", "C:/docs"]);

        exitCode.ShouldBe(0);
        builder.Input.ShouldBe("C:/docs");
        builder.DocsPath.ShouldBeNull();
        store.SaveCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Should_Add_Remote_Package_Url_Through_Download_Service()
    {
        const string packageUrl = "https://localhost/mattpocock-skills@1.2.3";
        var source = new DocumentationSource(SourceKind.RawPage, "source", "Source", packageUrl);
        var builder = new RecordingPackageBuilder(CreateBuildResult(source));
        var store = new RecordingPackageStore(source, "C:/packages/mattpocock-skills@1.2.3.db");
        var downloader = Substitute.For<IPackageDownloadService>();
        downloader
            .InstallAsync(packageUrl, null, Arg.Any<CancellationToken>())
            .Returns("C:/packages/mattpocock-skills@1.2.3.db");
        var application = new CliApplication(builder, store, packageDownloadService: downloader);

        var exitCode = await application.RunAsync(["add", packageUrl]);

        exitCode.ShouldBe(0);
        builder.Input.ShouldBeNull();
        store.SaveCallCount.ShouldBe(0);
        await downloader.Received(1).InstallAsync(packageUrl, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Apply_Per_Command_Registry_Url_To_Install()
    {
        const string registryUrl = "https://registry.example.com";
        var previousRegistryUrl = Environment.GetEnvironmentVariable("GROUNDKIT_REGISTRY_URL");
        try
        {
            Environment.SetEnvironmentVariable("GROUNDKIT_REGISTRY_URL", null);
            var source = new DocumentationSource(SourceKind.LocalDirectory, "docs", "Docs", "C:/docs");
            var downloader = Substitute.For<IPackageDownloadService>();
            downloader
                .InstallAsync("npm/react", null, Arg.Any<CancellationToken>())
                .Returns("C:/packages/react@latest.db");
            var application = new CliApplication(
                new RecordingPackageBuilder(CreateBuildResult(source)),
                new RecordingPackageStore(source, "C:/packages/docs@dev.db"),
                packageDownloadService: downloader
            );

            var exitCode = await application.RunAsync(
                ["install", "npm/react", "--registry-url", registryUrl]
            );

            exitCode.ShouldBe(0);
            Environment.GetEnvironmentVariable("GROUNDKIT_REGISTRY_URL").ShouldBe(registryUrl);
            await downloader.Received(1).InstallAsync("npm/react", null, Arg.Any<CancellationToken>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROUNDKIT_REGISTRY_URL", previousRegistryUrl);
        }
    }

    [Fact]
    public async Task Should_Search_Packages_Through_Registry_Client()
    {
        var source = new DocumentationSource(SourceKind.LocalDirectory, "docs", "Docs", "C:/docs");
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "react", "19.1.0", Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("react", "npm", "19.1.0", "UI library", 42)]);
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(source)),
            new RecordingPackageStore(source, "C:/packages/docs@dev.db"),
            registryClient
        );

        var exitCode = await application.RunAsync(["search-packages", "npm", "react", "19.1.0"]);

        exitCode.ShouldBe(0);
        await registryClient.Received(1)
            .SearchAsync("npm", "react", "19.1.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Download_Exact_Package_Through_Registry_Service()
    {
        var source = new DocumentationSource(SourceKind.LocalDirectory, "docs", "Docs", "C:/docs");
        var downloader = Substitute.For<IPackageDownloadService>();
        downloader
            .InstallRegistryPackageAsync("pip", "django", "5.1", Arg.Any<CancellationToken>())
            .Returns("C:/packages/django@5.1.db");
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(source)),
            new RecordingPackageStore(source, "C:/packages/docs@dev.db"),
            packageDownloadService: downloader
        );

        var exitCode = await application.RunAsync(["download-package", "pip", "django", "5.1"]);

        exitCode.ShouldBe(0);
        await downloader.Received(1)
            .InstallRegistryPackageAsync("pip", "django", "5.1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Add_Local_Package_File_Through_Download_Service()
    {
        var packagePath = Path.Combine(
            Path.GetTempPath(),
            $"mattpocock-skills@1.2.3-{Guid.NewGuid():N}.db"
        );
        await File.WriteAllBytesAsync(
            packagePath,
            [1, 2, 3],
            TestContext.Current.CancellationToken
        );
        try
        {
            var source = new DocumentationSource(
                SourceKind.RawPage,
                "source",
                "Source",
                packagePath
            );
            var builder = new RecordingPackageBuilder(CreateBuildResult(source));
            var store = new RecordingPackageStore(source, "C:/packages/mattpocock-skills@1.2.3.db");
            var downloader = Substitute.For<IPackageDownloadService>();
            downloader
                .InstallAsync(packagePath, null, Arg.Any<CancellationToken>())
                .Returns("C:/packages/mattpocock-skills@1.2.3.db");
            var application = new CliApplication(
                builder,
                store,
                packageDownloadService: downloader
            );

            var exitCode = await application.RunAsync(["add", packagePath]);

            exitCode.ShouldBe(0);
            builder.Input.ShouldBeNull();
            store.SaveCallCount.ShouldBe(0);
            await downloader
                .Received(1)
                .InstallAsync(packagePath, null, Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task Should_Pass_Docs_Path_To_Add()
    {
        var source = new DocumentationSource(
            SourceKind.LocalDirectory,
            "docs",
            "Docs",
            "C:/docs",
            "src"
        );
        var buildResult = CreateBuildResult(source);
        var builder = new RecordingPackageBuilder(buildResult);
        var store = new RecordingPackageStore(source, "C:/packages/docs@dev.db");
        var application = new CliApplication(builder, store);

        var exitCode = await application.RunAsync(["add", "C:/docs", "--docs-path", "src"]);

        exitCode.ShouldBe(0);
        builder.Input.ShouldBe("C:/docs");
        builder.DocsPath.ShouldBe("src");
    }

    [Fact]
    public async Task Should_Pass_Git_Ref_To_Add()
    {
        var source = new DocumentationSource(
            SourceKind.GitRepository,
            "docs",
            "Docs",
            "https://github.com/org/docs"
        );
        var builder = new RecordingPackageBuilder(CreateBuildResult(source));
        var store = new RecordingPackageStore(source, "C:/packages/docs@dev.db");
        var application = new CliApplication(builder, store);

        var exitCode = await application.RunAsync(
            ["add", "https://github.com/org/docs", "--tag", "v1.2.3"]
        );

        exitCode.ShouldBe(0);
        builder.GitRef.ShouldBe("v1.2.3");
    }

    [Fact]
    public async Task Should_List_Without_Packages()
    {
        var application = CreateApplication();

        var exitCode = await application.RunAsync(["list"]);

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Should_List_Without_Packages_Using_Flag()
    {
        var application = CreateApplication();

        var exitCode = await application.RunAsync(["--list"]);

        exitCode.ShouldBe(0);
    }

    [Theory]
    [InlineData("a", "add")]
    [InlineData("ls", "list")]
    [InlineData("l", "list")]
    [InlineData("ins", "inspect")]
    [InlineData("q", "query")]
    [InlineData("rf", "refresh")]
    [InlineData("rm", "remove")]
    [InlineData("sp", "search-packages")]
    [InlineData("dp", "download-package")]
    [InlineData("i", "install")]
    [InlineData("c", "catalog")]
    public void Should_Normalize_Short_Command_Aliases(string alias, string expected)
    {
        CliApplication.NormalizeCommand(alias).ShouldBe(expected);
    }

    [Fact]
    public void Should_Normalize_Short_Option_Aliases()
    {
        var normalized = CliApplication.NormalizeArguments(
            [
                "a",
                "./docs",
                "-p",
                "src",
                "-n",
                "react",
                "-v",
                "19.0.0",
                "-s",
                "./artifacts",
                "-t",
                "v19.0.0",
                "-c",
            ]
        );

        normalized.ShouldBe(
            [
                "add",
                "./docs",
                "--path",
                "src",
                "--name",
                "react",
                "--pkg-version",
                "19.0.0",
                "--save",
                "./artifacts",
                "--tag",
                "v19.0.0",
                "--choose-tag",
            ]
        );
    }

    [Fact]
    public async Task Should_Reject_Remove_Without_Package_Id()
    {
        var application = CreateApplication();

        var exitCode = await application.RunAsync(["remove"]);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Should_Refresh_Using_Stored_Source_Metadata()
    {
        var source = new DocumentationSource(
            SourceKind.GitRepository,
            "groundkit",
            "GroundKit",
            "https://example.invalid/groundkit.git",
            "docs",
            "v1.2.3",
            "v1.2.3",
            "main",
            "fingerprint-123",
            DateTimeOffset.Parse("2026-07-01T00:00:00Z")
        );
        var buildResult = CreateBuildResult(source);
        var builder = new RecordingPackageBuilder(buildResult);
        var store = new RecordingPackageStore(source, "/tmp/groundkit/groundkit@v1.2.3.db");
        var application = new CliApplication(builder, store);

        var exitCode = await application.RunAsync(["refresh", "groundkit"]);

        exitCode.ShouldBe(0);
        builder.Input.ShouldBe("https://example.invalid/groundkit.git");
        builder.DocsPath.ShouldBe("docs");
        store.SaveCallCount.ShouldBe(1);
        store.LastSavedPackageId.ShouldBe("groundkit");
    }

    [Fact]
    public async Task Should_Export_Using_Package_Store()
    {
        var source = new DocumentationSource(
            SourceKind.LocalDirectory,
            "groundkit",
            "GroundKit",
            "C:/groundkit/docs"
        );
        var builder = new RecordingPackageBuilder(CreateBuildResult(source));
        var store = new RecordingPackageStore(source, "/tmp/groundkit/groundkit@dev.db");
        var application = new CliApplication(builder, store);

        var exitCode = await application.RunAsync(["export", "groundkit", "./artifacts"]);

        exitCode.ShouldBe(0);
        store.ExportPackageId.ShouldBe("groundkit");
        store.ExportDestination.ShouldBe("./artifacts");
    }

    [Fact]
    public async Task Should_Inspect_Package_And_Source_Metadata()
    {
        var source = new DocumentationSource(
            SourceKind.GitRepository,
            "groundkit",
            "GroundKit",
            "https://example.invalid/groundkit.git",
            "docs",
            "v1.2.3"
        );
        var builder = new RecordingPackageBuilder(CreateBuildResult(source));
        var store = new RecordingPackageStore(source, "/tmp/groundkit/groundkit@v1.2.3.db");
        var application = new CliApplication(builder, store);

        var exitCode = await application.RunAsync(["inspect", "groundkit"]);

        exitCode.ShouldBe(0);
        store.LastRequestedPackageId.ShouldBe("groundkit");
        store.GetPackageCallCount.ShouldBe(1);
        store.GetSourceCallCount.ShouldBe(1);
    }

    private static CliApplication CreateApplication()
    {
        var source = new DocumentationSource(SourceKind.LocalDirectory, "docs", "Docs", "C:/docs");
        return new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(source)),
            new RecordingPackageStore(source, "C:/packages/docs@dev.db")
        );
    }

    private static BuildResult CreateBuildResult(DocumentationSource source)
    {
        return new BuildResult(
            source,
            new PackageManifest(
                source.CanonicalId,
                source.DisplayName,
                source.Version,
                source.Kind,
                source.Location,
                source.CanonicalId,
                source.Fingerprint,
                DateTimeOffset.UtcNow,
                "1.0.0",
                1,
                1,
                0
            ),
            [],
            [new DocumentRecord("doc-1", "Title", "title.md", "# Title")],
            [
                new ChunkRecord(
                    "chunk-1",
                    "doc-1",
                    "Title",
                    "Title",
                    "title.md",
                    "# Title",
                    2,
                    false,
                    "hash",
                    0
                ),
            ]
        );
    }

    private sealed class RecordingPackageBuilder(BuildResult buildResult) : IDocumentPackageBuilder
    {
        public string? Input { get; private set; }
        public string? DocsPath { get; private set; }
        public string? GitRef { get; private set; }
        public string? Version { get; private set; }
        public string? PackageName { get; private set; }

        public Task<BuildResult> BuildAsync(
            string input,
            string? docsPath = null,
            CancellationToken cancellationToken = default
        )
        {
            Input = input;
            DocsPath = docsPath;
            return Task.FromResult(buildResult);
        }

        public Task<BuildResult> BuildAsync(
            string input,
            string? docsPath,
            CancellationToken cancellationToken,
            string? version,
            string? gitRef
        )
        {
            Input = input;
            DocsPath = docsPath;
            GitRef = gitRef;
            Version = version;
            return Task.FromResult(buildResult);
        }

        public Task<BuildResult> BuildAsync(
            string input,
            string? docsPath,
            CancellationToken cancellationToken,
            string? version,
            string? gitRef,
            string? packageName
        )
        {
            Input = input;
            DocsPath = docsPath;
            GitRef = gitRef;
            Version = version;
            PackageName = packageName;
            return Task.FromResult(buildResult);
        }
    }

    private sealed class RecordingPackageStore(DocumentationSource source, string packagePath)
        : IPackageStore
    {
        public int SaveCallCount { get; private set; }
        public string? LastSavedPackageId { get; private set; }
        public string? ExportPackageId { get; private set; }
        public string? ExportDestination { get; private set; }
        public string? LastRequestedPackageId { get; private set; }
        public int GetPackageCallCount { get; private set; }
        public int GetSourceCallCount { get; private set; }
        public string? RemovedPackageId { get; private set; }
        public string? RemovedVersion { get; private set; }

        public Task<string> SaveAsync(
            BuildResult buildResult,
            CancellationToken cancellationToken = default
        )
        {
            SaveCallCount++;
            LastSavedPackageId = buildResult.Manifest.PackageId;
            return Task.FromResult(packagePath);
        }

        public Task<string> ImportAsync(
            string packageFilePath,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(packagePath);

        public Task<string> ExportAsync(
            string packageId,
            string destinationPath,
            CancellationToken cancellationToken = default
        )
        {
            ExportPackageId = packageId;
            ExportDestination = destinationPath;
            return Task.FromResult(Path.Combine(destinationPath, Path.GetFileName(packagePath)));
        }

        public Task<IReadOnlyList<PackageSummary>> ListAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<PackageSummary>>([]);

        public Task<PackageSummary?> GetPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default
        )
        {
            LastRequestedPackageId = packageId;
            GetPackageCallCount++;
            return Task.FromResult<PackageSummary?>(
                new PackageSummary(
                    source.CanonicalId,
                    source.DisplayName,
                    source.Version,
                    1,
                    1,
                    DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
                    packagePath
                )
            );
        }

        public Task<int> RemoveAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(0);

        public Task<int> RemoveAsync(
            string packageId,
            string version,
            CancellationToken cancellationToken = default
        )
        {
            RemovedPackageId = packageId;
            RemovedVersion = version;
            return Task.FromResult(1);
        }

        public Task<DocumentationSource?> GetSourceAsync(
            string packageId,
            CancellationToken cancellationToken = default
        )
        {
            LastRequestedPackageId = packageId;
            GetSourceCallCount++;
            return Task.FromResult<DocumentationSource?>(source);
        }

        public Task<DocsQueryResponse> QueryAsync(
            DocsQueryRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new DocsQueryResponse(request.PackageId, null, [], 0));
    }
}
