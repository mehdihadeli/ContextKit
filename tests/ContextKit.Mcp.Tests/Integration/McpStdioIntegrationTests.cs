using System.Diagnostics;
using System.Text.Json;

namespace GroundKit.Mcp.Tests.Integration;

public sealed class McpStdioIntegrationTests
{
    [Fact]
    public async Task Should_Initialize_And_List_GroundKit_Tools_Over_Stdio()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellationToken = timeout.Token;
        var repositoryRoot = FindRepositoryRoot();
        var dataRoot = Path.Combine(Path.GetTempPath(), $"groundkit-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        Process? process = null;

        try
        {
            process = StartServer(repositoryRoot, dataRoot);
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"GroundKit.Tests\",\"version\":\"1.0\"}}}"
            );
            await process.StandardInput.FlushAsync(cancellationToken);

            var initialize = await ReadResponseAsync(process, cancellationToken);
            initialize.RootElement.GetProperty("result").GetProperty("serverInfo");

            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}"
            );
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}"
            );
            await process.StandardInput.FlushAsync(cancellationToken);

            var tools = await ReadResponseAsync(process, cancellationToken);
            var toolNames = tools
                .RootElement.GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString())
                .ToArray();

            toolNames.ShouldContain("resolve-source");
            toolNames.ShouldContain("query-docs");
            toolNames.ShouldContain("ask-docs");
            toolNames.ShouldContain("get_docs");
            toolNames.ShouldContain("find_libraries");
            toolNames.ShouldContain("search-docs");
            toolNames.ShouldContain("library_catalog");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            process?.Dispose();

            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static Process StartServer(string repositoryRoot, string dataRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add("src/ContextKit.Mcp.Host/ContextKit.Mcp.Host.csproj");
        startInfo.ArgumentList.Add("--configuration");
#if DEBUG
        startInfo.ArgumentList.Add("Debug");
#else
        startInfo.ArgumentList.Add("Release");
#endif
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--");
        startInfo.Environment["GROUNDKIT_HOME"] = dataRoot;

        var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Could not start the GroundKit MCP stdio process."
            );
        return process;
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        Process process,
        CancellationToken cancellationToken
    )
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.TrimStart().StartsWith('{'))
            {
                return JsonDocument.Parse(line);
            }
        }

        throw new InvalidOperationException(
            "GroundKit MCP stdio process exited without a response."
        );
    }

    private static string FindRepositoryRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextKit.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the GroundKit repository root.");
    }
}
