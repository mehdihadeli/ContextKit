using System.Text.Json;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace GroundKit.Mcp.Tests.Unit;

public sealed class GroundKitMcpToolsTests
{
    [Fact]
    public async Task Should_Report_Missing_Semantic_Setup_As_Tool_Error()
    {
        var store = Substitute.For<IPackageStore>();
        store.GetPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new PackageSummary("react", "React", null, 1, 1, DateTimeOffset.UtcNow, "react.db"));
        store.QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<DocsQueryResponse>(new InvalidOperationException("ONNX provider missing. Run ck semantic provider install onnx.")));
        var result = await GroundKitMcpTools.QueryDocsAsync("react", "cleanup", store, CreateAgent(), TestContext.Current.CancellationToken, searchMode: "hybrid");
        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain("ck semantic provider install onnx");
        await store.Received(1).QueryAsync(Arg.Is<DocsQueryRequest>(request => request.Options!.SearchMode == SearchMode.Hybrid), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("query-docs", "lexical", SearchMode.Lexical)]
    [InlineData("get_docs", "semantic", SearchMode.Semantic)]
    [InlineData("ask-docs", "hybrid", SearchMode.Hybrid)]
    [InlineData("search-docs", "hybrid", SearchMode.Hybrid)]
    public async Task Should_Forward_Search_Mode_From_Every_Query_Tool(string tool, string mode, SearchMode expected)
    {
        var store = Substitute.For<IPackageStore>();
        var package = new PackageSummary("react", "React", null, 1, 1, DateTimeOffset.UtcNow, "react.db");
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([package]);
        store.GetPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(package);
        store.QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>()).Returns(new DocsQueryResponse("react", null, [], 0));
        var token = TestContext.Current.CancellationToken;
        var result = tool switch
        {
            "query-docs" => await GroundKitMcpTools.QueryDocsAsync("react", "cleanup", store, CreateAgent(), token, searchMode: mode),
            "get_docs" => await GroundKitMcpTools.GetDocsAsync("react", "cleanup", store, CreateAgent(), token, searchMode: mode),
            "ask-docs" => await GroundKitMcpTools.AskDocsAsync("react cleanup", store, CreateAgent(), token, searchMode: mode),
            _ => await GroundKitMcpTools.SearchDocsAsync("react cleanup", store, CreateAgent(), token, searchMode: mode),
        };
        result.IsError.ShouldNotBe(true);
        await store.Received(1).QueryAsync(Arg.Is<DocsQueryRequest>(request => request.Options!.SearchMode == expected), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("query-docs")]
    [InlineData("get_docs")]
    [InlineData("ask-docs")]
    [InlineData("search-docs")]
    public async Task Should_Reject_Unsupported_Mode_On_Every_Query_Tool(string tool)
    {
        var store = Substitute.For<IPackageStore>();
        var token = TestContext.Current.CancellationToken;
        var result = tool switch
        {
            "query-docs" => await GroundKitMcpTools.QueryDocsAsync("react", "cleanup", store, CreateAgent(), token, searchMode: "auto"),
            "get_docs" => await GroundKitMcpTools.GetDocsAsync("react", "cleanup", store, CreateAgent(), token, searchMode: "auto"),
            "ask-docs" => await GroundKitMcpTools.AskDocsAsync("react cleanup", store, CreateAgent(), token, searchMode: "auto"),
            _ => await GroundKitMcpTools.SearchDocsAsync("react cleanup", store, CreateAgent(), token, searchMode: "auto"),
        };
        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain("searchMode");
        await store.DidNotReceiveWithAnyArgs().QueryAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Should_Reject_Empty_Source_Query()
    {
        var result = await GroundKitMcpTools.ResolveSourceAsync(
            "",
            Substitute.For<IPackageStore>(),
            CreateAgent(),
            CancellationToken.None
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain("'query'");
    }

    [Fact]
    public async Task Should_Return_Matching_Source_As_Structured_Content()
    {
        var store = Substitute.For<IPackageStore>();
        store
            .ListAsync(Arg.Any<CancellationToken>())
            .Returns(
                [
                    new PackageSummary(
                        "react",
                        "React",
                        "19.0.0",
                        2,
                        4,
                        DateTimeOffset.UtcNow,
                        "/packages/react.db"
                    ),
                ]
            );

        var result = await GroundKitMcpTools.ResolveSourceAsync(
            "react",
            store,
            CreateAgent(),
            CancellationToken.None
        );

        result.IsError.ShouldNotBe(true);
        var structured = result.StructuredContent.ShouldBeOfType<JsonElement>();
        structured.GetProperty("packages").GetArrayLength().ShouldBe(1);
        structured
            .GetProperty("packages")[0]
            .GetProperty("packageId")
            .GetString()
            .ShouldBe("react");
    }

    [Theory]
    [InlineData("", "topic", "packageId")]
    [InlineData("react", "", "topic")]
    public async Task Should_Reject_Missing_Document_Query_Arguments(
        string packageId,
        string topic,
        string expectedArgument
    )
    {
        var result = await GroundKitMcpTools.QueryDocsAsync(
            packageId,
            topic,
            Substitute.For<IPackageStore>(),
            CreateAgent(),
            CancellationToken.None
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain(expectedArgument);
    }

    [Fact]
    public async Task Should_Report_Install_Command_When_Package_Is_Not_Installed()
    {
        var store = Substitute.For<IPackageStore>();
        store
            .GetPackageAsync("react", Arg.Any<CancellationToken>())
            .Returns((PackageSummary?)null);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var registry = Substitute.For<IContextRegistryClient>();
        registry
            .SearchAsync("npm", "react", null, Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("react", "npm", "19.0.0", "A library", 1_024L)]);

        var result = await GroundKitMcpTools.QueryDocsAsync(
            "react",
            "hooks",
            store,
            CreateAgent(),
            CancellationToken.None,
            registryClient: registry
        );

        result.IsError.ShouldBe(true);
        var text = GetText(result.Content);
        text.ShouldContain("not installed locally");
        text.ShouldContain("npm/react@19.0.0");
        text.ShouldContain("ck install npm/react 19.0.0");
        await store
            .DidNotReceive()
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Prefer_Requested_Version_When_Suggesting_Install()
    {
        var store = Substitute.For<IPackageStore>();
        store
            .GetPackageAsync("react@18.0.0", Arg.Any<CancellationToken>())
            .Returns((PackageSummary?)null);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var registry = Substitute.For<IContextRegistryClient>();
        registry
            .SearchAsync("npm", "react", "18.0.0", Arg.Any<CancellationToken>())
            .Returns(
                [
                    new RegistryPackage("react", "npm", "19.0.0", null, 2_048L),
                    new RegistryPackage("react", "npm", "18.0.0", null, 1_024L),
                ]
            );

        var result = await GroundKitMcpTools.QueryDocsAsync(
            "react@18.0.0",
            "hooks",
            store,
            CreateAgent(),
            CancellationToken.None,
            registryClient: registry
        );

        GetText(result.Content).ShouldContain("ck install npm/react 18.0.0");
    }

    [Fact]
    public async Task Should_List_Available_Versions_When_Requested_Version_Is_Not_Published()
    {
        var store = Substitute.For<IPackageStore>();
        store
            .GetPackageAsync("vue@3.5.0", Arg.Any<CancellationToken>())
            .Returns((PackageSummary?)null);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var registry = Substitute.For<IContextRegistryClient>();
        registry
            .SearchAsync("npm", "vue", "3.5.0", Arg.Any<CancellationToken>())
            .Returns([]);
        registry
            .SearchAsync("npm", "vue", null, Arg.Any<CancellationToken>())
            .Returns(
                [
                    new RegistryPackage("vue", "npm", "latest", null, 4_096L),
                    new RegistryPackage("vue", "npm", "3.4.21", null, 3_072L),
                ]
            );

        var result = await GroundKitMcpTools.QueryDocsAsync(
            "vue@3.5.0",
            "composition api",
            store,
            CreateAgent(),
            CancellationToken.None,
            registryClient: registry
        );

        result.IsError.ShouldBe(true);
        var text = GetText(result.Content);
        text.ShouldContain("Version '3.5.0' of npm/vue is not published.");
        text.ShouldContain("npm/vue@latest");
        text.ShouldContain("ck install npm/vue latest");
    }

    [Fact]
    public async Task Should_Stay_Actionable_When_Registry_Is_Unavailable()
    {
        var store = Substitute.For<IPackageStore>();
        store
            .GetPackageAsync("react", Arg.Any<CancellationToken>())
            .Returns((PackageSummary?)null);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var registry = Substitute.For<IContextRegistryClient>();
        registry
            .SearchAsync("npm", "react", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RegistryPackage>>(_ =>
                throw new HttpRequestException("registry offline")
            );

        var result = await GroundKitMcpTools.QueryDocsAsync(
            "react",
            "hooks",
            store,
            CreateAgent(),
            CancellationToken.None,
            registryClient: registry
        );

        result.IsError.ShouldBe(true);
        var text = GetText(result.Content);
        text.ShouldContain("could not be reached");
        text.ShouldContain("ck install npm/react");
    }

    [Fact]
    public async Task Should_List_Installed_Packages_When_Requested_Package_Is_Missing()
    {
        var store = Substitute.For<IPackageStore>();
        store
            .GetPackageAsync("vue", Arg.Any<CancellationToken>())
            .Returns((PackageSummary?)null);
        store
            .ListAsync(Arg.Any<CancellationToken>())
            .Returns(
                [new PackageSummary("react", "React", "19.0.0", 2, 4, DateTimeOffset.UtcNow, "/p.db")]
            );

        var result = await GroundKitMcpTools.QueryDocsAsync(
            "vue",
            "composition",
            store,
            CreateAgent(),
            CancellationToken.None
        );

        GetText(result.Content).ShouldContain("Installed packages: react");
    }

    [Fact]
    public async Task Should_Reject_Empty_Ask_Question()
    {
        var result = await GroundKitMcpTools.AskDocsAsync(
            "",
            Substitute.For<IPackageStore>(),
            CreateAgent(),
            CancellationToken.None
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain("'question'");
    }

    [Fact]
    public async Task Should_Detect_Package_And_Download_Before_Answering_Ask()
    {
        var store = Substitute.For<IPackageStore>();
        store
            .GetPackageAsync("npm/angular", Arg.Any<CancellationToken>())
            .Returns((PackageSummary?)null);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
                new DocsQueryResponse(call.Arg<DocsQueryRequest>().PackageId, "21.0.3", [], 0)
            );
        var registry = Substitute.For<IContextRegistryClient>();
        registry
            .SearchAsync("npm", "angular", null, Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("angular", "npm", "21.0.3", "Web framework", 1_024L)]);
        var downloader = Substitute.For<IPackageDownloadService>();
        downloader
            .InstallRegistryPackageAsync(
                "npm",
                "angular",
                "21.0.3",
                Arg.Any<CancellationToken>()
            )
            .Returns("/packages/angular@21.0.3.db");

        var result = await GroundKitMcpTools.AskDocsAsync(
            "what are components in angular?",
            store,
            CreateAgent(),
            CancellationToken.None,
            registryClient: registry,
            packageDownloadService: downloader
        );

        result.IsError.ShouldNotBe(true);
        await downloader
            .Received(1)
            .InstallRegistryPackageAsync(
                "npm",
                "angular",
                "21.0.3",
                Arg.Any<CancellationToken>()
            );
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
    public async Task Should_Answer_Ask_From_Installed_Package_Without_The_Registry()
    {
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
                        "/packages/angular.db"
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
                    "/packages/angular.db"
                )
            );
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
                new DocsQueryResponse(call.Arg<DocsQueryRequest>().PackageId, "21.0.3", [], 0)
            );
        var registry = Substitute.For<IContextRegistryClient>();
        var downloader = Substitute.For<IPackageDownloadService>();

        var result = await GroundKitMcpTools.AskDocsAsync(
            "what are components in angular?",
            store,
            CreateAgent(),
            CancellationToken.None,
            registryClient: registry,
            packageDownloadService: downloader
        );

        result.IsError.ShouldNotBe(true);
        await registry
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
    public async Task Should_Explain_When_Ask_Detects_No_Package()
    {
        var store = Substitute.For<IPackageStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        var result = await GroundKitMcpTools.AskDocsAsync(
            "how do i configure the flux capacitor?",
            store,
            CreateAgent(),
            CancellationToken.None
        );

        result.IsError.ShouldBe(true);
        var text = GetText(result.Content);
        text.ShouldContain("Could not tell which package");
        text.ShouldContain("ck add");
        await store
            .DidNotReceiveWithAnyArgs()
            .QueryAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Detect_Package_By_Alias_When_Asking()
    {
        var store = Substitute.For<IPackageStore>();
        store
            .GetPackageAsync("npm/angular", Arg.Any<CancellationToken>())
            .Returns((PackageSummary?)null);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
                new DocsQueryResponse(call.Arg<DocsQueryRequest>().PackageId, "21.0.3", [], 0)
            );
        var registry = Substitute.For<IContextRegistryClient>();
        registry
            .SearchAsync("npm", "angular", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("angular", "npm", "21.0.3", "Web framework", 1_024L)]);
        var downloader = Substitute.For<IPackageDownloadService>();
        downloader
            .InstallRegistryPackageAsync(
                "npm",
                "angular",
                "21.0.3",
                Arg.Any<CancellationToken>()
            )
            .Returns("/packages/angular@21.0.3.db");

        var result = await GroundKitMcpTools.AskDocsAsync(
            "angularjs digest cycle",
            store,
            CreateAgent(),
            CancellationToken.None,
            registryClient: registry,
            packageDownloadService: downloader
        );

        result.IsError.ShouldNotBe(true);
        await downloader
            .Received(1)
            .InstallRegistryPackageAsync(
                "npm",
                "angular",
                "21.0.3",
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Guide_When_Ask_Detects_A_Package_It_Cannot_Download()
    {
        var store = Substitute.For<IPackageStore>();
        store
            .GetPackageAsync("npm/angular", Arg.Any<CancellationToken>())
            .Returns((PackageSummary?)null);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var registry = Substitute.For<IContextRegistryClient>();
        registry
            .SearchAsync("npm", "angular", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("angular", "npm", "21.0.3", "Web framework", 1_024L)]);

        // No download service: the tool cannot install, so it must explain the install command.
        var result = await GroundKitMcpTools.AskDocsAsync(
            "what are components in angular?",
            store,
            CreateAgent(),
            CancellationToken.None,
            registryClient: registry
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain("ck install");
    }

    [Fact]
    public async Task Should_Reject_Empty_Find_Libraries_Question()
    {
        var result = await GroundKitMcpTools.FindLibrariesAsync(
            "",
            Substitute.For<IPackageStore>(),
            CreateAgent(),
            CancellationToken.None
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain("'question'");
    }

    [Fact]
    public async Task Should_Reject_A_Non_Positive_Find_Libraries_Limit()
    {
        var result = await GroundKitMcpTools.FindLibrariesAsync(
            "how do hooks work?",
            Substitute.For<IPackageStore>(),
            CreateAgent(),
            CancellationToken.None,
            maxResults: 0
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain("'maxResults'");
    }

    [Fact]
    public async Task Should_Rank_Libraries_For_A_Question()
    {
        var store = Substitute.For<IPackageStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([Summary("react")]);

        var result = await GroundKitMcpTools.FindLibrariesAsync(
            "how do i use hooks in react?",
            store,
            CreateAgent(),
            CancellationToken.None
        );

        result.IsError.ShouldNotBe(true);
        var structured = result.StructuredContent.ShouldBeOfType<JsonElement>();
        var libraries = structured.GetProperty("libraries");
        libraries.GetArrayLength().ShouldBe(1);
        libraries[0].GetProperty("libraryId").GetString().ShouldBe("react");
        libraries[0].GetProperty("installed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Should_Return_A_Curated_Id_For_A_Library_That_Is_Not_Installed()
    {
        var store = Substitute.For<IPackageStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        var result = await GroundKitMcpTools.FindLibrariesAsync(
            "how do i use hooks?",
            store,
            CreateAgent(),
            CancellationToken.None,
            libraryName: "react"
        );

        result.IsError.ShouldNotBe(true);
        var structured = result.StructuredContent.ShouldBeOfType<JsonElement>();
        var libraries = structured.GetProperty("libraries");
        libraries.GetArrayLength().ShouldBe(1);
        libraries[0].GetProperty("libraryId").GetString().ShouldBe("npm/react");
        libraries[0].GetProperty("installed").GetBoolean().ShouldBeFalse();
        libraries[0].GetProperty("repository").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Should_Reject_Empty_Search_Docs_Question()
    {
        var result = await GroundKitMcpTools.SearchDocsAsync(
            "",
            Substitute.For<IPackageStore>(),
            CreateAgent(),
            CancellationToken.None
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain("'question'");
    }

    [Theory]
    [InlineData(0, 8, 0.5, "maxTokens")]
    [InlineData(2_000, 0, 0.5, "maxHits")]
    [InlineData(2_000, 8, 2.0, "relativeScoreCutoff")]
    public async Task Should_Reject_Invalid_Search_Docs_Limits(
        int maxTokens,
        int maxHits,
        double cutoff,
        string expectedArgument
    )
    {
        var result = await GroundKitMcpTools.SearchDocsAsync(
            "how do hooks work?",
            Substitute.For<IPackageStore>(),
            CreateAgent(),
            CancellationToken.None,
            maxTokens: maxTokens,
            maxHits: maxHits,
            relativeScoreCutoff: cutoff
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain(expectedArgument);
    }

    [Fact]
    public async Task Should_Search_Every_Detected_Library_And_Tag_Its_Hits()
    {
        var store = Substitute.For<IPackageStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([Summary("react"), Summary("axios")]);
        store
            .GetPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Summary(call.Arg<string>()));
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var packageId = call.Arg<DocsQueryRequest>().PackageId;
                return new DocsQueryResponse(
                    packageId,
                    null,
                    [new DocsQueryHit($"{packageId} doc", "Section", "content", 10, false, 3.0)],
                    10
                );
            });

        var result = await GroundKitMcpTools.SearchDocsAsync(
            "axios interceptor in react",
            store,
            CreateAgent(),
            CancellationToken.None
        );

        result.IsError.ShouldNotBe(true);
        var structured = result.StructuredContent.ShouldBeOfType<JsonElement>();
        structured
            .GetProperty("packages")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ShouldBe(["react", "axios"]);
        structured.GetProperty("hits").GetArrayLength().ShouldBe(2);
        structured.GetProperty("hits")[0].GetProperty("packageId").GetString().ShouldBe("react");
        await store
            .Received(2)
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Pin_Libraries_From_The_Hint_List()
    {
        var store = Substitute.For<IPackageStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([Summary("react"), Summary("axios")]);
        store
            .GetPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Summary(call.Arg<string>()));
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
                new DocsQueryResponse(
                    call.Arg<DocsQueryRequest>().PackageId,
                    null,
                    [new DocsQueryHit("doc", "Section", "content", 10, false, 1.0)],
                    10
                )
            );

        // The question names no library at all, so only the hints decide what is searched.
        var result = await GroundKitMcpTools.SearchDocsAsync(
            "how do i stream a response",
            store,
            CreateAgent(),
            CancellationToken.None,
            libraries: ["axios", "react"]
        );

        result.IsError.ShouldNotBe(true);
        var structured = result.StructuredContent.ShouldBeOfType<JsonElement>();
        structured
            .GetProperty("packages")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ShouldBe(["axios", "react"]);
    }

    [Fact]
    public async Task Should_Explain_When_Search_Docs_Detects_No_Library()
    {
        var store = Substitute.For<IPackageStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        var result = await GroundKitMcpTools.SearchDocsAsync(
            "how do i configure the flux capacitor",
            store,
            CreateAgent(),
            CancellationToken.None
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain("Could not tell which package");
        await store.DidNotReceiveWithAnyArgs().QueryAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Guide_When_A_Pinned_Library_Cannot_Be_Resolved()
    {
        var store = Substitute.For<IPackageStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store
            .GetPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PackageSummary?)null);

        var result = await GroundKitMcpTools.SearchDocsAsync(
            "how do i use hooks",
            store,
            CreateAgent(),
            CancellationToken.None,
            libraries: ["react"]
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain("not installed locally");
        GetText(result.Content).ShouldContain("ck install npm/react");
    }

    private static PackageSummary Summary(string packageId) =>
        new(packageId, packageId, null, 1, 1, DateTimeOffset.UtcNow, $"/packages/{packageId}.db");

    private static GroundKitToolResponseAgent CreateAgent() =>
        new(NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());

    private static string GetText(IList<ContentBlock> content) =>
        content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
}
