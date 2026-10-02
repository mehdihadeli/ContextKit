using Spectre.Console.Cli;

namespace GroundKit.Registry;

internal static class RegistryCommandArguments
{
    public static int Run(RegistryApplication application, IEnumerable<string> arguments) =>
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

public class RegistryPathSettings : CommandSettings
{
    [CommandOption("-d|--dir")]
    public string? DirectoryPath { get; init; }
}

public sealed class ListRegistryCommand(RegistryApplication application)
    : Command<RegistryPathSettings>
{
    protected override int Execute(
        CommandContext context,
        RegistryPathSettings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string> { "list" };
        RegistryCommandArguments.AddOption(arguments, "--dir", settings.DirectoryPath);
        return RegistryCommandArguments.Run(application, arguments);
    }
}

public sealed class ValidateRegistryCommand(RegistryApplication application)
    : Command<RegistryPathSettings>
{
    protected override int Execute(
        CommandContext context,
        RegistryPathSettings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string> { "validate" };
        RegistryCommandArguments.AddOption(arguments, "--dir", settings.DirectoryPath);
        return RegistryCommandArguments.Run(application, arguments);
    }
}

public sealed class BuildRegistryCommand(RegistryApplication application)
    : Command<BuildRegistryCommand.Settings>
{
    public sealed class Settings : RegistryPathSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = string.Empty;

        [CommandArgument(1, "[VERSION]")]
        public string? Version { get; init; }

        [CommandOption("-o|--output")]
        public string? Output { get; init; }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string> { "build", settings.Name };
        if (!string.IsNullOrWhiteSpace(settings.Version))
        {
            arguments.Add(settings.Version);
        }

        RegistryCommandArguments.AddOption(arguments, "--dir", settings.DirectoryPath);
        RegistryCommandArguments.AddOption(arguments, "--output", settings.Output);
        return RegistryCommandArguments.Run(application, arguments);
    }
}

public sealed class BuildAllRegistryCommand(RegistryApplication application)
    : Command<BuildAllRegistryCommand.Settings>
{
    public sealed class Settings : RegistryPathSettings
    {
        [CommandOption("-o|--output")]
        public string? Output { get; init; }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string> { "build-all" };
        RegistryCommandArguments.AddOption(arguments, "--dir", settings.DirectoryPath);
        RegistryCommandArguments.AddOption(arguments, "--output", settings.Output);
        return RegistryCommandArguments.Run(application, arguments);
    }
}

public sealed class PublishRegistryCommand(RegistryApplication application)
    : Command<PublishRegistryCommand.Settings>
{
    public sealed class Settings : RegistryPathSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = string.Empty;

        [CommandArgument(1, "[VERSION]")]
        public string? Version { get; init; }

        [CommandOption("-o|--output")]
        public string? Output { get; init; }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string> { "publish", settings.Name };
        if (!string.IsNullOrWhiteSpace(settings.Version))
        {
            arguments.Add(settings.Version);
        }

        RegistryCommandArguments.AddOption(arguments, "--dir", settings.DirectoryPath);
        RegistryCommandArguments.AddOption(arguments, "--output", settings.Output);
        return RegistryCommandArguments.Run(application, arguments);
    }
}

public sealed class PublishAllRegistryCommand(RegistryApplication application)
    : Command<PublishAllRegistryCommand.Settings>
{
    public sealed class Settings : RegistryPathSettings
    {
        [CommandOption("-o|--output")]
        public string? Output { get; init; }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string> { "publish-all" };
        RegistryCommandArguments.AddOption(arguments, "--dir", settings.DirectoryPath);
        RegistryCommandArguments.AddOption(arguments, "--output", settings.Output);
        return RegistryCommandArguments.Run(application, arguments);
    }
}

public sealed class BundleRegistryCommand(RegistryApplication application)
    : Command<BundleRegistryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-o|--output")]
        public string? Output { get; init; }

        [CommandOption("-f|--format")]
        public string? Format { get; init; }

        [CommandOption("-t|--destination")]
        public string? Destination { get; init; }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string> { "bundle" };
        RegistryCommandArguments.AddOption(arguments, "--output", settings.Output);
        RegistryCommandArguments.AddOption(arguments, "--format", settings.Format);
        RegistryCommandArguments.AddOption(arguments, "--destination", settings.Destination);
        return RegistryCommandArguments.Run(application, arguments);
    }
}

public sealed class ImportBundleRegistryCommand(RegistryApplication application)
    : Command<ImportBundleRegistryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PATH>")]
        public string Path { get; init; } = string.Empty;

        [CommandOption("-o|--output")]
        public string? Output { get; init; }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string> { "import-bundle", settings.Path };
        RegistryCommandArguments.AddOption(arguments, "--output", settings.Output);
        return RegistryCommandArguments.Run(application, arguments);
    }
}

public sealed class CatalogIndexRegistryCommand(RegistryApplication application)
    : Command<CatalogIndexRegistryCommand.Settings>
{
    public sealed class Settings : RegistryPathSettings
    {
        [CommandOption("-o|--output")]
        public string? Output { get; init; }

        [CommandOption("-t|--destination")]
        public string? Destination { get; init; }

        [CommandOption("--base-url <URL>")]
        public string BaseUrl { get; init; } = string.Empty;
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "catalog-index" };
        RegistryCommandArguments.AddOption(arguments, "--dir", settings.DirectoryPath);
        RegistryCommandArguments.AddOption(arguments, "--output", settings.Output);
        RegistryCommandArguments.AddOption(arguments, "--destination", settings.Destination);
        RegistryCommandArguments.AddOption(arguments, "--base-url", settings.BaseUrl);
        return RegistryCommandArguments.Run(application, arguments);
    }
}

