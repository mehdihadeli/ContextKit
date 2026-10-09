using System.ComponentModel;
using Spectre.Console.Cli;

namespace GroundKit.Cli;

public sealed class SemanticCliCommand(CliApplication application) : Command<SemanticCliCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ACTION>")]
        public string Action { get; init; } = string.Empty;

        [CommandArgument(1, "[ARGUMENTS]")]
        public string[] Arguments { get; init; } = [];

        [CommandOption("--model <MODEL>")]
        public string? Model { get; init; }

        [CommandOption("--mode <MODE>")]
        public string? Mode { get; init; }

        [CommandOption("--bundle <DIRECTORY>")]
        [Description("Import an explicitly trusted local provider bundle directory for development/offline setup.")]
        public string? Bundle { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "semantic", settings.Action };
        arguments.AddRange(settings.Arguments);
        CliCommandArguments.AddOption(arguments, "--model", settings.Model);
        CliCommandArguments.AddOption(arguments, "--mode", settings.Mode);
        CliCommandArguments.AddOption(arguments, "--bundle", settings.Bundle);
        return CliCommandArguments.Run(application, arguments);
    }
}