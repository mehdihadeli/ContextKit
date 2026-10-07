using GroundKit.Oci;

namespace GroundKit.Registry.Tests.Unit;

public sealed class OciReferenceTests
{
    [Theory]
    [InlineData("oci://ghcr.io/owner/repo/npm-axios@sha256:abc", "ghcr.io", "owner/repo/npm-axios", "sha256:abc")]
    [InlineData("oci://ghcr.io/owner/repo/npm-axios:latest", "ghcr.io", "owner/repo/npm-axios", "latest")]
    [InlineData("ghcr.io/owner/repo/npm-axios", "ghcr.io", "owner/repo/npm-axios", "latest")]
    [InlineData("ghcr.io/owner/repo/npm-axios:2.0", "ghcr.io", "owner/repo/npm-axios", "2.0")]
    public void Should_Parse_Oci_Locators(string value, string host, string repository, string reference)
    {
        var parsed = OciReference.Parse(value);

        parsed.Host.ShouldBe(host);
        parsed.Repository.ShouldBe(repository);
        parsed.Reference.ShouldBe(reference);
    }

    [Theory]
    [InlineData("oci://ghcr.io/owner/repo/npm@sha256:abc", true)]
    [InlineData("ghcr.io/owner/repo", false)]
    [InlineData("https://example.test/x.db", false)]
    [InlineData(null, false)]
    public void Should_Detect_Oci_Scheme(string? value, bool expected) =>
        OciReference.IsOci(value).ShouldBe(expected);

    [Fact]
    public void Should_Round_Trip_Digest_And_Tag_Locators()
    {
        var digest = OciReference.Parse("oci://ghcr.io/owner/repo/npm@sha256:abc");
        var tag = OciReference.Parse("oci://ghcr.io/owner/repo/npm:latest");

        digest.ToString().ShouldBe("oci://ghcr.io/owner/repo/npm@sha256:abc");
        tag.ToString().ShouldBe("oci://ghcr.io/owner/repo/npm:latest");
    }

    [Theory]
    [InlineData("")]
    [InlineData("ghcr.io")]
    [InlineData("oci://ghcr.io/")]
    [InlineData("oci://ghcr.io/owner/repo@")]
    [InlineData("oci://ghcr.io/owner/repo:")]
    public void Should_Reject_Malformed_Locators(string value) =>
        Should.Throw<Exception>(() => OciReference.Parse(value));

    [Theory]
    [InlineData("npm", "axios", "npm-axios")]
    [InlineData("go", "github.com/spf13/cobra", "go-github.com-spf13-cobra")]
    [InlineData("npm", "Next.js", "npm-next.js")]
    public void Should_Flatten_Repository_Names(string registry, string name, string expected) =>
        OciNames.RepositoryName(registry, name).ShouldBe(expected);

    [Fact]
    public void Should_Reject_Repository_Names_With_No_Usable_Characters() =>
        Should.Throw<ArgumentException>(() => OciNames.Sanitize("///"));

    [Fact]
    public void Should_Compute_Sha256_Digests()
    {
        var digest = OciNames.Digest("{}"u8);

        digest.ShouldBe(
            "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"
        );
    }

    [Theory]
    [InlineData("ghcr.io/owner/groundkit", "ghcr.io", "owner/groundkit")]
    [InlineData("ghcr.io/Owner/GroundKit/", "ghcr.io", "owner/groundkit")]
    [InlineData("oci://ghcr.io/owner/groundkit", "ghcr.io", "owner/groundkit")]
    public void Should_Split_Oci_Repositories(string value, string host, string prefix)
    {
        var (parsedHost, parsedPrefix) = RegistryOciPublisher.Split(value);

        parsedHost.ShouldBe(host);
        parsedPrefix.ShouldBe(prefix);
    }

    [Theory]
    [InlineData("ghcr.io")]
    [InlineData("ghcr.io/")]
    [InlineData("")]
    public void Should_Reject_Malformed_Oci_Repositories(string value) =>
        Should.Throw<ArgumentException>(() => RegistryOciPublisher.Split(value));
}
