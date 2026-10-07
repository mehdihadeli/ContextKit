using GroundKit.Registry;

namespace GroundKit.Registry.Tests.Unit;

public sealed class RegistryApplicationAliasTests
{
    [Theory]
    [InlineData("ls", "list")]
    [InlineData("v", "validate")]
    [InlineData("b", "build")]
    [InlineData("ba", "build-all")]
    [InlineData("bd", "bundle")]
    [InlineData("ib", "import-bundle")]
    public void Should_Normalize_Short_Command_Aliases(string alias, string expected)
    {
        RegistryApplication.NormalizeArguments([alias])[0].ShouldBe(expected);
    }

    [Fact]
    public void Should_Normalize_Short_Option_Aliases()
    {
        var normalized = RegistryApplication.NormalizeArguments(
            [
                "b",
                "react",
                "19.1.0",
                "-d",
                "registry",
                "-o",
                "dist",
                "-f",
                "zip",
                "-t",
                "bundle.zip",
            ]
        );

        normalized.ShouldBe(
            [
                "build",
                "react",
                "19.1.0",
                "--dir",
                "registry",
                "--output",
                "dist",
                "--format",
                "zip",
                "--destination",
                "bundle.zip",
            ]
        );
    }
}
