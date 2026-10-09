using GroundKit.Hosting;
using Spectre.Console.Cli;

namespace GroundKit.Mcp;

internal static class McpCommandLine
{
    public static string[] NormalizeArguments(string[] args)
    {
        if (args.Length == 0) return args;
        var normalized = args.ToArray();
        if (string.Equals(normalized[0], "--http", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = new List<string> { "http" };
            var index = 1;
            if (index < normalized.Length && int.TryParse(normalized[index], out _))
            {
                arguments.Add("--port");
                arguments.Add(normalized[index]);
                index++;
            }
            arguments.AddRange(normalized[index..]);
            normalized = arguments.ToArray();
        }
        normalized[0] = NormalizeCommand(normalized[0]);
        for (var index = 1; index < normalized.Length; index++)
            normalized[index] = NormalizeOption(normalized[index]);
        return normalized;
    }

    public static CommandApp CreateCommandApp()
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("groundkit-mcp");
            config.Settings.StrictParsing = true;
            config.AddExample("-l", "react,vite");
            config.AddExample("--http", "4000", "--host", "0.0.0.0");
            config.AddCommand<HttpMcpCommand>("http")
                .WithAlias("h")
                .WithDescription("Start the HTTP MCP host and expose `/mcp` (alias: h).")
                .WithExample("h", "-u", "http://localhost:4000", "-l", "react,vite");
        });
        app.SetDefaultCommand<StdioMcpCommand>()
            .WithDescription("Start the MCP server over stdio. Use -l or --libs to restrict packages.");
        return app;
    }

    public static string NormalizeCommand(string command) => command.ToLowerInvariant() switch
    {
        "h" => "http",
        _ => command,
    };

    public static string NormalizeOption(string option) => option.ToLowerInvariant() switch
    {
        "-l" => "--libs",
        "-u" => "--urls",
        _ => option,
    };

    internal static WebApplication CreateHttpApp(string? libraries, string? urls, string? host, int? port) =>
        GroundKitMcpHosting.CreateHttpApp(libraries, urls, host, port);
}

public sealed class StdioMcpCommand : AsyncCommand<StdioMcpCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-l|--libs")]
        public string? Libraries { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        GroundKitMcpHosting.RunStdioAsync(settings.Libraries, cancellationToken);
}

public sealed class HttpMcpCommand : AsyncCommand<HttpMcpCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-l|--libs")]
        public string? Libraries { get; init; }
        [CommandOption("-u|--urls")]
        public string? Urls { get; init; }
        [CommandOption("--host")]
        public string? Host { get; init; }
        [CommandOption("--port")]
        public int? Port { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        GroundKitMcpHosting.RunHttpAsync(settings.Libraries, settings.Urls, settings.Host, settings.Port, cancellationToken);
}