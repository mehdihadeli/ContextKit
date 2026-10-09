using GroundKit.Core.Contracts;
using GroundKit.Ingestion.Services;

namespace GroundKit.Core.Tests.Unit;

public sealed class SourceDetectorTests
{
    private readonly SourceDetector _detector = new();

    [Fact]
    public void Should_Detect_Local_Directory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);

        try
        {
            var source = _detector.Detect(path);

            source.Kind.ShouldBe(SourceKind.LocalDirectory);
            source.DisplayName.ShouldBe(Path.GetFileName(path));
            source.Location.ShouldBe(Path.GetFullPath(path));
            source.CanonicalId.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            Directory.Delete(path);
        }
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "repo")]
    [InlineData("https://github.com/owner/repo", "repo")]
    [InlineData("https://gitlab.com/team/project.git", "project")]
    [InlineData("https://bitbucket.org/user/repo", "repo")]
    public void Should_Detect_Git_Repository_Url(string url, string expectedName)
    {
        var source = _detector.Detect(url);

        source.Kind.ShouldBe(SourceKind.GitRepository);
        source.DisplayName.ShouldBe(expectedName);
        source.Location.ShouldBe(url);
    }

    [Fact]
    public void Should_Extract_GitHub_Tree_Repository_And_Ref()
    {
        var url = "https://github.com/vercel/next.js/tree/v16.0.0";

        var source = _detector.Detect(url);

        source.Kind.ShouldBe(SourceKind.GitRepository);
        source.Location.ShouldBe("https://github.com/vercel/next.js");
        source.CanonicalId.ShouldBe("next");
        source.Tag.ShouldBe("v16.0.0");
    }

    [Fact]
    public void Should_Reject_Ssh_Git_Url()
    {
        Should.Throw<InvalidOperationException>(
            () => _detector.Detect("git@github.com:org/private-docs.git")
        );
    }

    [Theory]
    [InlineData("https://example.com/llms.txt")]
    [InlineData("https://example.com/llms-full.txt")]
    [InlineData("https://example.com/")]
    public void Should_Detect_Llms_Text_Url(string url)
    {
        var source = _detector.Detect(url);

        source.Kind.ShouldBe(SourceKind.LlmsText);
    }

    [Theory]
    [InlineData("https://example.com/page.md")]
    [InlineData("https://example.com/page.html")]
    [InlineData("https://example.com/raw.txt")]
    public void Should_Detect_Raw_Document_Url(string url)
    {
        var source = _detector.Detect(url);

        source.Kind.ShouldBe(SourceKind.RawPage);
    }

    [Fact]
    public void Should_Detect_Remote_Database_Url_As_Raw_Page()
    {
        var source = _detector.Detect("https://example.com/packages/react@19.1.0.db");

        source.Kind.ShouldBe(SourceKind.RawPage);
    }

    [Fact]
    public void Should_Reject_Empty_Input()
    {
        Should.Throw<ArgumentException>(() => _detector.Detect("  "));
    }

    [Fact]
    public void Should_Reject_Unsupported_Input()
    {
        Should.Throw<InvalidOperationException>(() => _detector.Detect("not-a-path-or-url"));
    }
}
