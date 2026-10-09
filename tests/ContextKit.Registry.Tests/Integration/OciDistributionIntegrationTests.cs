using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GroundKit.Configuration;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Hosting;
using GroundKit.Ingestion.Services;
using GroundKit.Oci;
using GroundKit.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using static GroundKit.Registry.Tests.Integration.RegistryBuildAllIntegrationTests;

namespace GroundKit.Registry.Tests.Integration;

/// <summary>
/// Drives the OCI distribution path end to end against an in-memory registry that implements the
/// token, blob upload, and manifest endpoints of the OCI Distribution Specification.
/// </summary>
/// <remarks>
/// The registry is addressed as <see cref="TtlShRegistryHelper.Host"/> so these offline tests and the
/// live ones in <see cref="TtlShPublishIntegrationTests"/> describe the same target.
/// </remarks>
[Collection(RegistryConsoleCollection.Name)]
public sealed class OciDistributionIntegrationTests : IDisposable
{
    private const string RegistryHost = TtlShRegistryHelper.Host;
    private const string OciRepository = RegistryHost + "/owner/groundkit";
    private const string CatalogBase =
        "https://catalog.test/registry/index.json";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "groundkit-registry-oci",
        Guid.NewGuid().ToString("n")
    );

    public OciDistributionIntegrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Should_Push_Built_Packages_As_Content_Addressed_Oci_Artifacts()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        var referencesPath = Path.Combine(_root, "oci-references.json");
        TestPackageFactory.Create(_root, output, "alpha", "latest");
        WriteDefinition(registryRoot, "alpha");
        var registry = new FakeOciRegistry();

        var (exitCode, outputText) = await RunAsync(
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

        exitCode.ShouldBe(0, outputText);
        outputText.ShouldContain("Pushed 1 package(s)");

        var packageBytes = await File.ReadAllBytesAsync(
            Path.Combine(output, "alpha@latest.db"),
            TestContext.Current.CancellationToken
        );
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        registry.Blobs.ShouldContainKey(digest);
        registry.Blobs[digest].ShouldBe(packageBytes);

        // The artifact repository name is flattened because OCI forbids slashes inside a segment.
        registry.Manifests.ShouldContainKey($"owner/groundkit/npm-alpha:latest");
        var manifest = JsonNode.Parse(registry.Manifests["owner/groundkit/npm-alpha:latest"])!;
        manifest["layers"]![0]!["digest"]!.GetValue<string>().ShouldBe(digest);
        manifest["layers"]![0]!["mediaType"]!.GetValue<string>().ShouldBe(OciNames.PackageMediaType);

        var references = JsonSerializer.Deserialize<RegistryOciReferences>(
            await File.ReadAllTextAsync(referencesPath, TestContext.Current.CancellationToken)
        )!;
        references.SchemaVersion.ShouldBe(1);
        var package = references.Packages.Single();
        package.Registry.ShouldBe("npm");
        package.Name.ShouldBe("alpha");
        package.Version.ShouldBe("latest");
        // The reference pins the manifest digest; the layer inside it pins the package bytes.
        package.Digest.ShouldStartWith("sha256:");
        package.Reference.ShouldBe($"oci://{RegistryHost}/owner/groundkit/npm-alpha@{package.Digest}");
        registry.AuthorizedRequests.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_Generate_Catalog_That_Addresses_Packages_By_Oci_Digest()
    {
        var (registryRoot, output, referencesPath, registry) = await PushSinglePackageAsync();
        var destination = Path.Combine(_root, "dist-catalog");

        var (exitCode, outputText) = await RunAsync(
            registry,
            "catalog-index",
            "--dir",
            registryRoot,
            "--output",
            output,
            "--destination",
            destination,
            "--oci-references",
            referencesPath
        );

        exitCode.ShouldBe(0, outputText);
        outputText.ShouldContain("references 1 OCI artifact(s)");

        var catalog = JsonSerializer.Deserialize<RegistryCatalog>(
            await File.ReadAllTextAsync(
                Path.Combine(destination, "index.json"),
                TestContext.Current.CancellationToken
            )
        )!;
        var entry = catalog.Packages.Single();
        entry.OciReference.ShouldNotBeNull();
        entry.OciReference!.ShouldStartWith($"oci://{RegistryHost}/owner/groundkit/npm-alpha@sha256:");
        entry.DownloadUrl.ShouldBe(
            $"https://{RegistryHost}/v2/owner/groundkit/npm-alpha/manifests/{entry.OciReference.Split('@')[1]}"
        );

        // OCI-backed catalogs do not copy assets to disk.
        Directory.Exists(Path.Combine(destination, "assets")).ShouldBeFalse();
        _ = registry;
    }

    [Fact]
    public async Task Should_Install_From_An_Oci_Backed_Catalog_And_Verify_The_Checksum()
    {
        var (registryRoot, output, referencesPath, registry) = await PushSinglePackageAsync();
        var destination = Path.Combine(_root, "dist-catalog");
        (await RunAsync(
            registry,
            "catalog-index",
            "--dir",
            registryRoot,
            "--output",
            output,
            "--destination",
            destination,
            "--oci-references",
            referencesPath
        ))
            .ExitCode.ShouldBe(0);

        var catalogBytes = await File.ReadAllBytesAsync(
            Path.Combine(destination, "index.json"),
            TestContext.Current.CancellationToken
        );
        var catalogClient = new HttpClient(new CatalogHandler(catalogBytes))
        {
            BaseAddress = new Uri(CatalogBase + "/"),
        };

        var services = new ServiceCollection();
        services.AddGroundKitServices();
        services.AddSingleton<IHttpClientFactory>(new TestHttpClientFactory(registry));
        using var provider = services.BuildServiceProvider();
        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_root, "consumer")),
            NullLogger<SqlitePackageStore>.Instance
        );
        var consumerClient = new ContextRegistryClient(
            catalogClient,
            provider.GetRequiredService<IHttpClientFactory>()
        );
        var installer = new PackageDownloadService(
            consumerClient,
            store,
            provider.GetRequiredService<IDocumentPackageBuilder>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            new GroundKitOptions(),
            NullLogger<PackageDownloadService>.Instance
        );
        var cli = new GroundKit.Cli.CliApplication(
            provider.GetRequiredService<IDocumentPackageBuilder>(),
            store,
            consumerClient,
            installer
        );

        var exitCode = await cli.RunAsync(["install", "npm/alpha", "latest"]);

        exitCode.ShouldBe(0);
        var installed = await store.GetPackageAsync("alpha", TestContext.Current.CancellationToken);
        installed.ShouldNotBeNull();
        installed.Version.ShouldBe("latest");
        installed.DocumentCount.ShouldBe(1);
        registry.TokenRequests.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_Reject_Install_When_The_Oci_Blob_Does_Not_Match_The_Catalog_Checksum()
    {
        var (registryRoot, output, referencesPath, registry) = await PushSinglePackageAsync();
        var destination = Path.Combine(_root, "dist-catalog");
        (await RunAsync(
            registry,
            "catalog-index",
            "--dir",
            registryRoot,
            "--output",
            output,
            "--destination",
            destination,
            "--oci-references",
            referencesPath
        ))
            .ExitCode.ShouldBe(0);

        // Serve a different blob body while keeping the catalog's size and hash unchanged.
        var catalog = JsonSerializer.Deserialize<RegistryCatalog>(
            await File.ReadAllTextAsync(
                Path.Combine(destination, "index.json"),
                TestContext.Current.CancellationToken
            )
        )!;
        var entry = catalog.Packages.Single();
        registry.Tamper(entry.OciReference!);

        var catalogClient = new HttpClient(
            new CatalogHandler(
                await File.ReadAllBytesAsync(
                    Path.Combine(destination, "index.json"),
                    TestContext.Current.CancellationToken
                )
            )
        )
        {
            BaseAddress = new Uri(CatalogBase + "/"),
        };

        var services = new ServiceCollection();
        services.AddGroundKitServices();
        services.AddSingleton<IHttpClientFactory>(new TestHttpClientFactory(registry));
        using var provider = services.BuildServiceProvider();
        var store = new SqlitePackageStore(
            new PackageStoreOptions(Path.Combine(_root, "tampered-consumer")),
            NullLogger<SqlitePackageStore>.Instance
        );
        var consumerClient = new ContextRegistryClient(
            catalogClient,
            provider.GetRequiredService<IHttpClientFactory>()
        );

        await Should.ThrowAsync<InvalidDataException>(() =>
            consumerClient.DownloadAsync(
                entry.Registry,
                entry.Name,
                entry.Version,
                Path.Combine(_root, "tampered-download.db"),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task Should_Fail_Catalog_Index_When_A_Package_Was_Not_Pushed()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        var referencesPath = Path.Combine(_root, "empty-references.json");
        TestPackageFactory.Create(_root, output, "alpha", "latest");
        WriteDefinition(registryRoot, "alpha");
        await RegistryOciPublisher.WriteAsync(
            new RegistryOciReferences(1, OciRepository, []),
            referencesPath,
            TestContext.Current.CancellationToken
        );

        var (exitCode, outputText) = await RunAsync(
            new FakeOciRegistry(),
            "catalog-index",
            "--dir",
            registryRoot,
            "--output",
            output,
            "--destination",
            Path.Combine(_root, "dist-catalog"),
            "--oci-references",
            referencesPath
        );

        exitCode.ShouldBe(1);
        outputText.ShouldContain("has no OCI reference");
    }

    [Fact]
    public async Task Should_Support_Tag_References_Without_Pushing()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        TestPackageFactory.Create(_root, output, "alpha", "latest");
        WriteDefinition(registryRoot, "alpha");

        var (exitCode, outputText) = await RunAsync(
            new FakeOciRegistry(),
            "catalog-index",
            "--dir",
            registryRoot,
            "--output",
            output,
            "--destination",
            Path.Combine(_root, "dist-catalog"),
            "--oci-repository",
            OciRepository
        );

        exitCode.ShouldBe(0, outputText);
        var catalog = JsonSerializer.Deserialize<RegistryCatalog>(
            await File.ReadAllTextAsync(
                Path.Combine(_root, "dist-catalog", "index.json"),
                TestContext.Current.CancellationToken
            )
        )!;
        catalog.Packages.Single().OciReference
            .ShouldBe($"oci://{RegistryHost}/owner/groundkit/npm-alpha:latest");
    }

    [Fact]
    public async Task Should_Require_A_Base_Url_Or_Oci_Target_For_Catalog_Index()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        TestPackageFactory.Create(_root, output, "alpha", "latest");
        WriteDefinition(registryRoot, "alpha");

        var (exitCode, outputText) = await RunAsync(
            new FakeOciRegistry(),
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

    [Fact]
    public async Task Should_Fail_Push_Oci_Without_A_Repository()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        TestPackageFactory.Create(_root, output, "alpha", "latest");
        WriteDefinition(registryRoot, "alpha");

        var (exitCode, outputText) = await RunAsync(
            new FakeOciRegistry(),
            "push-oci",
            "--dir",
            registryRoot,
            "--output",
            output
        );

        exitCode.ShouldBe(1);
        outputText.ShouldContain("--oci-repository is required");
    }

    public void Dispose()
    {
        // SQLite keeps pooled file handles alive, which blocks the temp tree from being deleted.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        DeleteDirectoryTree(_root);
    }

    private async Task<(
        string RegistryRoot,
        string Output,
        string ReferencesPath,
        FakeOciRegistry Registry
    )> PushSinglePackageAsync()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        var referencesPath = Path.Combine(_root, "oci-references.json");
        TestPackageFactory.Create(_root, output, "alpha", "latest");
        WriteDefinition(registryRoot, "alpha");
        var registry = new FakeOciRegistry();

        (await RunAsync(
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
        ))
            .ExitCode.ShouldBe(0);

        return (registryRoot, output, referencesPath, registry);
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
              docs_path: docs
            """
        );
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        FakeOciRegistry registry,
        string command,
        params string[] args
    )
    {
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        using var provider = services.BuildServiceProvider();
        var application = new RegistryApplication(
            provider.GetRequiredService<IDocumentPackageBuilder>(),
            NullLoggerFactory.Instance,
            provider.GetRequiredService<IHttpClientFactory>(),
            registry.CreateClient()
        );

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
            var exitCode = await application.RunAsync([command, .. args]);
            return (exitCode, writer.ToString());
        }
        finally
        {
            AnsiConsole.Console = previous;
        }
    }

}
