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
/// Covers the checked-in <c>angular</c> definition, which is the reference example of a definition
/// that declares several documentation versions.
/// </summary>
/// <remarks>
/// <para>
/// The offline tests build their own git repository whose tags mirror Angular's naming (no
/// <c>v</c> prefix), so multi-version behaviour is verified without cloning the real repository.
/// The checked-in file itself is asserted structurally, which pins it to the three newest majors.
/// </para>
/// <para>
/// Console output is captured for the commands under test, so this class joins
/// <see cref="RegistryConsoleCollection"/> to avoid racing other console-capturing tests.
/// </para>
/// </remarks>
[Collection(RegistryConsoleCollection.Name)]
public sealed class AngularVersionedDefinitionIntegrationTests : IDisposable
{
    /// <summary>Newest first, matching the order the definition declares.</summary>
    private static readonly string[] Versions = ["21.0.3", "20.3.15", "19.2.17"];

    private const string DocsPath = "adev/src/content";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "groundkit-angular-versioned",
        Guid.NewGuid().ToString("n")
    );

    public AngularVersionedDefinitionIntegrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Should_Declare_The_Three_Latest_Majors_With_Explicit_Tags()
    {
        var definition = LoadCheckedInAngularDefinition();

        definition.IsVersioned.ShouldBeTrue();
        definition.Kind.ShouldBe(SourceKind.GitRepository);
        definition.ResolveVersions().Select(item => item.Version).ShouldBe(Versions);

        // Angular's tags carry no "v" prefix, so an explicit tag must be set for each version.
        // Without it the default "v{version}" pattern would request a tag that does not exist.
        definition.ResolveVersions().ShouldAllBe(item => item.Tag == item.Version);

        foreach (var version in definition.Versions)
        {
            version.Source.ShouldBe("https://github.com/angular/angular");
            version.DocsPath.ShouldBe(DocsPath);
        }
    }

    [Fact]
    public void Should_Expose_The_Newest_Version_As_The_Default()
    {
        var definition = LoadCheckedInAngularDefinition();

        // build-all publishes every entry; a bare "build" selects the first one.
        definition.ResolveVersions()[0].Version.ShouldBe(Versions[0]);
    }

    [Fact]
    public async Task Should_Build_Every_Declared_Version_From_Its_Own_Tag()
    {
        var (registryRoot, output) = CreateTaggedFixture();

        var (exitCode, outputText) = await RunAsync(
            ["build-all", "--dir", registryRoot, "--output", output]
        );

        exitCode.ShouldBe(0, outputText);
        outputText.ShouldContain("RegistryBuildFailures=0");
        outputText.ShouldContain("angular@21.0.3");
        outputText.ShouldContain("angular@20.3.15");
        outputText.ShouldContain("angular@19.2.17");

        // One package per declared version, and each carries that tag's documents. This is what
        // proves the tag is used as the git ref rather than the default branch being reused.
        foreach (var version in Versions)
        {
            var packagePath = Path.Combine(output, $"angular@{version}.db");
            await AssertPackageAsync(packagePath, "angular", version, $"Angular {version} guide");
        }
    }

    [Fact]
    public async Task Should_Default_A_Bare_Build_To_The_Newest_Version()
    {
        var (registryRoot, output) = CreateTaggedFixture();

        var (exitCode, outputText) = await RunAsync(
            ["build", "angular", "--dir", registryRoot, "--output", output]
        );

        exitCode.ShouldBe(0, outputText);
        File.Exists(Path.Combine(output, $"angular@{Versions[0]}.db")).ShouldBeTrue();
        File.Exists(Path.Combine(output, "angular@20.3.15.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_Reject_A_Version_The_Definition_Does_Not_Declare()
    {
        var (registryRoot, output) = CreateTaggedFixture();

        // RegistryApplication.RunAsync reports command failures as a non-zero exit code instead of
        // letting the exception escape, so the assertion has to look at both the code and the text.
        var (exitCode, outputText) = await RunAsync(
            ["build", "angular", "99.0.0", "--dir", registryRoot, "--output", output]
        );

        exitCode.ShouldNotBe(0);
        outputText.ShouldContain("99.0.0");
        File.Exists(Path.Combine(output, "angular@99.0.0.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_Push_Every_Declared_Version_As_A_Separate_Oci_Tag()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        var referencesPath = Path.Combine(_root, "oci-references.json");
        var catalogDirectory = Path.Combine(_root, "dist-catalog");
        WriteVersionedDefinition(registryRoot);

        foreach (var version in Versions)
        {
            TestPackageFactory.Create(_root, output, "angular", version);
        }

        var registry = new FakeOciRegistry();
        var (pushExitCode, pushOutput) = await RunRegistryAsync(
            registry,
            "push-oci",
            "--dir",
            registryRoot,
            "--output",
            output,
            "--oci-repository",
            OciRepository,
            "--oci-references",
            referencesPath
        );

        pushExitCode.ShouldBe(0, pushOutput);
        pushOutput.ShouldContain($"Pushed {Versions.Length} package(s)");

        var references = System.Text.Json.JsonSerializer.Deserialize<RegistryOciReferences>(
            await File.ReadAllTextAsync(referencesPath, TestContext.Current.CancellationToken)
        )!;
        references.Packages.Select(package => package.Version).ShouldBe(Versions);
        references
            .Packages.ShouldAllBe(package =>
                registry.Manifests.ContainsKey($"owner/groundkit/npm-angular:{package.Version}")
            );

        var (catalogExitCode, catalogOutput) = await RunRegistryAsync(
            registry,
            "catalog-index",
            "--dir",
            registryRoot,
            "--output",
            output,
            "--destination",
            catalogDirectory,
            "--oci-references",
            referencesPath
        );

        catalogExitCode.ShouldBe(0, catalogOutput);
        var catalog = System.Text.Json.JsonSerializer.Deserialize<RegistryCatalog>(
            await File.ReadAllTextAsync(
                Path.Combine(catalogDirectory, "index.json"),
                TestContext.Current.CancellationToken
            )
        )!;

        // The catalog is ordered by registry, then name, then version with an ordinal comparison,
        // which is why 19.2.17 comes back first even though the definition lists it last.
        catalog
            .Packages.Select(entry => entry.Version)
            .ShouldBe(Versions.OrderBy(version => version, StringComparer.Ordinal));
        catalog.Packages.Select(entry => entry.Name).ShouldAllBe(name => name == "angular");
    }

    public void Dispose() => DeleteDirectoryTree(_root);

    private static RegistryDefinition LoadCheckedInAngularDefinition()
    {
        var registryPath = Path.Combine(FindRepositoryRoot(), "registry");
        return RegistryDefinitionLoader
            .LoadDirectory(registryPath)
            .Single(definition =>
                string.Equals(definition.Name, "angular", StringComparison.OrdinalIgnoreCase)
            );
    }

    /// <summary>
    /// Creates a git repository carrying one commit and one tag per declared version, with distinct
    /// documentation content so a build can prove which tag it checked out. The path ends in
    /// <c>.git</c> so <c>SourceDetector</c> classifies the <c>file://</c> URL as a git repository.
    /// </summary>
    private (string RegistryRoot, string Output) CreateTaggedFixture()
    {
        var repositoryPath = Path.Combine(_root, "repositories", "angular.git");
        Directory.CreateDirectory(repositoryPath);
        RunGit(repositoryPath, "init");

        foreach (var version in Versions.Reverse())
        {
            var documentPath = Path.Combine(repositoryPath, "adev", "src", "content", "guide.md");
            Directory.CreateDirectory(Path.GetDirectoryName(documentPath)!);
            File.WriteAllText(documentPath, $"# Angular {version} guide\n\nAngular {version} guide");
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
                version
            );
            RunGit(repositoryPath, "tag", version);
        }

        var registryRoot = Path.Combine(_root, "registry");
        var directory = Path.Combine(registryRoot, "npm");
        Directory.CreateDirectory(directory);
        var url = new Uri(repositoryPath).AbsoluteUri;
        File.WriteAllText(
            Path.Combine(directory, "angular.yaml"),
            $"""
            name: angular
            description: "Web application framework"
            repository: https://github.com/angular/angular
            versions:
              - version: "{Versions[0]}"
                tag: "{Versions[0]}"
                source:
                  type: git
                  url: "{url}"
                  docs_path: adev/src/content
              - version: "{Versions[1]}"
                tag: "{Versions[1]}"
                source:
                  type: git
                  url: "{url}"
                  docs_path: adev/src/content
              - version: "{Versions[2]}"
                tag: "{Versions[2]}"
                source:
                  type: git
                  url: "{url}"
                  docs_path: adev/src/content
            """
        );

        return (registryRoot, Path.Combine(_root, "dist-packages"));
    }

    private void WriteVersionedDefinition(string registryRoot)
    {
        var directory = Path.Combine(registryRoot, "npm");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "angular.yaml"),
            $"""
            name: angular
            description: "Web application framework"
            versions:
              - version: "{Versions[0]}"
                tag: "{Versions[0]}"
                source:
                  type: git
                  url: "https://github.com/angular/angular"
                  docs_path: adev/src/content
              - version: "{Versions[1]}"
                tag: "{Versions[1]}"
                source:
                  type: git
                  url: "https://github.com/angular/angular"
                  docs_path: adev/src/content
              - version: "{Versions[2]}"
                tag: "{Versions[2]}"
                source:
                  type: git
                  url: "https://github.com/angular/angular"
                  docs_path: adev/src/content
            """
        );
    }

    private static async Task AssertPackageAsync(
        string packagePath,
        string expectedPackageId,
        string expectedVersion,
        string expectedContent
    )
    {
        File.Exists(packagePath).ShouldBeTrue(packagePath);

        await using (var connection = new SqliteConnection(
            $"Data Source={packagePath};Pooling=False"
        ))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT package_id, version FROM manifest";
            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken
            );
            (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
            reader.GetString(0).ShouldBe(expectedPackageId);
            reader.GetString(1).ShouldBe(expectedVersion);
        }

        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.GetDirectoryName(packagePath)!),
            NullLogger<SqlitePackageStore>.Instance
        );

        // The query selector has to name the version: three packages share the "angular" id in one
        // store, and a bare id resolves to whichever was built most recently.
        var query = await store.QueryAsync(
            new DocsQueryRequest($"{expectedPackageId}@{expectedVersion}", expectedContent),
            TestContext.Current.CancellationToken
        );
        query
            .Hits.Any(hit => hit.Content.Contains(expectedContent, StringComparison.Ordinal))
            .ShouldBeTrue(
                $"The package for {expectedVersion} does not contain that tag's documents."
            );
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string[] args)
    {
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        await using var provider = services.BuildServiceProvider();
        return await RunCapturedAsync(
            new RegistryApplication(
                provider.GetRequiredService<IDocumentPackageBuilder>(),
                NullLoggerFactory.Instance,
                provider.GetRequiredService<IHttpClientFactory>()
            ),
            args
        );
    }

    private static async Task<(int ExitCode, string Output)> RunRegistryAsync(
        FakeOciRegistry registry,
        params string[] args
    )
    {
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        using var provider = services.BuildServiceProvider();
        return await RunCapturedAsync(
            new RegistryApplication(
                provider.GetRequiredService<IDocumentPackageBuilder>(),
                NullLoggerFactory.Instance,
                provider.GetRequiredService<IHttpClientFactory>(),
                registry.CreateClient()
            ),
            args
        );
    }

    /// <summary>
    /// Captures console output because the build summary line and the push count are parsed from it.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunCapturedAsync(
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

        using var process =
            Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
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

    private const string OciRepository = TtlShRegistryHelper.Host + "/owner/groundkit";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "GroundKit.slnx"))
        )
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static void DeleteDirectoryTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            // Git object files are read-only, which blocks a recursive delete on Windows.
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
