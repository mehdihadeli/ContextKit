namespace GroundKit.Mcp.Tests.Unit;

public sealed class McpCommandLineTests
{
    [Theory]
    [InlineData("h", "http")]
    [InlineData("http", "http")]
    public void Should_Normalize_Http_Command_Alias(string alias, string expected)
    {
        McpCommandLine.NormalizeArguments([alias])[0].ShouldBe(expected);
    }

    [Fact]
    public void Should_Normalize_Short_Option_Aliases()
    {
        var normalized = McpCommandLine.NormalizeArguments(
            ["h", "-u", "http://localhost:4000", "-l", "react,vite"]
        );

        normalized.ShouldBe(["http", "--urls", "http://localhost:4000", "--libs", "react,vite"]);
    }

    [Fact]
    public void Should_Normalize_Http_Shorthand_With_Port()
    {
        var normalized = McpCommandLine.NormalizeArguments(["--http", "4000", "--host", "0.0.0.0"]);

        normalized.ShouldBe(["http", "--port", "4000", "--host", "0.0.0.0"]);
    }
}
