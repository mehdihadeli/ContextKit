using GroundKit.Core.Contracts;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GroundKit.Cli;

public sealed class CatalogCommand : Command<CatalogCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[QUERY]")]
        public string? Query { get; init; }
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        var query = settings.Query?.Trim();
        var entries = LibraryCatalog
            .StarterLibraries.Where(entry =>
                string.IsNullOrWhiteSpace(query)
                || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();

        AnsiConsole.Write(new FigletText("GroundKit").Color(Color.Aqua));
        AnsiConsole.MarkupLine("[grey]Curated documentation sources for local MCP retrieval[/]");
        AnsiConsole.WriteLine();

        if (entries.Length == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No catalog entry matched:[/] {Markup.Escape(query ?? string.Empty)}"
            );
            return 1;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Library");
        table.AddColumn("Registry");
        table.AddColumn("Description");
        table.AddColumn("Docs path");

        foreach (var entry in entries)
        {
            table.AddRow(
                $"[aqua]{Markup.Escape(entry.Name)}[/]",
                Markup.Escape(entry.Registry),
                Markup.Escape(entry.Description),
                Markup.Escape(
                    string.IsNullOrWhiteSpace(entry.DocsPath) ? "repository root" : entry.DocsPath
                )
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"\n[grey]{entries.Length} starter source(s). Build one with:[/] [white]ck add <repository-url>[/]"
        );
        return 0;
    }
}
