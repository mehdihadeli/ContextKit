using GroundKit.Cli;
using GroundKit.Core.Contracts;
using GroundKit.Ingestion.Services;
using GroundKit.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroundKit.Tests.Integration;

/// <summary>
/// End-to-end coverage for <c>query</c> against a real SQLite store and a real package builder.
/// </summary>
/// <remarks>
/// The unit tests substitute <see cref="IPackageStore"/>, which is exactly how a version-label
/// mismatch between the selector and the store went unnoticed: a substitute answers whatever it is
/// given. These tests build a real package and then query it, so the selector has to be one the
/// store can actually resolve. Nothing here touches the network: the source is a local folder and no
/// registry client or download service is supplied.
/// </remarks>
public sealed class QueryFromNaturalLanguageIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"groundkit-query-{Guid.NewGuid():N}"
    );
    private readonly string _docsPath;
    private readonly string _packageRoot;

    public QueryFromNaturalLanguageIntegrationTests()
    {
        _docsPath = Path.Combine(_root, "source", "docs");
        _packageRoot = Path.Combine(_root, "packages");
        Directory.CreateDirectory(_docsPath);
        File.WriteAllText(
            Path.Combine(_docsPath, "components.md"),
            """
            # Components

            Components are the building blocks of an Angular application. A component
            controls a view, and the application is a tree of components.

            ## Lifecycle

            A component instance has a lifecycle managed by Angular.
            """
        );
        File.WriteAllText(
            Path.Combine(_docsPath, "signals.md"),
            """
            # Signals

            Signals are reactive values that notify consumers when they change.
            """
        );
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Should_Answer_A_Natural_Language_Question_From_A_Locally_Built_Package()
    {
        var application = CreateApplication();
        var addExitCode = await application.RunAsync([
            "add",
            Path.Combine(_root, "source"),
            "--path",
            "docs",
            "--name",
            "angular",
        ]);
        addExitCode.ShouldBe(0, "the local fixture should build a package.");

        var exitCode = await application.RunAsync(["query", "what are components in angular?"]);

        exitCode.ShouldBe(0, "the question names the package that was just installed.");
        var packages = await CreateStore().ListAsync(TestContext.Current.CancellationToken);
        packages.ShouldHaveSingleItem().PackageId.ShouldBe("angular");

        var response = await CreateStore()
            .QueryAsync(
                new DocsQueryRequest("angular", "what are components in angular?"),
                TestContext.Current.CancellationToken
            );
        response.PackageId.ShouldBe("angular");
        response.Hits.ShouldNotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Should_Answer_The_Explicit_Package_Form_For_A_Local_Package()
    {
        var application = CreateApplication();
        var addExitCode = await application.RunAsync([
            "add",
            Path.Combine(_root, "source"),
            "--path",
            "docs",
            "--name",
            "angular",
        ]);
        addExitCode.ShouldBe(0);

        // The two-argument form must keep working even though the package has no version.
        var exitCode = await application.RunAsync(["query", "angular", "lifecycle"]);

        exitCode.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Should_Report_An_Unresolvable_Version_For_A_Package_With_No_Version()
    {
        var application = CreateApplication();
        var addExitCode = await application.RunAsync([
            "add",
            Path.Combine(_root, "source"),
            "--path",
            "docs",
            "--name",
            "angular",
        ]);
        addExitCode.ShouldBe(0);

        // Nothing is published and no registry client is configured, so a versioned selector for a
        // versionless local package has to fail loudly rather than answer from the wrong identity.
        var exitCode = await application.RunAsync(["query", "angular@19", "lifecycle"]);

        exitCode.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Should_Guide_When_The_Question_Names_An_Uninstalled_Package()
    {
        var application = CreateApplication();

        var exitCode = await application.RunAsync(["query", "what are components in angular?"]);

        exitCode.ShouldBe(1, "nothing is installed and there is no registry to download from.");
        (await CreateStore().ListAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Should_Not_Answer_From_An_Unrelated_Package()
    {
        var application = CreateApplication();
        var addExitCode = await application.RunAsync([
            "add",
            Path.Combine(_root, "source"),
            "--path",
            "docs",
            "--name",
            "react",
        ]);
        addExitCode.ShouldBe(0);

        var exitCode = await application.RunAsync(["query", "how do i configure the flux capacitor?"]);

        exitCode.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Should_Search_Several_Libraries_For_One_Question()
    {
        var application = CreateApplication();
        foreach (var name in new[] { "angular", "react" })
        {
            var addExitCode = await application.RunAsync([
                "add",
                Path.Combine(_root, "source"),
                "--path",
                "docs",
                "--name",
                name,
            ]);
            addExitCode.ShouldBe(0, $"the {name} fixture should build a package.");
        }

        // The hints decide the packages, so the question does not have to name either of them.
        var exitCode = await application.RunAsync([
            "query",
            "what are components?",
            "--libraries",
            "angular,react",
        ]);

        exitCode.ShouldBe(0, "both hinted packages are installed locally.");
        var packages = await CreateStore().ListAsync(TestContext.Current.CancellationToken);
        packages.Count.ShouldBe(2);
    }

    private CliApplication CreateApplication() =>
        new(
            new DocumentPackageBuilder(
                new SourceDetector(),
                new PassthroughHttpClientFactory(),
                NullLogger<DocumentPackageBuilder>.Instance
            ),
            CreateStore()
        );

    private SqlitePackageStore CreateStore() =>
        new(
            new PackageStoreOptions(_packageRoot),
            NullLogger<SqlitePackageStore>.Instance
        );

    /// <summary>A local-only build never issues a request, so a bare client is enough.</summary>
    private sealed class PassthroughHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
