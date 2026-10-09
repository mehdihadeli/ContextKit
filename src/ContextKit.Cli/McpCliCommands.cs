using GroundKit.Hosting;
using Spectre.Console.Cli;

namespace GroundKit.Cli;

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