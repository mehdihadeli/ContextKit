using GroundKit.Core.Contracts;
using GroundKit.Ingestion.Services;

namespace GroundKit.Core.Tests.Unit;

public sealed class AddWithGitRepositoryUnitTests
{
    private readonly SourceDetector _detector = new();

    [Theory]
    [InlineData("https://github.com/owner/repository", "repository")]
    [InlineData("https://gitlab.com/group/repository.git", "repository")]
    [InlineData("https://bitbucket.org/team/repository", "repository")]
    [InlineData("https://codeberg.org/team/repository", "repository")]
    public void Should_Detect_Supported_Git_Hosts(string input, string expectedDisplayName)
    {
        var source = _detector.Detect(input);

        source.Kind.ShouldBe(SourceKind.GitRepository);
        source.DisplayName.ShouldBe(expectedDisplayName);
        source.Location.ShouldBe(input);
    }

    [Theory]
    [InlineData("v1.2.3")]
    [InlineData("main")]
    [InlineData("feature/docs%2Fv2")]
    public void Should_Extract_GitHub_Tree_Tag_Or_Branch(string reference)
    {
        var source = _detector.Detect($"https://github.com/owner/repository/tree/{reference}");

        source.Kind.ShouldBe(SourceKind.GitRepository);
        source.Location.ShouldBe("https://github.com/owner/repository");
        source.Tag.ShouldBe(reference.Replace("%2F", "/"));
    }

    [Fact]
    public void Should_Detect_Local_Git_File_Url()
    {
        var source = _detector.Detect("file:///tmp/private-docs.git");

        source.Kind.ShouldBe(SourceKind.GitRepository);
        source.DisplayName.ShouldBe("private-docs");
    }
}
