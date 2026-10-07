using GroundKit.Oci;
using Microsoft.Data.Sqlite;
using static GroundKit.Registry.Tests.Integration.RegistryBuildAllIntegrationTests;

namespace GroundKit.Registry.Tests.Integration;

/// <summary>
/// Publishes a real package to <see href="https://ttl.sh"/>, then consumes the published artifact
/// through the real <c>groundkit install</c>, <c>list</c>, and <c>query</c> commands.
/// </summary>
/// <remarks>
/// This is the only place the registry protocol runs against a real server, so it is opt-in with the
/// repository's existing network switch:
/// <code>
/// GROUNDKIT_NETWORK_TESTS=1 dotnet test --project tests/GroundKit.Registry.Tests \
///   --filter-class "GroundKit.Registry.Tests.Integration.TtlShPublishIntegrationTests"
/// </code>
/// ttl.sh needs no account, token, or secret, so anyone can run this and nothing has to be undone in
/// a settings page. The TTL is carried by the artifact tag, so the package version under test is the
/// TTL itself. Each run uses a unique repository prefix and deletes its artifact when it finishes;
/// anything left behind expires on its own. See <see cref="TtlShRegistryHelper"/> for the shared helpers.
/// </remarks>
[Collection(RegistryConsoleCollection.Name)]
public sealed class TtlShPublishIntegrationTests : IDisposable
{
    private const string PackageName = "groundkit-ttl-e2e";

    /// <summary>Matches the document <see cref="TestPackageFactory"/> writes into the package.</summary>
    private const string PackageContent = PackageName + " keeps this document.";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "groundkit-registry-ttl-sh",
        Guid.NewGuid().ToString("n")
    );

    public TtlShPublishIntegrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Should_Publish_To_Ttl_Sh_Then_Consume_The_Documents_Through_The_Cli()
    {
        TtlShRegistryHelper.RequireNetwork();

        using var run = TtlShRun.Create(_root, PackageName);
        var (pushCode, pushOutput) = await run.PushAsync();
        pushCode.ShouldBe(0, pushOutput);
        pushOutput.ShouldContain("Pushed 1 package(s)");

        // Publishing must produce a digest-pinned locator, which is what makes it verifiable.
        run.Reference.ShouldBe(
            TtlShRegistryHelper.Reference(run.Prefix, PackageName, run.ManifestDigest)
        );
        run.ManifestDigest.ShouldStartWith("sha256:");

        var (catalogCode, catalogOutput) = await run.BuildCatalogAsync();
        catalogCode.ShouldBe(0, catalogOutput);
        var entry = (await TestOciScenario.ReadCatalogAsync(run.CatalogPath)).Packages.Single();
        entry.OciReference.ShouldBe(run.Reference);
        entry.Sha256.ShouldBe(run.LayerDigest[("sha256:".Length)..]);
        entry.Size.ShouldBe(new FileInfo(run.PackagePath).Length);

        // The artifact must already be readable anonymously, by digest, before installing.
        var layer = await run.RegistryClient.ResolveLayerAsync(
            run.RepositoryPath,
            run.ManifestDigest,
            TestContext.Current.CancellationToken
        );
        layer.MediaType.ShouldBe(OciNames.PackageMediaType);
        layer.Digest.ShouldBe(run.LayerDigest);
        layer.Size.ShouldBe(entry.Size);

        using var consumer = await run.CreateConsumerAsync(Path.Combine(_root, "store"));

        // Consume the published artifact with the real CLI, exactly as an end user would.
        var (installCode, installOutput) = await consumer.RunAsync(
            "install",
            $"npm/{PackageName}",
            run.Ttl
        );
        installCode.ShouldBe(0, installOutput);
        installOutput.ShouldContain("Built and installed package:", Case.Insensitive);
        installOutput.ShouldNotContain("Registry unavailable", Case.Insensitive);

        // `list` must report the package the registry served.
        var (listCode, listOutput) = await consumer.RunAsync("list");
        listCode.ShouldBe(0, listOutput);
        listOutput.ShouldContain(PackageName);
        listOutput.ShouldContain(run.Ttl);

        // `query` must return the published document body, which is the point of the artifact.
        var (queryCode, queryOutput) = await consumer.RunAsync(
            "query",
            PackageName,
            "keeps this document",
            "--pretty"
        );
        queryCode.ShouldBe(0, queryOutput);
        queryOutput.ShouldContain(PackageContent);
        queryOutput.ShouldNotContain("No matching documentation found");

        var installed = await consumer.Store.GetPackageAsync(
            PackageName,
            TestContext.Current.CancellationToken
        );
        installed.ShouldNotBeNull();
        installed.Version.ShouldBe(run.Ttl);
        installed.PackagePath.ShouldNotBeNullOrWhiteSpace();

        // The installed bytes must be exactly what the registry served.
        var pulled = Path.Combine(_root, "pulled.db");
        await run.RegistryClient.DownloadBlobAsync(
            run.RepositoryPath,
            layer.Digest,
            pulled,
            entry.Size,
            TestContext.Current.CancellationToken
        );
        (await File.ReadAllBytesAsync(pulled, TestContext.Current.CancellationToken))
            .ShouldBe(await File.ReadAllBytesAsync(run.PackagePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_Serve_A_Repeat_Install_From_The_Local_Store()
    {
        TtlShRegistryHelper.RequireNetwork();

        using var run = TtlShRun.Create(_root, PackageName);
        (await run.PushAsync()).ExitCode.ShouldBe(0);
        (await run.BuildCatalogAsync()).ExitCode.ShouldBe(0);
        using var consumer = await run.CreateConsumerAsync(Path.Combine(_root, "store"));

        (await consumer.RunAsync("install", $"npm/{PackageName}", run.Ttl)).ExitCode.ShouldBe(0);

        // Second install resolves locally, so it still succeeds even after the artifact is gone.
        await run.RegistryClient.DeleteManifestAsync(
            run.RepositoryPath,
            run.ManifestDigest,
            TestContext.Current.CancellationToken
        );
        run.MarkDeleted();

        var (exitCode, output) = await consumer.RunAsync("install", $"npm/{PackageName}", run.Ttl);

        exitCode.ShouldBe(0, output);
        (await consumer.RunAsync("list")).Output.ShouldContain(PackageName);
    }

    [Fact]
    public async Task Should_Fail_The_Install_When_The_Published_Artifact_Expires()
    {
        TtlShRegistryHelper.RequireNetwork();

        using var run = TtlShRun.Create(_root, PackageName);
        (await run.PushAsync()).ExitCode.ShouldBe(0);
        (await run.BuildCatalogAsync()).ExitCode.ShouldBe(0);

        // Removing the manifest simulates the artifact expiring, which is the one failure mode a
        // time-limited registry introduces. The catalog still lists it, so the install must report
        // the broken download instead of quietly falling back to building from source.
        await run.RegistryClient.DeleteManifestAsync(
            run.RepositoryPath,
            run.ManifestDigest,
            TestContext.Current.CancellationToken
        );
        run.MarkDeleted();

        using var consumer = await run.CreateConsumerAsync(Path.Combine(_root, "store"));
        var (exitCode, output) = await consumer.RunAsync(
            "install",
            $"npm/{PackageName}",
            run.Ttl
        );

        exitCode.ShouldBe(1);
        output.ShouldContain("Error:");
        output.ShouldNotContain("Registry unavailable", Case.Insensitive);

        // Pin the failure to the removed artifact. Without this, a transport error would satisfy the
        // assertions above and the test would pass for the wrong reason.
        output.ShouldNotContain("No such host", Case.Insensitive);
        (await consumer.Store.GetPackageAsync(PackageName, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        DeleteDirectoryTree(_root);
    }
}
