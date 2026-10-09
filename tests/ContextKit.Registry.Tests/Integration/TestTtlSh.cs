using GroundKit.Oci;
using Microsoft.Data.Sqlite;

namespace GroundKit.Registry.Tests.Integration;

/// <summary>
/// One publish-and-consume run against ttl.sh: a uniquely addressed repository holding a single
/// package, the registry commands that publish and catalog it, and the clients used to install it.
/// </summary>
internal sealed class TtlShRun : IDisposable
{
    private readonly HttpClient _registryHttpClient;
    private bool _deleted;

    private TtlShRun(string root, string prefix, string packageName, string ttl, string registry)
    {
        Prefix = prefix;
        PackageName = packageName;
        Ttl = ttl;
        RegistryName = registry;

        RegistryRoot = Path.Combine(root, "registry");
        Output = Path.Combine(root, "dist-packages");
        CatalogDirectory = Path.Combine(root, "dist-catalog");
        ReferencesPath = Path.Combine(root, "oci-references.json");
        CatalogPath = Path.Combine(CatalogDirectory, "index.json");
        PackagePath = Path.Combine(Output, $"{packageName}@{ttl}.db");
        RepositoryPath = TtlShRegistryHelper.RepositoryPath(prefix, packageName, registry);

        TestPackageFactory.Create(root, Output, packageName, ttl);
        TestOciScenario.WriteVersionedDefinition(RegistryRoot, packageName, ttl, registry);

        _registryHttpClient = new HttpClient
        {
            BaseAddress = new Uri($"https://{TtlShRegistryHelper.Host}/"),
        };
        RegistryClient = new OciRegistryClient(
            _registryHttpClient,
            OciCredential.FromEnvironment(TtlShRegistryHelper.Host)
        );
    }

    /// <summary>
    /// Builds a package for <paramref name="packageName"/> and prepares an isolated set of
    /// directories for one live publish.
    /// </summary>
    public static TtlShRun Create(
        string root,
        string packageName,
        string? ttl = null,
        string registry = TtlShRegistryHelper.DefaultRegistry
    ) =>
        new(
            root,
            TtlShRegistryHelper.CreatePrefix(),
            packageName,
            ttl ?? TtlShRegistryHelper.DefaultTtl,
            registry
        );

    /// <summary>The unique prefix that keeps this run's artifacts separate from every other run.</summary>
    public string Prefix { get; }

    public string PackageName { get; }

    /// <summary>Both the package version and the artifact's time-to-live on ttl.sh.</summary>
    public string Ttl { get; }

    public string RegistryName { get; }

    public string RepositoryPath { get; }

    public string RegistryRoot { get; }

    public string Output { get; }

    public string CatalogDirectory { get; }

    public string CatalogPath { get; }

    public string ReferencesPath { get; }

    public string PackagePath { get; }

    /// <summary>The <c>ttl.sh/&lt;prefix&gt;</c> target passed to <c>push-oci</c>.</summary>
    public string Target => TtlShRegistryHelper.Target(Prefix);

    public OciRegistryClient RegistryClient { get; }

    /// <summary>The SHA-256 of the package bytes, which is what the manifest references.</summary>
    public string LayerDigest =>
        OciNames.Digest(File.ReadAllBytes(PackagePath));

    /// <summary>The manifest digest, populated by a successful <see cref="PushAsync"/>.</summary>
    public string ManifestDigest { get; private set; } = string.Empty;

    /// <summary>The <c>oci://</c> locator written into the catalog.</summary>
    public string Reference { get; private set; } = string.Empty;

    /// <summary>Publishes the built package and records the resulting digest and reference.</summary>
    public async Task<(int ExitCode, string Output)> PushAsync()
    {
        var result = await TestOciScenario.RunRegistryLiveAsync(
            "push-oci",
            "--dir",
            RegistryRoot,
            "--output",
            Output,
            "--oci-repository",
            Target,
            "--oci-references",
            ReferencesPath
        );

        if (result.ExitCode == 0)
        {
            var published = (
                await RegistryOciPublisher.ReadAsync(
                    ReferencesPath,
                    TestContext.Current.CancellationToken
                )
            ).Packages.Single();
            ManifestDigest = published.Digest;
            Reference = published.Reference;
            _deleted = false;
        }

        return result;
    }

    /// <summary>Generates the static catalog that addresses the published artifact by digest.</summary>
    public Task<(int ExitCode, string Output)> BuildCatalogAsync() =>
        TestOciScenario.RunRegistryLiveAsync(
            "catalog-index",
            "--dir",
            RegistryRoot,
            "--output",
            Output,
            "--destination",
            CatalogDirectory,
            "--oci-references",
            ReferencesPath
        );

    /// <summary>
    /// Builds the real consumer pipeline around the generated catalog. Package bytes come from the
    /// live registry, so the install exercises the network path an end user would hit.
    /// </summary>
    public async Task<TestConsumerContext> CreateConsumerAsync(string storeRoot) =>
        TestConsumerContext.Create(
            await File.ReadAllBytesAsync(CatalogPath, TestContext.Current.CancellationToken),
            new HttpClientHandler(),
            storeRoot
        );

    /// <summary>Records that the artifact was already removed, so dispose does not retry.</summary>
    public void MarkDeleted() => _deleted = true;

    public void Dispose()
    {
        // ttl.sh drops the artifact on its own, so deleting is only a courtesy to keep repeated runs
        // from leaving a trail of expired tags. It is skipped when the test already removed it.
        if (!_deleted && !string.IsNullOrEmpty(ManifestDigest))
        {
            try
            {
                RegistryClient
                    .DeleteManifestAsync(
                        RepositoryPath,
                        ManifestDigest,
                        TestContext.Current.CancellationToken
                    )
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    $"Could not delete {RepositoryPath}@{ManifestDigest}: {exception.Message}. "
                        + "ttl.sh expires it automatically."
                );
            }
        }

        _registryHttpClient.Dispose();
    }
}
