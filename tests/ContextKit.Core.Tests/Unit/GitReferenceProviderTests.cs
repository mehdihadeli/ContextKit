using GroundKit.Ingestion.Services;

namespace GroundKit.Core.Tests.Unit;

public sealed class GitReferenceProviderTests
{
    [Fact]
    public void Should_Select_Latest_Stable_Tag_Using_Semantic_Version_Ordering()
    {
        var latest = GitReferenceProvider.SelectLatestStableTag(
            ["v1.9.0", "v1.10.0", "v2.0.0-beta.1", "latest", "mattpocock-skills@1.0.0"]
        );

        latest.ShouldBe("v1.10.0");
    }

    [Fact]
    public void Should_Ignore_Prerelease_And_Invalid_Tags_When_Selecting_Latest_Stable_Tag()
    {
        var latest = GitReferenceProvider.SelectLatestStableTag(
            ["v2.0.0-rc.1", "v1.2.3+build.4", "release", "v1.2.2"]
        );

        latest.ShouldBe("v1.2.3+build.4");
    }
}
