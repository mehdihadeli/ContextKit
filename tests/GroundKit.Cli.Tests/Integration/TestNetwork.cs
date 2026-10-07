using System.Net;
using System.Net.Sockets;

namespace GroundKit.Tests.Integration;

/// <summary>
/// Gate for integration tests that download from the public internet.
/// </summary>
/// <remarks>
/// These tests reach real sites, so they are opt-in with <c>GROUNDKIT_NETWORK_TESTS=1</c> and stay out
/// of the default run, matching the registry test project. Without the gate a plain
/// <c>dotnet test</c> depends on third-party availability, and a failure against
/// <c>agentgateway.dev</c> or <c>github.com</c> says nothing about GroundKit.
/// </remarks>
internal static class TestNetwork
{
    /// <summary>The switch that opts a run into tests needing the public internet.</summary>
    internal const string SwitchName = "GROUNDKIT_NETWORK_TESTS";

    /// <summary>Skips the current test unless the run opted into network tests.</summary>
    internal static void SkipUnlessNetworkTestsAreEnabled() =>
        Assert.SkipUnless(
            string.Equals(
                Environment.GetEnvironmentVariable(SwitchName),
                "1",
                StringComparison.Ordinal
            ),
            $"Set {SwitchName}=1 to run tests that download from the public internet."
        );

    /// <summary>
    /// Skips the current test unless the run opted into network tests <em>and</em> the host behind
    /// <paramref name="url"/> resolves.
    /// </summary>
    /// <remarks>
    /// The resolution check keeps a broken local resolver from being reported as a GroundKit failure.
    /// A test that cannot reach its site should say so, instead of surfacing later as an empty package
    /// store or a download error that looks like a product bug.
    /// </remarks>
    internal static void RequireUrl(string url)
    {
        SkipUnlessNetworkTestsAreEnabled();

        var host = new Uri(url, UriKind.Absolute).Host;
        Assert.SkipWhen(
            !CanResolve(host),
            $"The local resolver cannot resolve '{host}', which this test downloads from. Point the "
                + "machine at a working resolver (for example 1.1.1.1) or disconnect the VPN, then "
                + "re-run."
        );
    }

    /// <summary>Resolves a host the way the HTTP stack will, so the gate fails for the same reason.</summary>
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
}
