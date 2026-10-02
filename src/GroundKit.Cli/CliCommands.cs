using Spectre.Console.Cli;

namespace GroundKit.Cli;

internal static class CliCommandArguments
{
    public static int Run(CliApplication application, IEnumerable<string> arguments) =>
        application.RunAsync([.. arguments]).GetAwaiter().GetResult();

    public static void AddOption(List<string> arguments, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments.Add(name);
            arguments.Add(value);
        }
    }
}

public sealed class AddCliCommand(CliApplication application) : Command<AddCliCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SOURCE>")]
        public string Source { get; init; } = string.Empty;

        [CommandOption("-p|--path")]
        public string? Path { get; init; }

        [CommandOption("-n|--name")]
        public string? Name { get; init; }

        [CommandOption("-v|--pkg-version")]
        public string? PackageVersion { get; init; }

        [CommandOption("-s|--save")]
        public string? Save { get; init; }

        [CommandOption("-t|--tag")]
        public string? Tag { get; init; }

        [CommandOption("-c|--choose-tag")]
        public bool ChooseTag { get; init; }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string> { "add", settings.Source };
        CliCommandArguments.AddOption(arguments, "--path", settings.Path);
        CliCommandArguments.AddOption(arguments, "--name", settings.Name);
        CliCommandArguments.AddOption(arguments, "--pkg-version", settings.PackageVersion);
        CliCommandArguments.AddOption(arguments, "--save", settings.Save);
        CliCommandArguments.AddOption(arguments, "--tag", settings.Tag);
        if (settings.ChooseTag)
        {
            arguments.Add("--choose-tag");
        }

        return CliCommandArguments.Run(application, arguments);
    }
}

public sealed class ImportCliCommand(CliApplication application)
    : Command<ImportCliCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PACKAGE-FILE>")]
        public string PackageFile { get; init; } = string.Empty;
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    ) => CliCommandArguments.Run(application, ["import", settings.PackageFile]);
}

public sealed class ExportCliCommand(CliApplication application)
    : Command<ExportCliCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PACKAGE-ID>")]
        public string PackageId { get; init; } = string.Empty;

        [CommandArgument(1, "<DESTINATION>")]
        public string Destination { get; init; } = string.Empty;
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    ) => CliCommandArguments.Run(application, ["export", settings.PackageId, settings.Destination]);
}

public sealed class ListCliCommand(CliApplication application) : Command
{
    protected override int Execute(CommandContext context, CancellationToken cancellationToken) =>
        CliCommandArguments.Run(application, ["list"]);
}

public sealed class InspectCliCommand(CliApplication application)
    : Command<InspectCliCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PACKAGE-ID>")]
        public string PackageId { get; init; } = string.Empty;
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    ) => CliCommandArguments.Run(application, ["inspect", settings.PackageId]);
}

public sealed class QueryCliCommand(CliApplication application) : Command<QueryCliCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PACKAGE-ID>")]
        public string PackageId { get; init; } = string.Empty;

        [CommandArgument(1, "<TOPIC>")]
        public string Topic { get; init; } = string.Empty;

        [CommandOption("--pretty")]
        public bool Pretty { get; init; }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    ) =>
        CliCommandArguments.Run(
            application,
            [
                "query",
                settings.PackageId,
                settings.Topic,
                .. (settings.Pretty ? new[] { "--pretty" } : Array.Empty<string>()),
            ]
        );
}

public sealed class RefreshCliCommand(CliApplication application)
    : Command<RefreshCliCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PACKAGE-ID>")]
        public string PackageId { get; init; } = string.Empty;
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    ) => CliCommandArguments.Run(application, ["refresh", settings.PackageId]);
}

public sealed class RemoveCliCommand(CliApplication application)
    : Command<RemoveCliCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NAME[@VERSION]>")]
        public string PackageSelector { get; init; } = string.Empty;
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    ) => CliCommandArguments.Run(application, ["remove", settings.PackageSelector]);
}

public sealed class SearchPackagesCliCommand(CliApplication application)
    : Command<SearchPackagesCliCommand.Settings>
{
    public sealed class Settings : RegistryUrlSettings
    {
        [CommandArgument(0, "<REGISTRY>")]
        public string Registry { get; init; } = string.Empty;

        [CommandArgument(1, "<NAME>")]
        public string Name { get; init; } = string.Empty;

        [CommandArgument(2, "[VERSION]")]
        public string? Version { get; init; }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string> { "search-packages", settings.Registry, settings.Name };
        if (!string.IsNullOrWhiteSpace(settings.Version))
        {
            arguments.Add(settings.Version);
        }
        CliCommandArguments.AddOption(arguments, "--registry-url", settings.RegistryUrl);

        return CliCommandArguments.Run(application, arguments);
    }
}

public sealed class DownloadPackageCliCommand(CliApplication application)
    : Command<DownloadPackageCliCommand.Settings>
{
    public sealed class Settings : RegistryUrlSettings
    {
        [CommandArgument(0, "<REGISTRY>")]
        public string Registry { get; init; } = string.Empty;

        [CommandArgument(1, "<NAME>")]
        public string Name { get; init; } = string.Empty;

        [CommandArgument(2, "<VERSION>")]
        public string Version { get; init; } = string.Empty;
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string>
        {
            "download-package",
            settings.Registry,
            settings.Name,
            settings.Version,
        };
        CliCommandArguments.AddOption(arguments, "--registry-url", settings.RegistryUrl);
        return CliCommandArguments.Run(application, arguments);
    }
}

public sealed class InstallCliCommand(CliApplication application)
    : Command<InstallCliCommand.Settings>
{
    public sealed class Settings : RegistryUrlSettings
    {
        [CommandArgument(0, "<REGISTRY/NAME|NAME|SOURCE>")]
        public string Requested { get; init; } = string.Empty;

        [CommandArgument(1, "[VERSION]")]
        public string? Version { get; init; }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string> { "install", settings.Requested };
        if (!string.IsNullOrWhiteSpace(settings.Version))
        {
            arguments.Add(settings.Version);
        }
        CliCommandArguments.AddOption(arguments, "--registry-url", settings.RegistryUrl);

        return CliCommandArguments.Run(application, arguments);
    }
}

public abstract class RegistryUrlSettings : CommandSettings
{
    [CommandOption("--registry-url <URL>")]
    public string? RegistryUrl { get; init; }
}
