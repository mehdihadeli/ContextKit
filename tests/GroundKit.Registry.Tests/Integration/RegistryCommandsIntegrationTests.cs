using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Hosting;
using GroundKit.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using static GroundKit.Registry.Tests.Integration.RegistryBuildAllIntegrationTests;

namespace GroundKit.Registry.Tests.Integration;

/// <summary>
/// Covers the registry command surface the release pipeline drives end to end:
/// <c>catalog-index</c>, <c>bundle</c>, <c>import-bundle</c>, and <c>publish-all</c>.
/// </summary>
[Collection(RegistryConsoleCollection.Name)]
public sealed class RegistryCommandsIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "groundkit-registry-commands",
        Guid.NewGuid().ToString("n")
    );

    public RegistryCommandsIntegrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Should_Generate_Static_Catalog_From_Built_Packages()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        var destination = Path.Combine(_root, "dist-catalog");
        BuildPackage(output, "alpha", "latest");
        WriteDefinition(registryRoot, "npm", "alpha");

        var (exitCode, outputText) = await RunAsync(
            "catalog-index",
            "--dir",
            registryRoot,
            "--output",
            output,
            "--destination",
            destination,
            "--base-url",
            "https://github.com/example/groundkit/releases/download/registry-1/"
        );

        exitCode.ShouldBe(0, outputText);

        var catalog = JsonSerializer.Deserialize<RegistryCatalog>(
            await File.ReadAllTextAsync(
                Path.Combine(destination, "index.json"),
                TestContext.Current.CancellationToken
            )
        )!;
        catalog.SchemaVersion.ShouldBe(1);
        var entry = catalog.Packages.Single();
        entry.Registry.ShouldBe("npm");
        entry.Name.ShouldBe("alpha");
        entry.Version.ShouldBe("latest");
        entry.DownloadUrl.ShouldBe(
            "https://github.com/example/groundkit/releases/download/registry-1/" + entry.Sha256 + ".db"
        );
        File.Exists(Path.Combine(destination, "assets", entry.Sha256 + ".db")).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_Fail_Catalog_Index_Without_Base_Url()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        BuildPackage(output, "alpha", "latest");
        WriteDefinition(registryRoot, "npm", "alpha");

        var (exitCode, outputText) = await RunAsync(
            "catalog-index",
            "--dir",
            registryRoot,
            "--output",
            output,
            "--destination",
            Path.Combine(_root, "dist-catalog")
        );

        exitCode.ShouldBe(1);
        outputText.ShouldContain("--base-url is required");
    }

    [Theory]
    [InlineData(".zip")]
    [InlineData(".tar.gz")]
    public async Task Should_Bundle_And_Import_Packages(string extension)
    {
        var output = Path.Combine(_root, "dist-packages");
        var importDirectory = Path.Combine(_root, "imported");
        var bundlePath = Path.Combine(_root, "groundkit-registry" + extension);
        BuildPackage(output, "alpha", "latest");
        BuildPackage(output, "beta", "2.0");

        var (bundleExitCode, bundleOutput) = await RunAsync(
            "bundle",
            "--output",
            output,
            "--destination",
            bundlePath
        );

        bundleExitCode.ShouldBe(0, bundleOutput);
        File.Exists(bundlePath).ShouldBeTrue();

        var bundleBytes = await File.ReadAllBytesAsync(
            bundlePath,
            TestContext.Current.CancellationToken
        );
        var index = JsonSerializer.Deserialize<RegistryBundleIndex>(
            Encoding.UTF8.GetString(ReadBundleEntry(bundlePath, "index.json"))
        )!;
        index.SchemaVersion.ShouldBe(1);
        index.Packages.Select(package => package.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(["packages/alpha@latest.db", "packages/beta@2.0.db"]);
        var checksums = Encoding.UTF8.GetString(ReadBundleEntry(bundlePath, "SHA256SUMS"));
        index.Packages.ShouldAllBe(package => checksums.Contains(package.Sha256, StringComparison.Ordinal));

        var (importExitCode, importOutput) = await RunAsync(
            "import-bundle",
            bundlePath,
            "--output",
            importDirectory
        );

        importExitCode.ShouldBe(0, importOutput);
        importOutput.ShouldContain("Imported 2 package(s).");
        File.ReadAllBytes(Path.Combine(importDirectory, "alpha@latest.db"))
            .ShouldBe(
                await File.ReadAllBytesAsync(
                    Path.Combine(output, "alpha@latest.db"),
                    TestContext.Current.CancellationToken
                )
            );
        File.ReadAllBytes(Path.Combine(importDirectory, "beta@2.0.db"))
            .ShouldBe(
                await File.ReadAllBytesAsync(
                    Path.Combine(output, "beta@2.0.db"),
                    TestContext.Current.CancellationToken
                )
            );

        // Reinflating the same package set must produce an identical archive.
        if (extension == ".zip")
        {
            var secondBundle = Path.Combine(_root, "second" + extension);
            (await RunAsync("bundle", "--output", importDirectory, "--destination", secondBundle))
                .ExitCode.ShouldBe(0);
            (
                await File.ReadAllBytesAsync(
                    secondBundle,
                    TestContext.Current.CancellationToken
                )
            ).ShouldBe(bundleBytes);
        }
        else
        {
            bundleBytes.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public async Task Should_Fail_Bundle_When_No_Packages_Exist()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        var (exitCode, outputText) = await RunAsync(
            "bundle",
            "--output",
            Path.Combine(_root, "empty"),
            "--destination",
            Path.Combine(_root, "empty.zip")
        );

        exitCode.ShouldBe(1);
        outputText.ShouldContain("No .db packages found");
    }

    [Fact]
    public async Task Should_Fail_Import_Bundle_Without_Path()
    {
        var (exitCode, outputText) = await RunAsync("import-bundle");

        exitCode.ShouldBe(1);
        outputText.ShouldContain("import-bundle <path>");
    }

    [Theory]
    [InlineData("import-bundle", "import-bundle <path> [--output <path>]")]
    [InlineData("build", "build <name> [version] [--dir <path>] [--output <path>]")]
    [InlineData("publish", "publish <name> [version] [--dir <path>] [--output <path>]")]
    [InlineData("not-a-command", "[--dir <path>] [--output <path>] [--allow-failures]")]
    public async Task Should_Render_Usage_Without_Markup_Errors(string command, string expected)
    {
        var (exitCode, outputText) = await RunAsync(command);

        exitCode.ShouldBe(command == "not-a-command" ? 0 : 1);
        outputText.ShouldContain(expected);
        outputText.ShouldNotContain("Could not find color or style");
    }

    [Fact]
    public async Task Should_Fail_Import_Bundle_When_Checksum_Does_Not_Match()
    {
        var output = Path.Combine(_root, "dist-packages");
        var bundlePath = Path.Combine(_root, "tampered.zip");
        BuildPackage(output, "alpha", "latest");
        (await RunAsync("bundle", "--output", output, "--destination", bundlePath))
            .ExitCode.ShouldBe(0);

        // Rewrite the package inside the bundle so it no longer matches SHA256SUMS.
        using (var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("packages/alpha@latest.db")!;
            entry.Delete();
            using var writer = new StreamWriter(
                archive.CreateEntry("packages/alpha@latest.db").Open()
            );
            writer.Write("tampered");
        }

        var (exitCode, outputText) = await RunAsync(
            "import-bundle",
            bundlePath,
            "--output",
            Path.Combine(_root, "tampered-import")
        );

        exitCode.ShouldBe(1);
        outputText.ShouldContain("Checksum mismatch");
    }

    [Fact]
    public async Task Should_Publish_All_Definitions_When_Registry_Is_Empty()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        var handler = new PublishingHttpMessageHandler();
        WriteDefinition(registryRoot, "npm", "alpha", CreateAlphaRepository());

        var (exitCode, outputText) = await RunPublishAllAsync(handler, registryRoot, output);

        exitCode.ShouldBe(0, outputText);
        outputText.ShouldContain("RegistryBuildFailures=0");
        handler.Requests.ShouldBe(
            [
                "GET packages/npm/alpha/latest",
                "POST packages/npm/alpha/latest",
            ]
        );
        handler.PublishedBodyLength.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_Skip_Already_Published_Definitions()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        var handler = new PublishingHttpMessageHandler { Exists = true };
        WriteDefinition(registryRoot, "npm", "alpha", CreateAlphaRepository());

        var (exitCode, outputText) = await RunPublishAllAsync(handler, registryRoot, output);

        exitCode.ShouldBe(0, outputText);
        outputText.ShouldContain("Skipped existing: npm/alpha@latest");
        handler.Requests.ShouldBe(["GET packages/npm/alpha/latest"]);
    }

    [Fact]
    public async Task Should_Fail_Publish_All_Without_Publish_Key()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        WriteDefinition(registryRoot, "npm", "alpha", CreateAlphaRepository());
        var previousKey = Environment.GetEnvironmentVariable("REGISTRY_PUBLISH_KEY");
        Environment.SetEnvironmentVariable("REGISTRY_PUBLISH_KEY", null);

        try
        {
            var (exitCode, outputText) = await RunPublishAllAsync(
                new PublishingHttpMessageHandler(),
                registryRoot,
                output,
                setPublishKey: false
            );

            exitCode.ShouldBe(1);
            outputText.ShouldContain("RegistryBuildFailures=1");
            outputText.ShouldContain("REGISTRY_PUBLISH_KEY is required");
        }
        finally
        {
            Environment.SetEnvironmentVariable("REGISTRY_PUBLISH_KEY", previousKey);
        }
    }

    public void Dispose()
    {
        DeleteDirectoryTree(_root);
    }

    /// <summary>
    /// Writes a real SQLite package so catalog, bundle, and publish paths read genuine manifests.
    /// </summary>
    private void BuildPackage(string outputDirectory, string packageId, string version)
    {
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        using var provider = services.BuildServiceProvider();
        var packageBuilder = provider.GetRequiredService<IDocumentPackageBuilder>();
        var source = Path.Combine(_root, "sources", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(source);
        var markdown = Path.Combine(source, "guide.md");
        File.WriteAllText(markdown, $"# {packageId}\n\n{packageId} keeps this document.");

        try
        {
            var result = packageBuilder
                .BuildAsync(source, null, TestContext.Current.CancellationToken, version, null)
                .GetAwaiter()
                .GetResult();
            var normalized = result with
            {
                Source = result.Source with { CanonicalId = packageId, DisplayName = packageId },
                Manifest = result.Manifest with
                {
                    PackageId = packageId,
                    DisplayName = packageId,
                    Version = version,
                    SourceCanonicalId = packageId,
                },
            };
            new SqlitePackageStore(
                new PackageStoreOptions(outputDirectory),
                NullLogger<SqlitePackageStore>.Instance
            )
                .SaveAsync(normalized)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }

        File.Exists(Path.Combine(outputDirectory, $"{packageId}@{version}.db")).ShouldBeTrue();
    }

    private string CreateAlphaRepository() =>
        CreateGitRepository(
            Path.Combine(_root, "repositories", "alpha.git"),
            ("docs/guide.md", "# Alpha Guide\n\nAlpha keeps this document.")
        );

    private static void WriteDefinition(
        string registryRoot,
        string manager,
        string name,
        string? source = null
    )
    {
        source ??= $"https://github.com/example/{name}";
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
              docs_path: docs
            """
        );
    }

    private static byte[] ReadBundleEntry(string bundlePath, string entryName)
    {
        if (bundlePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(bundlePath);
            var entry = archive.GetEntry(entryName)
                ?? throw new InvalidDataException($"Bundle entry '{entryName}' was not found.");
            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            return buffer.ToArray();
        }

        using var file = File.OpenRead(bundlePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(gzip);
        while (tar.GetNextEntry() is { } tarEntry)
        {
            var name = tarEntry.Name.TrimStart('.', '/');
            if (!string.Equals(name, entryName, StringComparison.Ordinal))
            {
                continue;
            }

            using var buffer = new MemoryStream();
            tarEntry.DataStream!.CopyTo(buffer);
            return buffer.ToArray();
        }

        throw new InvalidDataException($"Bundle entry '{entryName}' was not found.");
    }

    private async Task<(int ExitCode, string Output)> RunPublishAllAsync(
        PublishingHttpMessageHandler handler,
        string registryRoot,
        string output,
        bool setPublishKey = true
    )
    {
        var previousKey = Environment.GetEnvironmentVariable("REGISTRY_PUBLISH_KEY");
        if (setPublishKey && string.IsNullOrWhiteSpace(previousKey))
        {
            Environment.SetEnvironmentVariable("REGISTRY_PUBLISH_KEY", "test-publish-key");
        }

        try
        {
            var services = new ServiceCollection();
            services.AddGroundKitServices();
            using var provider = services.BuildServiceProvider();
            var application = new RegistryApplication(
                provider.GetRequiredService<IDocumentPackageBuilder>(),
                NullLoggerFactory.Instance,
                new RegistryPublisher(
                    new HttpClient(handler, disposeHandler: false)
                    {
                        BaseAddress = new Uri("https://registry.example.test/"),
                    }
                ),
                provider.GetRequiredService<IHttpClientFactory>()
            );

            return await RunCoreAsync(
                application,
                ["publish-all", "--dir", registryRoot, "--output", output]
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("REGISTRY_PUBLISH_KEY", previousKey);
        }
    }

    private static Task<(int ExitCode, string Output)> RunAsync(string command, params string[] args) =>
        RunCoreAsync(CreateApplication(), [command, .. args]);

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

    /// <summary>
    /// Captures the console output because the pipeline parses its machine-readable summary line.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunCoreAsync(
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

    private sealed class PublishingHttpMessageHandler : HttpMessageHandler
    {
        public bool Exists { get; init; }

        public List<string> Requests { get; } = [];

        public long PublishedBodyLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath.TrimStart('/')}");
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(Exists ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }

            PublishedBodyLength = (await request.Content!.ReadAsByteArrayAsync(cancellationToken)).Length;
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
}
