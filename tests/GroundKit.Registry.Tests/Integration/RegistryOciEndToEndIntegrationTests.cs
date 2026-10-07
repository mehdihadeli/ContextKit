using GroundKit.Core.Contracts;
using GroundKit.Oci;
using static GroundKit.Registry.Tests.Integration.RegistryBuildAllIntegrationTests;

namespace GroundKit.Registry.Tests.Integration;

/// <summary>
/// The complete consumer scenario, end to end: build a package, publish it as an OCI artifact,
/// generate the static catalog, then install it through the real <c>groundkit install</c> command
/// and read the documentation back out.
/// </summary>
/// <remarks>
/// Everything here runs through production code paths, with <see cref="TtlShRegistryHelper.Host"/> as the
/// target registry. The only substitution is the registry transport, so the test stays deterministic
/// and offline while still exercising the token flow, blob upload, manifest resolution, digest
/// verification, catalog parsing, and CLI install.
/// </remarks>
[Collection(RegistryConsoleCollection.Name)]
public sealed class RegistryOciEndToEndIntegrationTests : IDisposable
{
    private const string OciRepository = TtlShRegistryHelper.Host + "/owner/groundkit";
    private const string PackageName = "alpha";
    private const string PackageVersion = "latest";
    private const string InstalledContent = "alpha keeps this document.";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "groundkit-registry-oci-e2e",
        Guid.NewGuid().ToString("n")
    );

    public RegistryOciEndToEndIntegrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Should_Publish_Then_Install_Through_The_Cli_And_Query_The_Content()
    {
        var published = await PublishAsync();
        var catalogBytes = await File.ReadAllBytesAsync(
            published.CatalogPath,
            TestContext.Current.CancellationToken
        );

        // The published artifact is content addressed: the reference pins the manifest digest.
        published.Reference.ShouldStartWith($"oci://{FakeOciRegistry.Host}/owner/groundkit/npm-alpha@sha256:");

        using var consumer = TestConsumerContext.Create(
            catalogBytes,
            published.Registry,
            Path.Combine(_root, "store")
        );

        var (exitCode, output) = await consumer.RunAsync(
            "install",
            $"npm/{PackageName}",
            PackageVersion
        );

        exitCode.ShouldBe(0, output);
        output.ShouldContain("Built and installed package:");

        var installed = await consumer.Store.GetPackageAsync(
            PackageName,
            TestContext.Current.CancellationToken
        );
        installed.ShouldNotBeNull();
        installed.Version.ShouldBe(PackageVersion);

        // The installed bytes are the exact artifact that was uploaded.
        var built = await File.ReadAllBytesAsync(
            published.PackagePath,
            TestContext.Current.CancellationToken
        );
        published.Registry.Blobs[published.LayerDigest].ShouldBe(built);

        // Authorised pulls prove the catalog entry resolved to a real, servable artifact.
        published.Registry.TokenRequests.ShouldBeGreaterThan(0);
        published.Registry.AuthorizedRequests.ShouldBeGreaterThan(0);

        var query = await consumer.Store.QueryAsync(
            new DocsQueryRequest(PackageName, InstalledContent),
            TestContext.Current.CancellationToken
        );
        query.PackageId.ShouldBe(PackageName);
        query
            .Hits.ShouldContain(hit => hit.Content.Contains(InstalledContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_Resolve_The_Published_Version_When_None_Is_Requested()
    {
        var published = await PublishAsync();
        var catalogBytes = await File.ReadAllBytesAsync(
            published.CatalogPath,
            TestContext.Current.CancellationToken
        );
        using var consumer = TestConsumerContext.Create(
            catalogBytes,
            published.Registry,
            Path.Combine(_root, "store")
        );

        // No version argument: the install must fall back to the catalog's newest entry.
        var (exitCode, output) = await consumer.RunAsync("install", $"npm/{PackageName}");

        exitCode.ShouldBe(0, output);
        var installed = await consumer.Store.GetPackageAsync(
            PackageName,
            TestContext.Current.CancellationToken
        );
        installed.ShouldNotBeNull();
        installed.Version.ShouldBe(PackageVersion);
    }

    [Fact]
    public async Task Should_Serve_A_Repeat_Install_From_The_Local_Store()
    {
        var published = await PublishAsync();
        var catalogBytes = await File.ReadAllBytesAsync(
            published.CatalogPath,
            TestContext.Current.CancellationToken
        );
        using var consumer = TestConsumerContext.Create(
            catalogBytes,
            published.Registry,
            Path.Combine(_root, "store")
        );

        (await consumer.RunAsync("install", $"npm/{PackageName}", PackageVersion))
            .ExitCode.ShouldBe(0);
        var requestsAfterFirstInstall = published.Registry.AuthorizedRequests;

        // Already installed: the second run must not reach the registry again.
        var (exitCode, output) = await consumer.RunAsync(
            "install",
            $"npm/{PackageName}",
            PackageVersion
        );

        exitCode.ShouldBe(0, output);
        published.Registry.AuthorizedRequests.ShouldBe(requestsAfterFirstInstall);
    }

    [Fact]
    public async Task Should_Refuse_To_Install_When_The_Published_Blob_Was_Replaced()
    {
        var published = await PublishAsync();
        var catalogBytes = await File.ReadAllBytesAsync(
            published.CatalogPath,
            TestContext.Current.CancellationToken
        );
        published.Registry.Tamper(published.Reference);

        using var consumer = TestConsumerContext.Create(
            catalogBytes,
            published.Registry,
            Path.Combine(_root, "store")
        );

        var (exitCode, output) = await consumer.RunAsync(
            "install",
            $"npm/{PackageName}",
            PackageVersion
        );

        exitCode.ShouldBe(1);
        output.ShouldContain("checksum");
        (await consumer.Store.GetPackageAsync(PackageName, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    public void Dispose()
    {
        // SQLite keeps pooled file handles alive, which blocks the temp tree from being deleted.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        DeleteDirectoryTree(_root);
    }

    private sealed record PublishedArtifact(
        FakeOciRegistry Registry,
        string PackagePath,
        string CatalogPath,
        string LayerDigest,
        string Reference
    );

    /// <summary>
    /// Builds a package, pushes it, and generates the catalog, exactly as the registry workflow does.
    /// </summary>
    private async Task<PublishedArtifact> PublishAsync()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var output = Path.Combine(_root, "dist-packages");
        var catalogDirectory = Path.Combine(_root, "dist-catalog");
        var referencesPath = Path.Combine(_root, "oci-references.json");
        TestPackageFactory.Create(_root, output, PackageName, PackageVersion);
        TestOciScenario.WriteDefinition(registryRoot, PackageName);
        var registry = new FakeOciRegistry();

        var (pushCode, pushOutput) = await TestOciScenario.RunRegistryAsync(
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
        pushCode.ShouldBe(0, pushOutput);

        var (catalogCode, catalogOutput) = await TestOciScenario.RunRegistryAsync(
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
        catalogCode.ShouldBe(0, catalogOutput);

        var catalogPath = Path.Combine(catalogDirectory, "index.json");
        var catalog = await TestOciScenario.ReadCatalogAsync(catalogPath);
        var entry = catalog.Packages.Single();
        var reference = entry.OciReference.ShouldNotBeNull();
        var packagePath = Path.Combine(output, $"{PackageName}@{PackageVersion}.db");
        var layerDigest = OciNames.Digest(
            await File.ReadAllBytesAsync(packagePath, TestContext.Current.CancellationToken)
        );

        return new PublishedArtifact(registry, packagePath, catalogPath, layerDigest, reference);
    }
}
