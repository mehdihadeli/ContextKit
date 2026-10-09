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
    public async Task Should_Download_Missing_Package_Before_Querying()
    {
        var store = CreateStoreWithoutPackages();
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "react", null, Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("react", "npm", "latest", "UI library", 1_024)]);
        var downloader = Substitute.For<IPackageDownloadService>();
        downloader
            .InstallRegistryPackageAsync("npm", "react", "latest", Arg.Any<CancellationToken>())
            .Returns("C:/packages/react@latest.db");
        store
            .ListAsync(Arg.Any<CancellationToken>())
            .Returns(
                [
                    new PackageSummary(
                        "react",
                        "React",
                        "latest",
                        3,
                        9,
                        DateTimeOffset.UtcNow,
                        "C:/packages/react@latest.db"
                    ),
                ]
            );
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync(["query", "react", "useEffect"]);

        exitCode.ShouldBe(0);
        await downloader
            .Received(1)
            .InstallRegistryPackageAsync("npm", "react", "latest", Arg.Any<CancellationToken>());
        await store.Received(1)
            .QueryAsync(
                Arg.Is<DocsQueryRequest>(request =>
                    request.PackageId == "react@latest" && request.Topic == "useEffect"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Query_Installed_Package_Without_Contacting_The_Registry()
    {
        var source = CreateSource();
        var store = new RecordingPackageStore(source, "C:/packages/docs@dev.db");
        var registryClient = Substitute.For<IContextRegistryClient>();
        var downloader = Substitute.For<IPackageDownloadService>();
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(source)),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync(["query", "react", "useEffect"]);

        exitCode.ShouldBe(0);
        await registryClient
            .DidNotReceiveWithAnyArgs()
            .SearchAsync(default!, default!, default, Arg.Any<CancellationToken>());
        await downloader
            .DidNotReceiveWithAnyArgs()
            .InstallRegistryPackageAsync(
                default!,
                default!,
                default,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Guide_To_Add_When_Registry_Has_No_Match()
    {
        var store = CreateStoreWithoutPackages();
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "left-pad", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var downloader = Substitute.For<IPackageDownloadService>();
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync(["query", "left-pad", "install"]);

        exitCode.ShouldBe(1);
        await downloader
            .DidNotReceiveWithAnyArgs()
            .InstallRegistryPackageAsync(
                default!,
                default!,
                default,
                Arg.Any<CancellationToken>()
            );
        await store
            .DidNotReceiveWithAnyArgs()
            .QueryAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Not_Download_When_No_Install_Is_Requested()
    {
        var store = CreateStoreWithoutPackages();
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "react", null, Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("react", "npm", "latest", "UI library", 1_024)]);
        var downloader = Substitute.For<IPackageDownloadService>();
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync([
            "query",
            "react",
            "useEffect",
            "--no-install",
        ]);

        exitCode.ShouldBe(1);
        await downloader
            .DidNotReceiveWithAnyArgs()
            .InstallRegistryPackageAsync(
                default!,
                default!,
                default,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Download_Requested_Version_Of_Missing_Package()
    {
        var store = CreateStoreWithoutPackages();
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "react", "18.2.0", Arg.Any<CancellationToken>())
            .Returns(
                [
                    new RegistryPackage("react", "npm", "latest", null, 2_048),
                    new RegistryPackage("react", "npm", "18.2.0", null, 1_024),
                ]
            );
        var downloader = Substitute.For<IPackageDownloadService>();
        downloader
            .InstallRegistryPackageAsync("npm", "react", "18.2.0", Arg.Any<CancellationToken>())
            .Returns("C:/packages/react@18.2.0.db");
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync(["query", "react@18.2.0", "useEffect"]);

        exitCode.ShouldBe(0);
        await downloader
            .Received(1)
            .InstallRegistryPackageAsync("npm", "react", "18.2.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Keep_Scoped_Package_Name_Out_Of_The_Registry_Segment()
    {        var store = CreateStoreWithoutPackages();
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "@angular/core", "21.0.3", Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("@angular/core", "npm", "21.0.3", null, 512)]);
        var downloader = Substitute.For<IPackageDownloadService>();
        downloader
            .InstallRegistryPackageAsync(
                "npm",
                "@angular/core",
                "21.0.3",
                Arg.Any<CancellationToken>()
            )
            .Returns("C:/packages/core@21.0.3.db");
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync(["query", "@angular/core@21.0.3", "lifecycle"]);

        exitCode.ShouldBe(0);
        await registryClient
            .Received(1)
            .SearchAsync("npm", "@angular/core", "21.0.3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Should_Parse_Package_Selectors()
    {
        CliApplication.ParsePackageSelector("react").ShouldBe(("npm", "react", null));
        CliApplication.ParsePackageSelector("npm/react").ShouldBe(("npm", "react", null));
        CliApplication.ParsePackageSelector("pip/django@5.1").ShouldBe(("pip", "django", "5.1"));
        CliApplication
            .ParsePackageSelector("@angular/core@21.0.3")
            .ShouldBe(("npm", "@angular/core", "21.0.3"));
    }

    [Fact]
    public async Task Should_Resolve_A_Partial_Version_Against_The_Registry()
    {
        var store = CreateStoreWithoutPackages();
        var registryClient = Substitute.For<IContextRegistryClient>();

        // The registry matches versions exactly, so the first lookup for `18` finds nothing. The
        // second asks for every published version and the resolver picks 18.2.0 out of them.
        registryClient
            .SearchAsync("npm", "react", "18", Arg.Any<CancellationToken>())
            .Returns([]);
        registryClient
            .SearchAsync("npm", "react", null, Arg.Any<CancellationToken>())
            .Returns(
                [
                    new RegistryPackage("react", "npm", "latest", null, 2_048),
                    new RegistryPackage("react", "npm", "18.2.0", null, 1_024),
                ]
            );
        var downloader = Substitute.For<IPackageDownloadService>();
        downloader
            .InstallRegistryPackageAsync("npm", "react", "18.2.0", Arg.Any<CancellationToken>())
            .Returns("C:/packages/react@18.2.0.db");
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync(["query", "react@18", "useEffect"]);

        exitCode.ShouldBe(0);
        await downloader
            .Received(1)
            .InstallRegistryPackageAsync("npm", "react", "18.2.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Not_Download_A_Different_Major_Line_When_The_Requested_One_Is_Absent()
    {
        var store = CreateStoreWithoutPackages();
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "angular", "18", Arg.Any<CancellationToken>())
            .Returns([]);
        registryClient
            .SearchAsync("npm", "angular", null, Arg.Any<CancellationToken>())
            .Returns(
                [
                    new RegistryPackage("angular", "npm", "21.0.3", null, 4_096),
                    new RegistryPackage("angular", "npm", "19.2.17", null, 2_048),
                ]
            );
        var downloader = Substitute.For<IPackageDownloadService>();
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync(["query", "angular@18", "lifecycle"]);

        // Silently fetching 21.0.3 would answer a question nobody asked.
        exitCode.ShouldBe(1);
        await downloader
            .DidNotReceiveWithAnyArgs()
            .InstallRegistryPackageAsync(
                default!,
                default!,
                default,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Prefer_The_Installed_Package_Over_The_Registry()
    {
        var store = Substitute.For<IPackageStore>();
        store
            .GetPackageAsync("react@18", Arg.Any<CancellationToken>())
            .Returns(
                new PackageSummary(
                    "react",
                    "React",
                    "18.2.0",
                    10,
                    40,
                    DateTimeOffset.UtcNow,
                    "C:/packages/react@18.2.0.db"
                )
            );
        var registryClient = Substitute.For<IContextRegistryClient>();
        var downloader = Substitute.For<IPackageDownloadService>();
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
                new DocsQueryResponse(call.Arg<DocsQueryRequest>().PackageId, "18.2.0", [], 0)
            );
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync(["query", "react@18", "useEffect"]);

        exitCode.ShouldBe(0);
        await registryClient
            .DidNotReceiveWithAnyArgs()
            .SearchAsync(default!, default!, default, Arg.Any<CancellationToken>());
        await store
            .Received(1)
            .QueryAsync(
                Arg.Is<DocsQueryRequest>(request =>
                    request.PackageId == "react@18.2.0" && request.Topic == "useEffect"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public void Should_Apply_Registry_Url_Override_Before_Services_Are_Built()
    {
        const string registryUrl = "https://registry.example.com/index.json";
        var previousRegistryUrl = Environment.GetEnvironmentVariable("GROUNDKIT_REGISTRY_URL");
        try
        {
            Environment.SetEnvironmentVariable("GROUNDKIT_REGISTRY_URL", null);

            var remaining = CliApplication.ApplyRegistryUrlOverride([
                "search-packages",
                "npm",
                "react",
                "--registry-url",
                registryUrl,
            ]);

            // The option must be consumed here, because `GroundKitOptions` is resolved eagerly by
            // `AddGroundKitServices` and the registry client reads its endpoint from it.
            Environment.GetEnvironmentVariable("GROUNDKIT_REGISTRY_URL").ShouldBe(registryUrl);
            remaining.ShouldBe(["search-packages", "npm", "react"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROUNDKIT_REGISTRY_URL", previousRegistryUrl);
        }
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

    [Fact]
    public async Task Should_Detect_Package_From_Question_And_Download_Before_Querying()
    {
        var store = CreateStoreWithoutPackages();
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "angular", null, Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("angular", "npm", "21.0.3", "Web framework", 1_024)]);
        var downloader = Substitute.For<IPackageDownloadService>();
        downloader
            .InstallRegistryPackageAsync("npm", "angular", "21.0.3", Arg.Any<CancellationToken>())
            .Returns("C:/packages/angular@21.0.3.db");
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync(["query", "what are components in angular?"]);

        exitCode.ShouldBe(0);
        await downloader
            .Received(1)
            .InstallRegistryPackageAsync("npm", "angular", "21.0.3", Arg.Any<CancellationToken>());
        await store
            .Received(1)
            .QueryAsync(
                Arg.Is<DocsQueryRequest>(request =>
                    request.PackageId == "angular"
                    && request.Topic == "what are components in angular?"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Answer_Question_From_Installed_Package_Without_The_Registry()
    {
        var source = new DocumentationSource(
            SourceKind.LocalDirectory,
            "angular",
            "Angular",
            "C:/angular"
        );
        var store = new RecordingPackageStore(source, "C:/packages/angular@dev.db");
        var registryClient = Substitute.For<IContextRegistryClient>();
        var downloader = Substitute.For<IPackageDownloadService>();
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(source)),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync(["query", "what are components in angular?"]);

        exitCode.ShouldBe(0);
        await registryClient
            .DidNotReceiveWithAnyArgs()
            .SearchAsync(default!, default!, default, Arg.Any<CancellationToken>());
        await downloader
            .DidNotReceiveWithAnyArgs()
            .InstallRegistryPackageAsync(
                default!,
                default!,
                default,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Guide_When_Question_Names_No_Known_Package()
    {
        var store = CreateStoreWithoutPackages();
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store
        );

        var exitCode = await application.RunAsync([
            "query",
            "how do i configure the flux capacitor?",
        ]);

        exitCode.ShouldBe(1);
        await store
            .DidNotReceiveWithAnyArgs()
            .QueryAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Query_An_Installed_Package_That_Has_No_Version()
    {
        // Regression: a package built from a local folder carries no version. The selector must stay
        // the bare id, because the store has no version to resolve and "angular@dev" would miss.
        var store = Substitute.For<IPackageStore>();
        store
            .ListAsync(Arg.Any<CancellationToken>())
            .Returns(
                [
                    new PackageSummary(
                        "angular",
                        "Angular",
                        null,
                        1,
                        1,
                        DateTimeOffset.UtcNow,
                        "/packages/angular@dev.db"
                    ),
                ]
            );
        store
            .GetPackageAsync("angular", Arg.Any<CancellationToken>())
            .Returns(
                new PackageSummary(
                    "angular",
                    "Angular",
                    null,
                    1,
                    1,
                    DateTimeOffset.UtcNow,
                    "/packages/angular@dev.db"
                )
            );
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new DocsQueryResponse(call.Arg<DocsQueryRequest>().PackageId, null, [], 0));
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store
        );

        var exitCode = await application.RunAsync(["query", "what are components in angular?"]);

        exitCode.ShouldBe(0);
        await store
            .Received(1)
            .QueryAsync(
                Arg.Is<DocsQueryRequest>(request =>
                    request.PackageId == "angular"
                    && request.Topic == "what are components in angular?"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Guide_When_Detected_Package_Is_Not_Published()
    {
        var store = CreateStoreWithoutPackages();
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "vue", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var downloader = Substitute.For<IPackageDownloadService>();
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync(["query", "composition api in vue"]);

        exitCode.ShouldBe(1);
        await downloader
            .DidNotReceiveWithAnyArgs()
            .InstallRegistryPackageAsync(
                default!,
                default!,
                default,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Guide_When_The_Registry_Is_Unavailable_For_A_Detected_Package()
    {
        var store = CreateStoreWithoutPackages();
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "angular", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RegistryPackage>>(_ =>
                throw new HttpRequestException("registry offline")
            );
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            Substitute.For<IPackageDownloadService>()
        );

        var exitCode = await application.RunAsync(["query", "what are components in angular?"]);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Should_Not_Download_A_Detected_Package_When_Install_Is_Disabled()
    {
        var store = CreateStoreWithoutPackages();
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "angular", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("angular", "npm", "21.0.3", "Web framework", 1_024)]);
        var downloader = Substitute.For<IPackageDownloadService>();
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient,
            downloader
        );

        var exitCode = await application.RunAsync([
            "query",
            "what are components in angular?",
            "--no-install",
        ]);

        exitCode.ShouldBe(1);
        await downloader
            .DidNotReceiveWithAnyArgs()
            .InstallRegistryPackageAsync(
                default!,
                default!,
                default,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Prefer_The_Detected_Installed_Package_Over_The_Curated_Entry()
    {
        // "angular" is both installed and curated. The installed copy must answer, with no lookup.
        var store = Substitute.For<IPackageStore>();
        store
            .ListAsync(Arg.Any<CancellationToken>())
            .Returns(
                [
                    new PackageSummary(
                        "angular",
                        "Angular",
                        "21.0.3",
                        1,
                        1,
                        DateTimeOffset.UtcNow,
                        "/packages/angular@21.0.3.db"
                    ),
                ]
            );
        store
            .GetPackageAsync("angular", Arg.Any<CancellationToken>())
            .Returns(
                new PackageSummary(
                    "angular",
                    "Angular",
                    "21.0.3",
                    1,
                    1,
                    DateTimeOffset.UtcNow,
                    "/packages/angular@21.0.3.db"
                )
            );
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new DocsQueryResponse(call.Arg<DocsQueryRequest>().PackageId, "21.0.3", [], 0));
        var registryClient = Substitute.For<IContextRegistryClient>();
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store,
            registryClient
        );

        var exitCode = await application.RunAsync(["query", "what are components in angular?"]);

        exitCode.ShouldBe(0);
        await registryClient
            .DidNotReceiveWithAnyArgs()
            .SearchAsync(default!, default!, default, Arg.Any<CancellationToken>());
        await store
            .Received(1)
            .QueryAsync(
                Arg.Is<DocsQueryRequest>(request => request.PackageId == "angular@21.0.3"),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Keep_The_Explicit_Two_Argument_Form_Unchanged()
    {
        // "react" followed by a topic that itself names another package must not re-detect.
        var store = CreateStoreWithoutPackages();
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store
        );

        var exitCode = await application.RunAsync([
            "query",
            "react",
            "how do hooks differ from vue",
        ]);

        exitCode.ShouldBe(1);
        await store
            .Received(1)
            .GetPackageAsync("react", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Require_A_Question_Or_A_Package()
    {
        var store = CreateStoreWithoutPackages();
        var application = new CliApplication(
            new RecordingPackageBuilder(CreateBuildResult(CreateSource())),
            store
        );

        var exitCode = await application.RunAsync(["query"]);

        exitCode.ShouldBe(1);
    }

    private static DocumentationSource CreateSource() =>
        new(SourceKind.LocalDirectory, "docs", "Docs", "C:/docs");

    /// <summary>
    /// A store with nothing installed, so every query selector takes the not-installed path.
    /// Queries still answer, which lets a test assert what the query was finally resolved to.
    /// </summary>
    private static IPackageStore CreateStoreWithoutPackages()
    {
        var store = Substitute.For<IPackageStore>();
        store
            .GetPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PackageSummary?)null);
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new DocsQueryResponse(call.Arg<DocsQueryRequest>().PackageId, "latest", [], 0));
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        return store;
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
