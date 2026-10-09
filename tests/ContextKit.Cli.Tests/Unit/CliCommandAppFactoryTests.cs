using System.Net.Http;
using GroundKit.Cli;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Registry;
using GroundKit.Semantic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Cli;

namespace GroundKit.Tests.Unit;

/// <summary>
/// Drives the real Spectre command app rather than calling <see cref="CliApplication"/> directly.
/// </summary>
/// <remarks>
/// The other CLI tests hand arguments straight to <see cref="CliApplication.RunAsync"/>, which skips
/// command-line parsing entirely. That is exactly where <c>--docs-path</c> used to disappear: the alias
/// was read from the raw arguments but never declared as an option, so Spectre accepted the command,
/// discarded the alias, and built a package from whichever directory auto-detection picked instead.
/// </remarks>
public sealed class CliCommandAppFactoryTests
{
    [Theory]
    [InlineData("--path")]
    [InlineData("--docs-path")]
    [InlineData("-p")]
    public async Task Should_Forward_The_Documentation_Path_Option(string option)
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        builder
            .BuildAsync(
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>()
            )
            .Returns(CreateBuildResult());
        var store = Substitute.For<IPackageStore>();
        store.SaveAsync(Arg.Any<BuildResult>(), Arg.Any<CancellationToken>()).Returns("package.db");

        var exitCode = await CreateApp(builder, store)
            .RunAsync(["add", "./my-project", option, "manual"], TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        await builder
            .Received(1)
            .BuildAsync(
                "./my-project",
                "manual",
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>()
            );
    }

    [Fact]
    public async Task Should_Reject_An_Undeclared_Option_Instead_Of_Ignoring_It()
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        var store = Substitute.For<IPackageStore>();

        var exitCode = await CreateApp(builder, store)
            .RunAsync(
                ["add", "./my-project", "--not-a-real-option", "x"],
                TestContext.Current.CancellationToken
            );

        // Non-zero is the point: silently ignoring an unknown option is how a command ends up doing
        // something other than what the caller asked for.
        exitCode.ShouldNotBe(0);
        await builder
            .DidNotReceiveWithAnyArgs()
            .BuildAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Should_Forward_A_Single_Natural_Language_Query_Argument()
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        var store = Substitute.For<IPackageStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store
            .GetPackageAsync("npm/angular", Arg.Any<CancellationToken>())
            .Returns(
                new PackageSummary(
                    "angular",
                    "Angular",
                    "21.0.3",
                    1,
                    1,
                    DateTimeOffset.UtcNow,
                    "angular.db"
                )
            );
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new DocsQueryResponse(call.Arg<DocsQueryRequest>().PackageId, "21.0.3", [], 0));

        // A single positional argument has to survive Spectre's binding as the question text; both
        // query arguments are optional so the detector can read the package out of the question.
        var exitCode = await CreateApp(builder, store)
            .RunAsync(
                ["query", "what are components in angular?"],
                TestContext.Current.CancellationToken
            );

        exitCode.ShouldBe(0);
        await store
            .Received(1)
            .QueryAsync(
                Arg.Is<DocsQueryRequest>(request =>
                    request.PackageId == "angular@21.0.3"
                    && request.Topic == "what are components in angular?"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Forward_Two_Argument_Query_With_Flags()
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        var store = Substitute.For<IPackageStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store
            .GetPackageAsync("react", Arg.Any<CancellationToken>())
            .Returns(
                new PackageSummary(
                    "react",
                    "React",
                    "19.0.0",
                    1,
                    1,
                    DateTimeOffset.UtcNow,
                    "react.db"
                )
            );
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new DocsQueryResponse(call.Arg<DocsQueryRequest>().PackageId, "19.0.0", [], 0));

        var exitCode = await CreateApp(builder, store)
            .RunAsync(
                ["query", "react", "useEffect cleanup", "--pretty", "--no-install"],
                TestContext.Current.CancellationToken
            );

        exitCode.ShouldBe(0);
        await store
            .Received(1)
            .QueryAsync(
                Arg.Is<DocsQueryRequest>(request =>
                    request.PackageId == "react@19.0.0"
                    && request.Topic == "useEffect cleanup"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Report_Usage_When_Query_Has_No_Arguments()
    {
        var exitCode = await CreateApp(
                Substitute.For<IDocumentPackageBuilder>(),
                Substitute.For<IPackageStore>()
            )
            .RunAsync(["query"], TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Should_Forward_The_Libraries_Option_For_A_Multi_Library_Query()
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        var store = Substitute.For<IPackageStore>();
        store
            .ListAsync(Arg.Any<CancellationToken>())
            .Returns([LibrarySummary("angular"), LibrarySummary("react")]);
        store
            .GetPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => LibrarySummary(call.Arg<string>()));
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
                new DocsQueryResponse(call.Arg<DocsQueryRequest>().PackageId, null, [], 0)
            );

        // Both the option and its value must be consumed by Spectre, leaving the question intact and
        // the hints picking the packages rather than the words.
        var exitCode = await CreateApp(builder, store)
            .RunAsync(
                ["query", "how do components work?", "--libraries", "angular,react"],
                TestContext.Current.CancellationToken
            );

        exitCode.ShouldBe(0);
        await store
            .Received(2)
            .QueryAsync(
                Arg.Is<DocsQueryRequest>(request =>
                    request.Topic == "how do components work?"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Should_Forward_Allow_Failures_To_Registry_Build_All()
    {
        var root = Path.Combine(Path.GetTempPath(), "groundkit-cli-registry", Guid.NewGuid().ToString("n"));
        var registryRoot = Path.Combine(root, "registry");
        var output = Path.Combine(root, "dist-packages");
        Directory.CreateDirectory(registryRoot);
        WriteDefinition(registryRoot, "alpha");
        WriteDefinition(registryRoot, "failing");

        var builder = Substitute.For<IDocumentPackageBuilder>();
        // The registry pipeline calls the five-argument overload, so both must be stubbed; the
        // shorter overloads are default interface methods and are intercepted separately.
        builder
            .BuildAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(CreateBuildResult()));
        builder
            .BuildAsync(
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>()
            )
            .Returns(_ => Task.FromResult(CreateBuildResult()));
        builder
            .BuildAsync(
                Arg.Is<string>(source => source.Contains("failing", StringComparison.Ordinal)),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>()
            )
            .Returns(Task.FromException<BuildResult>(new InvalidOperationException("boom")));

        try
        {
            var exitCode = await CreateRegistryApp(builder)
                .RunAsync(
                    [
                        "registry",
                        "build-all",
                        "--dir",
                        registryRoot,
                        "--output",
                        output,
                        "--allow-failures",
                    ],
                    TestContext.Current.CancellationToken
                );

            // One of the two definitions fails, so without --allow-failures this is a 1. A 0 therefore
            // proves both that Spectre declared the option and that the flag reached RegistryApplication.
            exitCode.ShouldBe(0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Should_Reject_An_Undeclared_Registry_Option()
    {
        var exitCode = await CreateRegistryApp(Substitute.For<IDocumentPackageBuilder>())
            .RunAsync(
                ["registry", "build-all", "--not-a-real-option"],
                TestContext.Current.CancellationToken
            );

        exitCode.ShouldNotBe(0);
    }

    private static void WriteDefinition(string registryRoot, string name)
    {
        var directory = Path.Combine(registryRoot, "npm");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{name}.yaml"),
            $"""
            name: {name}
            description: {name} documentation
            source:
              type: git
              url: "https://github.com/example/{name}"
            """
        );
    }

    [Theory]
    [InlineData("lexical", SearchMode.Lexical)]
    [InlineData("semantic", SearchMode.Semantic)]
    [InlineData("hybrid", SearchMode.Hybrid)]
    public async Task Should_Forward_Search_Mode_Without_Polluting_Query(string mode, SearchMode expected)
    {
        var store = Substitute.For<IPackageStore>();
        store.GetPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(LibrarySummary("react"));
        store.QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>()).Returns(new DocsQueryResponse("react", null, [], 0));
        var exit = await CreateApp(Substitute.For<IDocumentPackageBuilder>(), store).RunAsync(
            ["query", "react", "cleanup", "--search-mode", mode], TestContext.Current.CancellationToken
        );
        exit.ShouldBe(0);
        await store.Received(1).QueryAsync(Arg.Is<DocsQueryRequest>(request => request.Topic == "cleanup" && request.Options!.SearchMode == expected), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Forward_Search_Mode_To_All_Hinted_Libraries()
    {
        var store = Substitute.For<IPackageStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([LibrarySummary("react"), LibrarySummary("angular")]);
        store.GetPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => LibrarySummary(call.Arg<string>()));
        store.QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>()).Returns(call => new DocsQueryResponse(call.Arg<DocsQueryRequest>().PackageId, null, [], 0));
        var exit = await CreateApp(Substitute.For<IDocumentPackageBuilder>(), store).RunAsync(
            ["query", "component teardown", "--libraries", "react,angular", "--search-mode", "hybrid"], TestContext.Current.CancellationToken
        );
        exit.ShouldBe(0);
        await store.Received(2).QueryAsync(Arg.Is<DocsQueryRequest>(request => request.Topic == "component teardown" && request.Options!.SearchMode == SearchMode.Hybrid), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Reject_Invalid_Mode_Before_Querying()
    {
        var store = Substitute.For<IPackageStore>();
        var exit = await CreateApp(Substitute.For<IDocumentPackageBuilder>(), store).RunAsync(
            ["query", "react", "cleanup", "--search-mode", "auto"], TestContext.Current.CancellationToken
        );
        exit.ShouldBe(1);
        await store.DidNotReceiveWithAnyArgs().QueryAsync(default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("disable")]
    [InlineData("model", "list")]
    public async Task Should_Parse_Semantic_Management_Commands(params string[] arguments)
    {
        var root = Path.Combine(Path.GetTempPath(), "groundkit-cli-semantic", Guid.NewGuid().ToString("N"));
        using var runtime = new SemanticRuntime(root);
        try
        {
            var exit = await CreateApp(Substitute.For<IDocumentPackageBuilder>(), Substitute.For<IPackageStore>(), semanticRuntime: runtime)
                .RunAsync(["semantic", .. arguments], TestContext.Current.CancellationToken);
            exit.ShouldBe(0);
            runtime.Settings.Mode.ShouldBe(SearchMode.Lexical);
            Directory.Exists(runtime.ProviderPath).ShouldBeFalse();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static CommandApp CreateRegistryApp(IDocumentPackageBuilder builder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(builder);
        services.AddSingleton(
            new RegistryApplication(builder, NullLoggerFactory.Instance, Substitute.For<IHttpClientFactory>())
        );
        return CliCommandAppFactory.Create(services);
    }

    private static CommandApp CreateApp(
        IDocumentPackageBuilder builder,
        IPackageStore store,
        IContextRegistryClient? registryClient = null,
        SemanticRuntime? semanticRuntime = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(builder);
        services.AddSingleton(store);
        if (semanticRuntime is not null) services.AddSingleton(semanticRuntime);
        if (registryClient is not null)
        {
            services.AddSingleton(registryClient);
        }

        services.AddSingleton<CliApplication>();
        return CliCommandAppFactory.Create(services);
    }

    /// <summary>A stored package with no version, which is what a local build produces.</summary>
    private static PackageSummary LibrarySummary(string packageId) =>
        new(packageId, packageId, null, 1, 1, DateTimeOffset.UtcNow, $"{packageId}.db");

    private static BuildResult CreateBuildResult()
    {
        var source = new DocumentationSource(
            SourceKind.LocalDirectory,
            "my-project",
            "my-project",
            "C:/my-project"
        );
        return new BuildResult(
            source,
            new PackageManifest(
                "my-project",
                "my-project",
                null,
                SourceKind.LocalDirectory,
                "C:/my-project",
                "my-project",
                null,
                DateTimeOffset.UtcNow,
                "1.0.0",
                1,
                1,
                0
            ),
            [],
            [],
            []
        );
    }
}
