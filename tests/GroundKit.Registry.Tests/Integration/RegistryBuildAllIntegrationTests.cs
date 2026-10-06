using System.Diagnostics;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Hosting;
using GroundKit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace GroundKit.Registry.Tests.Integration;

/// <summary>
/// Covers the exact command the "Validate and build registry definitions" pipeline step runs, so a
/// registry definition that <c>validate</c> accepts but <c>build-all</c> cannot ingest fails the
/// build instead of silently publishing an incomplete catalog.
/// </summary>
[Collection(RegistryConsoleCollection.Name)]
public sealed class RegistryBuildAllIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "groundkit-registry-build-all",
        Guid.NewGuid().ToString("n")
    );

    public RegistryBuildAllIntegrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Should_Build_Every_Definition_Across_Manager_Directories()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        var alphaRepository = CreateGitRepository(
            Path.Combine(_root, "repositories", "alpha.git"),
            ("docs/guide.md", "# Alpha Guide\nAlpha keeps this document.")
        );
        var betaRepository = CreateGitRepository(
            Path.Combine(_root, "repositories", "beta.git"),
            ("docs/guide.md", "# Beta Guide\nBeta keeps this document.")
        );
        WriteDefinition(registryRoot, "npm", "alpha", alphaRepository);
        WriteDefinition(registryRoot, "pip", "beta", betaRepository);

        var (exitCode, outputText) = await RunAsync(
            ["build-all", "--dir", registryRoot, "--output", output]
        );

        exitCode.ShouldBe(0);
        outputText.ShouldContain("RegistryBuildFailures=0");
        await AssertPackageAsync(
            Path.Combine(output, "alpha@latest.db"),
            "alpha",
            "Alpha keeps this document."
        );
        await AssertPackageAsync(
            Path.Combine(output, "beta@latest.db"),
            "beta",
            "Beta keeps this document."
        );
    }

    /// <summary>
    /// Pipeline parity: builds every checked-in definition with the exact command the
    /// "Validate and build registry definitions" step runs. Network gated, because a stale
    /// upstream repository or docs path is only observable by actually cloning it.
    /// </summary>
    [Fact]
    public async Task Should_Build_All_Checked_In_Definitions_Into_Packages()
    {
        SkipUnlessNetworkTestsAreEnabled();

        var registryPath = Path.Combine(FindRepositoryRoot(), "registry");
        var output = Path.Combine(_root, "checked-in-dist");
        var definitions = RegistryDefinitionLoader.LoadDirectory(registryPath);

        definitions.ShouldNotBeEmpty();
        var (exitCode, outputText) = await RunAsync(
            ["build-all", "--dir", registryPath, "--output", output]
        );

        exitCode.ShouldBe(0, outputText);
        outputText.ShouldContain("RegistryBuildFailures=0");

        var built = await ReadManifestsAsync(output);
        built.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ShouldBe(definitions.Select(definition => definition.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        built.ShouldAllBe(entry => entry.Value > 0);
    }

    [Fact]
    public async Task Should_Pass_Validate_Then_Fail_Build_All_When_Docs_Path_Is_Missing()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        var goodRepository = CreateGitRepository(
            Path.Combine(_root, "repositories", "good.git"),
            ("docs/guide.md", "# Good Guide\nGood keeps this document.")
        );
        WriteDefinition(registryRoot, "npm", "good", goodRepository);
        WriteDefinition(
            registryRoot,
            "npm",
            "broken",
            CreateGitRepository(
                Path.Combine(_root, "repositories", "broken.git"),
                ("docs/guide.md", "# Broken Guide")
            ),
            docsPath: "documentation"
        );
        var application = CreateApplication();

        // `validate` only reads YAML, so it cannot detect a docs path that does not exist upstream.
        (await application.RunAsync(["validate", "--dir", registryRoot])).ShouldBe(0);

        var (exitCode, outputText) = await RunAsync(
            application,
            ["build-all", "--dir", registryRoot, "--output", output]
        );

        exitCode.ShouldBe(1);
        outputText.ShouldContain("RegistryBuildFailures=1");
        await AssertPackageAsync(
            Path.Combine(output, "good@latest.db"),
            "good",
            "Good keeps this document."
        );
        File.Exists(Path.Combine(output, "broken@latest.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_Tolerate_Failures_And_Keep_Partial_Output_When_AllowFailures_Is_Set()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        var goodRepository = CreateGitRepository(
            Path.Combine(_root, "repositories", "good.git"),
            ("docs/guide.md", "# Good Guide\nGood keeps this document.")
        );
        WriteDefinition(registryRoot, "npm", "good", goodRepository);
        WriteDefinition(
            registryRoot,
            "npm",
            "broken",
            CreateGitRepository(
                Path.Combine(_root, "repositories", "broken.git"),
                ("docs/guide.md", "# Broken Guide")
            ),
            docsPath: "documentation"
        );

        var (exitCode, outputText) = await RunAsync(
            ["build-all", "--dir", registryRoot, "--output", output, "--allow-failures"]
        );

        exitCode.ShouldBe(0);
        outputText.ShouldContain("RegistryBuildFailures=1");
        await AssertPackageAsync(
            Path.Combine(output, "good@latest.db"),
            "good",
            "Good keeps this document."
        );
    }

    [Fact]
    public async Task Should_Fail_When_AllowFailures_Is_Set_But_Nothing_Builds()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        WriteDefinition(
            registryRoot,
            "npm",
            "broken",
            CreateGitRepository(
                Path.Combine(_root, "repositories", "broken.git"),
                ("docs/guide.md", "# Broken Guide")
            ),
            docsPath: "documentation"
        );

        var (exitCode, outputText) = await RunAsync(
            ["build-all", "--dir", registryRoot, "--output", output, "--allow-failures"]
        );

        exitCode.ShouldBe(1);
        outputText.ShouldContain("RegistryBuildFailures=1");
        File.Exists(Path.Combine(output, "broken@latest.db")).ShouldBeFalse();
    }

    public void Dispose()
    {
        DeleteDirectoryTree(_root);
    }

    private static RegistryApplication CreateApplication()
    {
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        var provider = services.BuildServiceProvider();
        return new RegistryApplication(
            provider.GetRequiredService<IDocumentPackageBuilder>(),
            NullLoggerFactory.Instance,
            new RegistryPublisher(new HttpClient()),
            provider.GetRequiredService<IHttpClientFactory>()
        );
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string[] args) =>
        await RunAsync(CreateApplication(), args);

    /// <summary>
    /// Runs the command while capturing the console output, because the pipeline parses the
    /// machine-readable <c>RegistryBuildFailures=N</c> line out of it.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunAsync(
        RegistryApplication application,
        string[] args
    )
    {
        var previous = AnsiConsole.Console;
        var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(
            new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(writer),
            }
        );

        try
        {
            var exitCode = await application.RunAsync(args);
            return (exitCode, writer.ToString());
        }
        finally
        {
            AnsiConsole.Console = previous;
        }
    }

    private static void WriteDefinition(
        string registryRoot,
        string manager,
        string name,
        string source,
        string docsPath = "docs"
    )
    {
        var directory = Path.Combine(registryRoot, manager);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{name}.yaml"),
            $"""
            name: {name}
            description: {name} documentation
            source:
              type: git
              url: "{source}"
              docs_path: {docsPath}
            """
        );
    }

    /// <summary>
    /// Creates a real git repository. The <c>.git</c> suffix makes <c>SourceDetector</c> classify the
    /// <c>file://</c> URL as a git repository, so git ingestion runs exactly as it does in the
    /// pipeline without reaching the network.
    /// </summary>
    internal static string CreateGitRepository(
        string repositoryPath,
        params (string Path, string Content)[] files
    )
    {
        Directory.CreateDirectory(repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "README.md"), "# Fixture repository");
        foreach (var (relativePath, content) in files)
        {
            var target = Path.Combine(
                repositoryPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)
            );
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }

        RunGit(repositoryPath, "init");
        RunGit(repositoryPath, "add", "--all");
        RunGit(
            repositoryPath,
            "-c",
            "user.email=groundkit-tests@example.test",
            "-c",
            "user.name=GroundKit Tests",
            "-c",
            "commit.gpgsign=false",
            "commit",
            "--message",
            "fixture"
        );
        return new Uri(repositoryPath).AbsoluteUri;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {standardError}{standardOutput}"
            );
        }
    }

    private static async Task AssertPackageAsync(
        string packagePath,
        string expectedPackageId,
        string expectedContent
    )
    {
        File.Exists(packagePath).ShouldBeTrue(packagePath);

        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.GetDirectoryName(packagePath)!),
            NullLogger<SqlitePackageStore>.Instance
        );
        var query = await store.QueryAsync(
            new DocsQueryRequest(expectedPackageId, expectedContent),
            TestContext.Current.CancellationToken
        );
        query.PackageId.ShouldBe(expectedPackageId);
        query.Hits.Any(hit => hit.Content.Contains(expectedContent, StringComparison.Ordinal))
            .ShouldBeTrue();

        await using var connection = new SqliteConnection($"Data Source={packagePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_count FROM manifest";
        var documentCount = Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
        );
        documentCount.ShouldBe(1);
    }

    /// <summary>
    /// Reads <c>package_id -> document_count</c> from every package in <paramref name="output"/>.
    /// </summary>
    internal static async Task<Dictionary<string, int>> ReadManifestsAsync(string output)
    {
        var manifests = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var packagePath in Directory.EnumerateFiles(output, "*.db", SearchOption.TopDirectoryOnly))
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = packagePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString()
            );
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT package_id, document_count FROM manifest";
            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken
            );
            (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue(
                $"Package '{packagePath}' has no manifest."
            );
            manifests[reader.GetString(0)] = reader.GetInt32(1);
        }

        return manifests;
    }

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GroundKit.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    /// <summary>
    /// Clones every checked-in upstream repository, so these tests are opt-in. The registry
    /// pipeline enables them with <c>GROUNDKIT_NETWORK_TESTS=1</c>.
    /// </summary>
    internal static void SkipUnlessNetworkTestsAreEnabled() =>
        Assert.SkipUnless(
            string.Equals(
                Environment.GetEnvironmentVariable("GROUNDKIT_NETWORK_TESTS"),
                "1",
                StringComparison.Ordinal
            ),
            "Set GROUNDKIT_NETWORK_TESTS=1 to run tests that clone upstream documentation repositories."
        );

    internal static void DeleteDirectoryTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(path, recursive: true);
    }
}
