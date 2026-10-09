using System.Net;
using System.Net.Sockets;
using GroundKit.Oci;
using static GroundKit.Registry.Tests.Integration.RegistryBuildAllIntegrationTests;

namespace GroundKit.Registry.Tests.Integration;

/// <summary>
/// Shared helper for every test that talks to <see href="https://ttl.sh"/>, a free anonymous OCI
/// registry whose artifact tag is a time-to-live.
/// </summary>
/// <remarks>
/// ttl.sh is used instead of a container registry that requires credentials because it removes every
/// moving part other than the registry protocol itself: no account, no token, no package visibility
/// to flip, and no cleanup page. Anything left behind expires on its own, which also makes the
/// repository safe for anyone running the tests.
/// <para>
/// Everything a ttl.sh test needs lives here so no test repeats it: the host, the time-to-live, the
/// repository and reference formats, the network preflight, and the unique repository prefix. Offline
/// tests use <see cref="Host"/> for the same reason, so both modes name one target. A live run pairs
/// this with <see cref="TtlShRun"/>.
/// </para>
/// </remarks>
internal static class TtlShRegistryHelper
{
    /// <summary>The ttl.sh host, reused as the host name of the in-memory registry.</summary>
    public const string Host = "ttl.sh";

    /// <summary>
    /// The object store ttl.sh redirects blob downloads to. Manifests and blob <em>uploads</em> are
    /// answered by ttl.sh itself, but a blob <c>GET</c> comes back as a <c>307</c> to a presigned URL
    /// on this host, so a download needs a second name resolution that an upload never does.
    /// </summary>
    public const string BlobStorageHost = "s3.us-east-1.storage.sh";

    /// <summary>
    /// The time-to-live every live run uses, in the <c>&lt;number&gt;&lt;unit&gt;</c> form ttl.sh reads
    /// from the artifact tag.
    /// </summary>
    /// <remarks>
    /// ttl.sh takes the TTL from the tag and accepts minutes up to a 24 hour maximum, defaulting to
    /// one hour when no TTL is given. Ten minutes covers a single test comfortably - a full run is
    /// measured in seconds - while keeping an abandoned artifact from lingering after a failed or
    /// cancelled job. The test never depends on the artifact outliving the run, because every
    /// artifact it pushes is consumed immediately and deleted on the way out.
    /// <para>
    /// The TTL doubles as the package version, so a test reads it as
    /// <see cref="TtlShRun.Ttl"/> rather than hardcoding a version string.
    /// </para>
    /// </remarks>
    public const string DefaultTtl = "10m";

    /// <summary>The package-manager directory the OCI repository name is derived from.</summary>
    public const string DefaultRegistry = "npm";

    /// <summary>
    /// ttl.sh needs no credentials, so live tests ride the repository's existing network switch
    /// instead of a secret. The switch alone is not enough, though: the route also has to be
    /// resolvable, or the tests fail for a reason the registry protocol cannot cause.
    /// </summary>
    public static void RequireNetwork()
    {
        SkipUnlessNetworkTestsAreEnabled();

        // A blob download resolves two names, and the second one is easy to lose. Resolvers behind an
        // ISP or a split-tunnel VPN are known to return an empty answer for the storage host's
        // double-CNAME chain even while authoritative DNS is healthy, which reaches HttpClient as
        // SocketError.HostNotFound and fails the install inside the transport, before any GroundKit
        // code runs. Windows then negative-caches that empty answer, so a retry inside the test would
        // not recover the run either. A skip that names the resolver is the useful signal here.
        foreach (var host in (string[])[Host, BlobStorageHost])
        {
            Assert.SkipWhen(
                !CanResolve(host),
                $"The local resolver cannot resolve '{host}', which ttl.sh needs to serve a blob "
                    + "download. ttl.sh and the storage host's authoritative records are fine - a "
                    + "public resolver answers for both - so this is a local resolver or split-tunnel "
                    + "VPN problem rather than a registry protocol problem. Point the machine at a "
                    + "public resolver such as 1.1.1.1, or exclude ttl.sh from the VPN, then re-run."
            );
        }
    }

    /// <summary>
    /// Resolves a host the way the HTTP stack will, so the preflight fails for the same reason a real
    /// request would.
    /// </summary>
    private static bool CanResolve(string host)
    {
        try
        {
            return Dns.GetHostAddresses(host).Length > 0;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// A unique repository prefix, so parallel runs, repeated runs, and leftovers from an earlier
    /// failure can never collide.
    /// </summary>
    public static string CreatePrefix() => "gk-" + Guid.NewGuid().ToString("n")[..10];

    /// <summary>The <c>ttl.sh/&lt;prefix&gt;</c> target accepted by the <c>push-oci</c> command.</summary>
    public static string Target(string prefix) => $"{Host}/{prefix}";

    /// <summary>
    /// The <c>&lt;prefix&gt;/&lt;registry&gt;-&lt;name&gt;</c> repository path ttl.sh stores the
    /// artifact under, matching <see cref="OciNames.RepositoryName"/>.
    /// </summary>
    public static string RepositoryPath(
        string prefix,
        string packageName,
        string registry = DefaultRegistry
    ) => $"{prefix}/{OciNames.RepositoryName(registry, packageName)}";

    /// <summary>The <c>oci://</c> locator a published artifact is addressed by.</summary>
    public static string Reference(
        string prefix,
        string packageName,
        string manifestDigest,
        string registry = DefaultRegistry
    ) => $"oci://{Host}/{RepositoryPath(prefix, packageName, registry)}@{manifestDigest}";
}
