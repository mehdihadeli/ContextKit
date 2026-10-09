using System.Text.Json;
using System.Text.RegularExpressions;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Ingestion.Services;
using GroundKit.Semantic;
using Spectre.Console;

namespace GroundKit.Cli;

public sealed class CliApplication(
    IDocumentPackageBuilder packageBuilder,
    IPackageStore packageStore,
    IContextRegistryClient? registryClient = null,
    IPackageDownloadService? packageDownloadService = null,
    IGitReferenceProvider? gitReferenceProvider = null,
    SemanticRuntime? semanticRuntime = null
)
{
    internal static string[] NormalizeArguments(string[] args)
    {
        if (args.Length == 0)
        {
            return args;
        }

        var normalized = args.ToArray();
        normalized[0] = NormalizeCommand(args[0]);

        for (var index = 1; index < normalized.Length; index++)
        {
            normalized[index] = NormalizeOption(normalized[index]);
        }

        return normalized;
    }

    /// <summary>
    /// Applies the per-invocation <c>--registry-url</c> override by way of the environment, and
    /// returns the arguments with the option removed.
    /// </summary>
    /// <remarks>
    /// This has to run before <c>AddGroundKitServices</c>: that call resolves
    /// <c>GroundKitOptions.Load()</c> eagerly, and the registry client takes its base address from
    /// the options when it is resolved. Applying the override inside a command is too late, because
    /// the client the command receives was already built against the default endpoint.
    /// </remarks>
    internal static string[] ApplyRegistryUrlOverride(string[] args)
    {
        var registryUrl = TryReadOption(args, "--registry-url");
        if (string.IsNullOrWhiteSpace(registryUrl))
        {
            return args;
        }

        Environment.SetEnvironmentVariable("GROUNDKIT_REGISTRY_URL", registryUrl);
        return RemoveOption(args, "--registry-url");
    }

    public async Task<int> RunAsync(string[] args)
    {
        args = NormalizeArguments(ApplyRegistryUrlOverride(args));

        if (args.Length == 0)
        {
            ShowHelp();
            return 0;
        }

        var command = NormalizeCommand(args[0]);

        try
        {
            return command switch
            {
                "add" => await RunAddAsync(args),
                "import" => await RunImportAsync(args),
                "export" => await RunExportAsync(args),
                "list" => await RunListAsync(),
                "inspect" => await RunInspectAsync(args),
                "query" => await RunQueryAsync(args),
                "semantic" => await RunSemanticAsync(args),
                "refresh" => await RunRefreshAsync(args),
                "remove" => await RunRemoveAsync(args),
                "search-packages" => await RunSearchPackagesAsync(args),
                "download-package" => await RunDownloadPackageAsync(args),
                "install" => await RunInstallAsync(args),
                _ => ShowUnknownCommand(command),
            };
        }
        catch (Exception exception)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(exception.Message)}");
            return 1;
        }
    }

    private async Task<int> RunAddAsync(string[] args)
    {
        if (args.Length < 2)
        {
            AnsiConsole.Markup("[red]Usage:[/] ");
            AnsiConsole.WriteLine(
                "add <source> [--path <path>] [--name <name>] [--pkg-version <version>] [--save <path>] [--tag <tag>] [--choose-tag]"
            );
            return 1;
        }

        var catalogEntry = LibraryCatalog.Find(args[1]);
        var source = catalogEntry?.Repository ?? args[1];
        var docsPath =
            TryReadOption(args, "--path")
            ?? TryReadOption(args, "--docs-path")
            ?? catalogEntry?.DocsPath;
        var packageName = TryReadOption(args, "--name");
        var packageVersion = TryReadOption(args, "--pkg-version");
        var savePath = TryReadOption(args, "--save");
        var gitRef = TryReadOption(args, "--tag");
        var chooseTag = HasFlag(args, "--choose-tag");

        if (
            packageDownloadService is not null
            && (IsLocalPackageFile(source) || IsRemotePackageUrl(source))
        )
        {
            var importedPackagePath = await packageDownloadService.InstallAsync(
                source,
                packageVersion,
                CancellationToken.None
            );
            AnsiConsole.MarkupLine(
                $"[green]Added package:[/] {Markup.Escape(importedPackagePath)}"
            );
            return 0;
        }

        if (gitRef is null && IsGitRepositoryUrl(source) && gitReferenceProvider is not null)
        {
            var tags = await gitReferenceProvider.GetTagsAsync(source);
            if (chooseTag && tags.Count > 0 && AnsiConsole.Profile.Capabilities.Interactive)
            {
                var prompt = new SelectionPrompt<string>()
                    .Title("Select a [green]Git tag[/] or use the latest stable tag:")
                    .PageSize(12)
                    .AddChoices(["Use latest stable tag", .. tags]);
                var selection = AnsiConsole.Prompt(prompt);
                gitRef =
                    selection == "Use latest stable tag"
                        ? GitReferenceProvider.SelectLatestStableTag(tags)
                        : selection;
            }
            else
            {
                gitRef = GitReferenceProvider.SelectLatestStableTag(tags);
            }
        }

        var buildResult = await packageBuilder.BuildAsync(
            source,
            docsPath,
            CancellationToken.None,
            packageVersion,
            gitRef,
            packageName
        );
        var packagePath = await packageStore.SaveAsync(buildResult);
        var savedCopyPath = savePath is null
            ? null
            : await packageStore.ExportAsync(buildResult.Manifest.PackageId, savePath);

        AnsiConsole.MarkupLine(
            $"[green]Added package:[/] {Markup.Escape(buildResult.Manifest.PackageId)}"
        );
        AnsiConsole.MarkupLine($"Path: {Markup.Escape(packagePath)}");
        if (savedCopyPath is not null)
        {
            AnsiConsole.MarkupLine($"Saved copy: {Markup.Escape(savedCopyPath)}");
        }
        AnsiConsole.MarkupLine(
            $"Documents: {buildResult.Manifest.DocumentCount}, Sections: {buildResult.Manifest.ChunkCount}"
        );

        if (buildResult.Warnings.Count > 0)
        {
            var warningTable = new Table().AddColumn("Warning").AddColumn("Source");
            foreach (var warning in buildResult.Warnings)
            {
                warningTable.AddRow(warning.Message, warning.SourcePath ?? "-");
            }

            AnsiConsole.Write(warningTable);
        }

        return 0;
    }

    private async Task<int> RunListAsync()
    {
        var packages = await packageStore.ListAsync();
        if (packages.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No packages installed yet.[/]");
            return 0;
        }

        var packageRows = packages
            .Select(package =>
            {
                var packageSizeBytes = File.Exists(package.PackagePath)
                    ? new FileInfo(package.PackagePath).Length
                    : 0;
                return new
                {
                    Package = package,
                    SizeBytes = packageSizeBytes,
                    SizeLabel = packageSizeBytes > 0 ? FormatBytes(packageSizeBytes) : "unknown",
                };
            })
            .ToArray();

        var table = new Table
        {
            Title = new TableTitle("[aqua]Installed packages[/]"),
            Border = TableBorder.Rounded,
            Expand = true,
        };
        table.AddColumn(new TableColumn("[grey]Package[/]"));
        table.AddColumn(new TableColumn("[grey]Version[/]"));
        table.AddColumn(new TableColumn("[grey]Size[/]").RightAligned());
        table.AddColumn(new TableColumn("[grey]Documents[/]").RightAligned());
        table.AddColumn(new TableColumn("[grey]Sections[/]").RightAligned());

        foreach (var row in packageRows)
        {
            table.AddRow(
                $"[white]{Markup.Escape(row.Package.PackageId)}[/]",
                Markup.Escape(row.Package.Version ?? "dev"),
                row.SizeLabel,
                $"{row.Package.DocumentCount:N0}",
                $"{row.Package.ChunkCount:N0}"
            );
        }

        AnsiConsole.Write(table);

        var totalBytes = packageRows.Sum(row => row.SizeBytes);
        var totalDocuments = packageRows.Sum(row => row.Package.DocumentCount);
        var totalSections = packageRows.Sum(row => row.Package.ChunkCount);
        var summary = new Grid();
        summary.AddColumn();
        summary.AddColumn();
        summary.AddRow("Packages", packages.Count.ToString("N0"));
        summary.AddRow("Size", totalBytes > 0 ? FormatBytes(totalBytes) : "unknown");
        summary.AddRow("Documents", totalDocuments.ToString("N0"));
        summary.AddRow("Sections", totalSections.ToString("N0"));

        AnsiConsole.Write(
            new Panel(summary).Header("[grey]Totals[/]").Border(BoxBorder.Rounded).Expand()
        );

        return 0;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static bool IsGitRepositoryUrl(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (input.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return uri.Host is "github.com" or "gitlab.com" or "bitbucket.org" or "codeberg.org"
            && uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length
                >= 2
            && !uri.AbsolutePath.Contains("/tree/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRemotePackageUrl(string input)
    {
        if (
            !Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
        )
        {
            return false;
        }

        if (uri.AbsolutePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault();
        return fileName is not null
            && Regex.IsMatch(
                fileName,
                @"^[^/]+@v?\d+(?:\.\d+){1,3}$",
                RegexOptions.CultureInvariant
            );
    }

    private static bool IsLocalPackageFile(string input) =>
        File.Exists(input) && input.EndsWith(".db", StringComparison.OrdinalIgnoreCase);

    private async Task<int> RunImportAsync(string[] args)
    {
        if (args.Length < 2)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] import <package-file>");
            return 1;
        }

        var importedPath = await packageStore.ImportAsync(args[1]);
        AnsiConsole.MarkupLine($"[green]Imported package:[/] {Markup.Escape(importedPath)}");
        return 0;
    }

    private async Task<int> RunExportAsync(string[] args)
    {
        if (args.Length < 3)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] export <package-id> <destination>");
            return 1;
        }

        var exportPath = await packageStore.ExportAsync(args[1], args[2]);
        AnsiConsole.MarkupLine($"[green]Exported package:[/] {Markup.Escape(exportPath)}");
        return 0;
    }

    private async Task<int> RunInspectAsync(string[] args)
    {
        if (args.Length < 2)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] inspect <package-id>");
            return 1;
        }

        var package = await packageStore.GetPackageAsync(args[1]);
        if (package is null)
        {
            AnsiConsole.MarkupLine($"[yellow]Package not found:[/] {Markup.Escape(args[1])}");
            return 1;
        }

        var source = await packageStore.GetSourceAsync(args[1]);
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("Package", package.PackageId);
        grid.AddRow("Display name", package.DisplayName);
        grid.AddRow("Version", package.Version ?? "dev");
        grid.AddRow("Documents", package.DocumentCount.ToString());
        grid.AddRow("Chunks", package.ChunkCount.ToString());
        grid.AddRow("Warnings", package.WarningCount.ToString());
        grid.AddRow("Built at", package.BuiltAt.ToString("u"));
        grid.AddRow("Path", package.PackagePath);

        if (source is not null)
        {
            grid.AddRow("Source kind", source.Kind.ToString());
            grid.AddRow("Source location", source.Location);
            grid.AddRow("Docs path", source.DocsPath ?? "-");
            grid.AddRow("Source version", source.Version ?? "-");
            grid.AddRow("Tag", source.Tag ?? "-");
            grid.AddRow("Branch", source.Branch ?? "-");
            grid.AddRow("Fingerprint", source.Fingerprint ?? "-");
        }

        AnsiConsole.Write(new Panel(grid).Header($"Package {package.PackageId}").Expand());
        return 0;
    }

    private async Task<int> RunQueryAsync(string[] args)
    {
        var pretty = HasFlag(args, "--pretty");
        var allowInstall = !HasFlag(args, "--no-install");
        var searchModeValue = OptionValue(args, "--search-mode");
        var retrieval = new RetrievalOptions(
            SearchMode: searchModeValue is null ? null : SemanticRuntime.ParseMode(searchModeValue),
            IncludeAdjacentChunks: HasFlag(args, "--include-adjacent"),
            IncludeReferences: HasFlag(args, "--include-references")
        );
        var libraryHints = SplitLibraryHints(OptionValue(args, "--libraries"));
        var positionals = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (IsQueryOption(argument))
            {
                continue;
            }

            // `--libraries <names>` carries a value, so both the option and its argument must be kept
            // out of the question text.
            if (string.Equals(argument, "--libraries", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "--search-mode", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            positionals.Add(argument);
        }

        if (positionals.Count == 0)
        {
            ShowQueryUsage();
            return 1;
        }

        // Explicit hints make the whole remaining text a question: the hints, not the words, decide
        // which packages are searched.
        if (libraryHints.Count > 0)
        {
            return await RunMultiLibraryQueryAsync(
                string.Join(' ', positionals),
                libraryHints,
                pretty,
                allowInstall,
                retrieval
            );
        }

        string selector;
        string topic;
        if (positionals.Count == 1)
        {
            // A single argument is a natural-language question, so the package is whatever it names.
            // `query "what are components in angular?"` is the same as `query angular "what are
            // components in angular?"` once the mention is found.
            var detected = await DetectPackageFromQuestionAsync(positionals[0]);
            if (detected is null)
            {
                await ShowUndetectedPackageGuidanceAsync(positionals[0]);
                return 1;
            }

            selector = detected;
            topic = positionals[0];
        }
        else
        {
            selector = positionals[0];
            topic = string.Join(' ', positionals.Skip(1));
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            ShowQueryUsage();
            return 1;
        }

        var resolvedSelector = selector;
        var installed = await packageStore.GetPackageAsync(selector);
        if (installed is null)
        {
            var installedSelector = await InstallMissingPackageForQueryAsync(
                selector,
                allowInstall
            );
            if (installedSelector is null)
            {
                return 1;
            }

            resolvedSelector = installedSelector;
        }
        else
        {
            // Query the exact stored identity instead of the raw selector: the store resolves a
            // partial version such as `react@18` to a real package, and the answer should say which
            // version it came from.
            resolvedSelector = DescribeInstalled(selector, installed);
        }

        var response = await packageStore.QueryAsync(
            new DocsQueryRequest(resolvedSelector, topic, retrieval)
        );

        if (!pretty)
        {
            var payload = new
            {
                packageId = response.PackageId,
                version = response.Version,
                totalTokens = response.TotalTokens,
                hits = response.Hits.Select(hit => new
                {
                    documentTitle = hit.DocumentTitle,
                    sectionTitle = hit.SectionTitle,
                    content = hit.Content,
                    tokenEstimate = hit.TokenEstimate,
                    hasCode = hit.HasCode,
                    score = hit.Score,
                }),
            };
            Console.WriteLine(JsonSerializer.Serialize(payload));
            return 0;
        }

        if (response.Hits.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching documentation found.[/]");
            return 0;
        }

        foreach (var hit in response.Hits)
        {
            AnsiConsole.Write(
                new Panel(Markup.Escape(hit.Content))
                    .Header(Markup.Escape($"{hit.DocumentTitle} / {hit.SectionTitle}"))
                    .Expand()
            );
            AnsiConsole.MarkupLine(
                $"Tokens: {hit.TokenEstimate} | Score: {hit.Score:F3} | HasCode: {hit.HasCode}"
            );
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine($"[green]Total tokens returned:[/] {response.TotalTokens}");
        return 0;
    }

    private static void ShowQueryUsage()
    {
        // Plain WriteLine: the bracketed optional arguments would otherwise be read as markup.
        AnsiConsole.Markup("[red]Usage:[/] ");
        AnsiConsole.WriteLine("query <package-id> <topic> [--pretty] [--no-install]");
        AnsiConsole.Markup("   or: ");
        AnsiConsole.WriteLine("query \"<question>\" [--pretty] [--no-install]");
        AnsiConsole.Markup("   or: ");
        AnsiConsole.WriteLine(
            "query \"<question>\" --libraries <a,b,c,d> [--pretty] [--no-install]"
        );
    }

    /// <summary>How many libraries one question may span, matching the MCP search-docs limit.</summary>
    private const int MaxQueryLibraries = 4;

    /// <summary>
    /// Searches several packages for one question and returns a single fused ranking. Each library is
    /// resolved on its own, so one that cannot be installed does not stop the others from answering.
    /// </summary>
    private async Task<int> RunMultiLibraryQueryAsync(
        string question,
        IReadOnlyList<string> libraryHints,
        bool pretty,
        bool allowInstall,
        RetrievalOptions retrieval
    )
    {
        var selectors = await ResolveMultiLibrarySelectorsAsync(libraryHints);
        var responses = new List<DocsQueryResponse>();

        foreach (var selector in selectors)
        {
            var querySelector = selector;
            if (await packageStore.GetPackageAsync(selector) is null)
            {
                var installedSelector = await InstallMissingPackageForQueryAsync(
                    selector,
                    allowInstall
                );
                if (installedSelector is null)
                {
                    continue;
                }

                querySelector = installedSelector;
            }

            responses.Add(
                await packageStore.QueryAsync(new DocsQueryRequest(querySelector, question, retrieval))
            );
        }

        if (responses.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]None of the requested libraries could be searched.[/]"
            );
            return 1;
        }

        var limit = new RetrievalOptions();
        var hits = RankFusion.Fuse(responses, limit.MaxTokens);
        var totalTokens = hits.Sum(hit => hit.Hit.TokenEstimate);

        if (!pretty)
        {
            var payload = new
            {
                question,
                packages = responses.Select(response => response.PackageId).ToArray(),
                totalTokens,
                hits = hits.Select(hit => new
                {
                    packageId = hit.PackageId,
                    version = hit.Version,
                    documentTitle = hit.Hit.DocumentTitle,
                    sectionTitle = hit.Hit.SectionTitle,
                    content = hit.Hit.Content,
                    tokenEstimate = hit.Hit.TokenEstimate,
                    hasCode = hit.Hit.HasCode,
                    score = hit.Hit.Score,
                }),
            };
            Console.WriteLine(JsonSerializer.Serialize(payload));
            return 0;
        }

        if (hits.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching documentation found.[/]");
            return 0;
        }

        foreach (var hit in hits)
        {
            AnsiConsole.Write(
                new Panel(Markup.Escape(hit.Hit.Content))
                    .Header(
                        Markup.Escape(
                            $"{hit.PackageId} / {hit.Hit.DocumentTitle} / {hit.Hit.SectionTitle}"
                        )
                    )
                    .Expand()
            );
            AnsiConsole.MarkupLine(
                $"Tokens: {hit.Hit.TokenEstimate} | Score: {hit.Hit.Score:F3} | HasCode: {hit.Hit.HasCode}"
            );
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine($"[green]Total tokens returned:[/] {totalTokens}");
        return 0;
    }

    private async Task<int> RunSemanticAsync(string[] args)
    {
        var runtime = semanticRuntime ?? throw new InvalidOperationException("Semantic runtime services are unavailable.");
        var action = args.ElementAtOrDefault(1)?.ToLowerInvariant();
        switch (action)
        {
            case "provider" when args.ElementAtOrDefault(2) == "install" && args.ElementAtOrDefault(3) == "onnx":
                await runtime.InstallProviderAsync(TryReadOption(args, "--bundle"));
                AnsiConsole.WriteLine("ONNX provider installed.");
                return 0;
            case "model" when args.ElementAtOrDefault(2) == "list":
                foreach (var model in SemanticRuntime.Models)
                    AnsiConsole.WriteLine($"{model.Id} | English | {model.Dimensions} dimensions | {model.Assets.Sum(asset => asset.Size):N0} bytes | {model.License}");
                return 0;
            case "model" when args.ElementAtOrDefault(2) == "install" && args.Length > 3:
                await runtime.InstallModelAsync(args[3]);
                AnsiConsole.WriteLine($"Model installed: {args[3]}.");
                return 0;
            case "model" when args.ElementAtOrDefault(2) == "use" && args.Length > 3:
                await runtime.UseModelAsync(args[3]);
                AnsiConsole.WriteLine($"Selected model: {args[3]}.");
                return 0;
            case "enable":
                var mode = SemanticRuntime.ParseMode(TryReadOption(args, "--mode") ?? "hybrid");
                if (mode == SearchMode.Lexical) throw new ArgumentException("Use ck semantic disable for lexical search.");
                var modelId = TryReadOption(args, "--model") ?? runtime.Settings.Model;
                await runtime.UseModelAsync(modelId);
                await runtime.EmbedAsync(SemanticRuntime.GetModel(modelId), ["GroundKit embedding compatibility check"]);
                await IndexPackagesAsync(runtime, null);
                await runtime.EnableAsync(modelId, mode);
                AnsiConsole.WriteLine($"Search enabled: {mode.ToString().ToLowerInvariant()} | {modelId}.");
                return 0;
            case "disable":
                runtime.Disable();
                AnsiConsole.WriteLine("Search mode: lexical. Provider, model, and indexes retained.");
                return 0;
            case "index":
                await IndexPackagesAsync(runtime, args.ElementAtOrDefault(2));
                return 0;
            case "status":
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    mode = runtime.Settings.Mode.ToString().ToLowerInvariant(),
                    model = runtime.Settings.Model,
                    rootPath = runtime.RootPath,
                    providerInstalled = File.Exists(Path.Combine(runtime.ProviderPath, "provider.json")),
                    modelInstalled = File.Exists(Path.Combine(runtime.ModelPath(SemanticRuntime.GetModel(runtime.Settings.Model)), "model.onnx")),
                }));
                return 0;
            default:
                throw new ArgumentException("Usage: ck semantic provider install onnx | model list/install/use | enable | disable | index [package-id] | status.");
        }
    }

    private async Task IndexPackagesAsync(SemanticRuntime runtime, string? selector)
    {
        var model = SemanticRuntime.GetModel(runtime.Settings.Model);
        await runtime.EmbedAsync(model, ["GroundKit embedding compatibility check"]);
        IReadOnlyList<PackageSummary> packages;
        if (selector is null) packages = await packageStore.ListAsync();
        else
        {
            var package = await packageStore.GetPackageAsync(selector)
                ?? throw new InvalidOperationException($"Package '{selector}' was not found.");
            packages = [package];
        }
        foreach (var package in packages)
        {
            await packageStore.QueryAsync(new DocsQueryRequest(
                StoredSelector(package), "GroundKit semantic index", new RetrievalOptions(MaxHits: 1, SearchMode: SearchMode.Semantic)
            ));
            AnsiConsole.WriteLine($"Indexed {package.PackageId}{(package.Version is null ? "" : "@" + package.Version)}.");
        }
    }

    /// <summary>
    /// Turns library hints into selectors the store understands, preferring an installed copy so a
    /// local package is never replaced by a download.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveMultiLibrarySelectorsAsync(
        IReadOnlyList<string> libraryHints
    )
    {
        var installed = await packageStore.ListAsync();
        var selectors = new List<string>();

        foreach (var hint in libraryHints)
        {
            var reference = PackageReference.Parse(hint);
            if (string.IsNullOrWhiteSpace(reference.Name))
            {
                continue;
            }

            var match = installed.FirstOrDefault(package =>
                package.PackageId.Equals(reference.Name, StringComparison.OrdinalIgnoreCase)
            );

            // A hinted version is preserved rather than replaced by the stored one, so a partial hint
            // such as `angular@19` still resolves through the shared version rules.
            var selector = match is not null
                ? reference.Version is null
                    ? StoredSelector(match)
                    : $"{match.PackageId}@{reference.Version}"
                : BuildCuratedSelector(reference);

            if (!selectors.Contains(selector, StringComparer.OrdinalIgnoreCase))
            {
                selectors.Add(selector);
            }
        }

        return selectors;
    }

    private static string BuildCuratedSelector(PackageReference reference)
    {
        var curated = LibraryCatalog.Find(reference.Name);
        var registry = curated?.Registry ?? reference.Registry;
        var name = curated?.Name ?? reference.Name;
        return reference.Version is null
            ? $"{registry}/{name}"
            : $"{registry}/{name}@{reference.Version}";
    }

    private static IReadOnlyList<string> SplitLibraryHints(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
                .Take(MaxQueryLibraries)
                .ToArray();

    private static string? OptionValue(IReadOnlyList<string> args, string optionName)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (
                string.Equals(
                    NormalizeOption(args[index]),
                    optionName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return index + 1 < args.Count ? args[index + 1] : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Infers which package a natural-language question is about. Installed packages are offered to
    /// the detector before the curated catalog, so a local copy always wins over a download.
    /// </summary>
    /// <returns>A selector the rest of <c>query</c> can resolve, or null when nothing matched.</returns>
    private async Task<string?> DetectPackageFromQuestionAsync(string question)
    {
        var installed = await packageStore.ListAsync();
        var candidates = installed
            .Select(package => package.PackageId)
            .Concat(LibraryCatalog.StarterLibraries.Select(entry => entry.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var matched = PackageMentionDetector.Detect(question, candidates);
        if (matched is null)
        {
            return null;
        }

        var installedMatch = installed.FirstOrDefault(package =>
            string.Equals(package.PackageId, matched, StringComparison.OrdinalIgnoreCase)
        );
        if (installedMatch is not null)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Detected package {Markup.Escape(installedMatch.PackageId)} from the question.[/]"
            );
            return installedMatch.PackageId;
        }

        var curated = LibraryCatalog.Find(matched);
        if (curated is null)
        {
            return matched;
        }

        var selector = $"{curated.Registry}/{curated.Name}";
        AnsiConsole.MarkupLine(
            $"[grey]Detected package {Markup.Escape(selector)} from the question.[/]"
        );
        return selector;
    }

    /// <summary>
    /// Explains that the question did not name a known package and points at the two ways to make
    /// one queryable: install a published package, or build a package from a repository or folder.
    /// </summary>
    private async Task ShowUndetectedPackageGuidanceAsync(string question)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]Could not tell which package \"{Markup.Escape(question)}\" is about.[/]"
        );
        AnsiConsole.MarkupLine(
            "Name the package explicitly, for example "
                + "[aqua]ck query react \"how do hooks work?\"[/]"
        );

        var installed = await packageStore.ListAsync();
        if (installed.Count > 0)
        {
            var names = installed
                .Select(package => package.PackageId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
            AnsiConsole.MarkupLine(
                $"[grey]Installed packages: {Markup.Escape(string.Join(", ", names))}[/]"
            );
        }

        AnsiConsole.MarkupLine(
            "Or add the documentation first, from a repository or a local docs folder, then query it:"
        );
        AnsiConsole.MarkupLine(
            "  [aqua]ck add <repository-url> --docs-path <folder> --name <name>[/]"
        );
        AnsiConsole.MarkupLine(
            "  [aqua]ck add ./<local-folder> --docs-path <folder> --name <name>[/]"
        );
    }

    /// <summary>
    /// Names the package a selector resolved to, announcing the substitution when a partial version
    /// such as <c>react@18</c> was matched to a concrete stored one.
    /// </summary>
    private static string DescribeInstalled(string selector, PackageSummary installed)
    {
        var (_, _, requestedVersion) = ParsePackageSelector(selector);
        var resolved = StoredSelector(installed);

        if (
            requestedVersion is not null
            && !string.Equals(requestedVersion, installed.Version, StringComparison.OrdinalIgnoreCase)
        )
        {
            AnsiConsole.MarkupLine(
                $"[grey]Using installed {Markup.Escape(resolved)} for "
                    + $"{Markup.Escape(selector)}.[/]"
            );
        }

        return resolved;
    }

    /// <summary>
    /// The selector that addresses a stored package.
    /// </summary>
    /// <remarks>
    /// A package built from a local folder has no version, and the store keys such a package by its
    /// id alone. Appending a made-up label would ask the store for a version that does not exist and
    /// turn a resolvable query into "package not found".
    /// </remarks>
    private static string StoredSelector(PackageSummary package) =>
        string.IsNullOrWhiteSpace(package.Version)
            ? package.PackageId
            : $"{package.PackageId}@{package.Version}";

    private static bool IsQueryOption(string argument) =>
        string.Equals(argument, "--pretty", StringComparison.OrdinalIgnoreCase)
        || string.Equals(argument, "--no-install", StringComparison.OrdinalIgnoreCase)
        || string.Equals(argument, "--include-adjacent", StringComparison.OrdinalIgnoreCase)
        || string.Equals(argument, "--include-references", StringComparison.OrdinalIgnoreCase)
        || argument.StartsWith("--search-mode=", StringComparison.OrdinalIgnoreCase)
        || argument.StartsWith("--libraries=", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a query for a package that is not installed. When the package is published to the
    /// registry it is downloaded on demand, so <c>query</c> stays a single step for a known library.
    /// Nothing is ever built from source here: cloning a repository is too large a side effect for a
    /// read command, so an unpublished package gets instructions instead.
    /// </summary>
    /// <returns>The selector to query with, or null when the caller should stop.</returns>
    private async Task<string?> InstallMissingPackageForQueryAsync(
        string selector,
        bool allowInstall
    )
    {
        var (registry, name, requestedVersion) = ParsePackageSelector(selector);
        IReadOnlyList<RegistryPackage> matches = [];
        var registryUnavailable = false;

        if (registryClient is not null)
        {
            try
            {
                matches = await registryClient.SearchAsync(registry, name, requestedVersion);

                // The registry matches versions exactly, so `react@18` finds nothing there. Ask for
                // every published version and let the shared resolver choose, which makes a partial
                // version behave the same way against the registry as it does against the store.
                if (matches.Count == 0 && requestedVersion is not null)
                {
                    matches = await registryClient.SearchAsync(registry, name);
                }
            }
            catch (Exception exception)
                when (exception
                        is HttpRequestException
                            or TaskCanceledException
                            or InvalidOperationException
                )
            {
                registryUnavailable = true;
                AnsiConsole.MarkupLine(
                    $"[yellow]Registry unavailable:[/] {Markup.Escape(exception.Message)}"
                );
            }
        }

        var resolvedVersion = PackageVersionResolver.Resolve(
            requestedVersion,
            matches.Select(candidate => candidate.Version)
        );
        var match = resolvedVersion is null
            ? null
            : matches.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Version,
                    resolvedVersion,
                    StringComparison.OrdinalIgnoreCase
                )
            );

        if (match is null)
        {
            await ShowMissingPackageGuidanceAsync(
                selector,
                registry,
                name,
                matches,
                requestedVersion,
                resolvedVersion: null,
                registryUnavailable: registryUnavailable
            );
            return null;
        }

        if (!allowInstall)
        {
            await ShowMissingPackageGuidanceAsync(
                selector,
                registry,
                name,
                matches,
                requestedVersion,
                resolvedVersion,
                "Automatic download is disabled by --no-install."
            );
            return null;
        }

        if (packageDownloadService is null)
        {
            await ShowMissingPackageGuidanceAsync(
                selector,
                registry,
                name,
                matches,
                requestedVersion,
                resolvedVersion,
                "Automatic download is unavailable in this environment."
            );
            return null;
        }

        var reference = $"{match.Registry}/{match.Name}@{match.Version}";
        if (
            requestedVersion is not null
            && !string.Equals(requestedVersion, match.Version, StringComparison.OrdinalIgnoreCase)
        )
        {
            AnsiConsole.MarkupLine(
                $"[grey]Resolved {Markup.Escape(selector)} to {Markup.Escape(reference)}.[/]"
            );
        }

        AnsiConsole.MarkupLine(
            $"[grey]Package {Markup.Escape(selector)} is not installed. Downloading "
                + $"{Markup.Escape(reference)} from the registry...[/]"
        );

        var installedPath = await packageDownloadService.InstallRegistryPackageAsync(
            match.Registry,
            match.Name,
            match.Version
        );
        AnsiConsole.MarkupLine($"[green]Installed package:[/] {Markup.Escape(installedPath)}");

        // Resolve through the store rather than assuming the artifact's identity or version label
        // matches the catalog: the manifest inside the package is the authority on both.
        var installed = (await packageStore.ListAsync())
            .FirstOrDefault(package =>
                string.Equals(
                    Path.GetFullPath(package.PackagePath),
                    Path.GetFullPath(installedPath),
                    StringComparison.OrdinalIgnoreCase
                )
            );

        return installed is null
            ? match.Name
            : StoredSelector(installed);
    }

    /// <summary>
    /// Explains why a query could not be answered and names the two routes that would fix it: pull a
    /// published artifact, or build a package from a repository or a local docs folder.
    /// </summary>
    private async Task ShowMissingPackageGuidanceAsync(
        string selector,
        string registry,
        string name,
        IReadOnlyList<RegistryPackage> published,
        string? requestedVersion = null,
        string? resolvedVersion = null,
        string? reason = null,
        bool registryUnavailable = false
    )
    {
        AnsiConsole.MarkupLine($"[yellow]Package {Markup.Escape(selector)} is not installed.[/]");
        if (reason is not null)
        {
            AnsiConsole.MarkupLine(Markup.Escape(reason));
        }

        var reference = $"{registry}/{name}";
        if (registryUnavailable)
        {
            AnsiConsole.MarkupLine(
                $"[grey]The registry could not be reached, so {Markup.Escape(reference)} "
                    + "could not be checked.[/]"
            );
        }
        else if (published.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{Markup.Escape(reference)} was not found in the registry.[/]"
            );
        }
        else if (requestedVersion is not null && resolvedVersion is null)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Version {Markup.Escape(requestedVersion)} of "
                    + $"{Markup.Escape(reference)} is not published. Published versions: "
                    + $"{Markup.Escape(string.Join(", ", PackageVersionResolver.Order(published.Select(package => package.Version))))}.[/]"
            );
        }
        else if (
            requestedVersion is not null
            && resolvedVersion is not null
            && !string.Equals(requestedVersion, resolvedVersion, StringComparison.OrdinalIgnoreCase)
        )
        {
            AnsiConsole.MarkupLine(
                $"[grey]Version {Markup.Escape(requestedVersion)} of "
                    + $"{Markup.Escape(reference)} resolves to {Markup.Escape(resolvedVersion)}.[/]"
            );
        }

        var suggestion =
            resolvedVersion
            ?? PackageVersionResolver.Resolve(
                requestedVersion,
                published.Select(package => package.Version)
            )
            ?? PackageVersionResolver.Order(published.Select(package => package.Version))
                .FirstOrDefault();

        var installCommand = suggestion is null
            ? $"[aqua]ck install {Markup.Escape(reference)}[/]"
            : $"[aqua]ck install {Markup.Escape(reference)} {Markup.Escape(suggestion)}[/]";
        AnsiConsole.MarkupLine($"Download it with {installCommand}, then query again.");

        var curated = LibraryCatalog.StarterLibraries.FirstOrDefault(entry =>
            string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Registry, registry, StringComparison.OrdinalIgnoreCase)
        );

        AnsiConsole.MarkupLine(
            "Or build the package once from a repository or a local docs folder, then query it offline:"
        );
        if (curated is not null)
        {
            AnsiConsole.MarkupLine(
                $"  [aqua]ck add {Markup.Escape(curated.Repository)} "
                    + $"--docs-path {Markup.Escape(curated.DocsPath)} --name {Markup.Escape(name)}[/]"
            );
            AnsiConsole.MarkupLine(
                $"  [grey]{Markup.Escape(name)} is in the curated catalog "
                    + $"({Markup.Escape(curated.Description)}).[/]"
            );
        }

        AnsiConsole.MarkupLine(
            $"  [aqua]ck add <repository-url> --docs-path <folder> --name {Markup.Escape(name)}[/]"
        );
        AnsiConsole.MarkupLine(
            $"  [aqua]ck add ./{Markup.Escape(name)} --docs-path <folder> --name {Markup.Escape(name)}[/]"
        );

        var installed = await packageStore.ListAsync();
        if (installed.Count > 0)
        {
            var names = installed
                .Select(package => package.PackageId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
            AnsiConsole.MarkupLine(
                $"[grey]Installed packages: {Markup.Escape(string.Join(", ", names))}[/]"
            );
        }
    }

    private async Task<int> RunRefreshAsync(string[] args)
    {
        if (args.Length < 2)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] refresh <package-id>");
            return 1;
        }

        var source = await packageStore.GetSourceAsync(args[1]);
        if (source is null)
        {
            AnsiConsole.MarkupLine($"[red]Package not found:[/] {Markup.Escape(args[1])}");
            return 1;
        }

        var buildResult = await packageBuilder.BuildAsync(source.Location, source.DocsPath);
        var packagePath = await packageStore.SaveAsync(buildResult);

        AnsiConsole.MarkupLine(
            $"[green]Refreshed package:[/] {Markup.Escape(buildResult.Manifest.PackageId)}"
        );
        AnsiConsole.MarkupLine($"Path: {Markup.Escape(packagePath)}");
        AnsiConsole.MarkupLine(
            $"Documents: {buildResult.Manifest.DocumentCount}, Sections: {buildResult.Manifest.ChunkCount}"
        );
        return 0;
    }

    private async Task<int> RunRemoveAsync(string[] args)
    {
        if (args.Length < 2)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] remove <name[@version]>");
            return 1;
        }

        var selector = args[1];
        var separator = selector.LastIndexOf('@');
        var hasVersion = separator > 0 && separator < selector.Length - 1;
        var packageId = hasVersion ? selector[..separator] : selector;
        var requestedVersion = hasVersion ? selector[(separator + 1)..] : null;
        var packages = (await packageStore.ListAsync())
            .Where(package =>
                package.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        if (requestedVersion is not null)
        {
            packages = packages
                .Where(package =>
                    string.Equals(
                        package.Version ?? "dev",
                        requestedVersion,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .ToList();
        }
        else if (packages.Count > 1)
        {
            if (!AnsiConsole.Profile.Capabilities.Interactive)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Multiple versions of {Markup.Escape(packageId)} are installed. "
                        + "Pass name@version to remove one:[/]"
                );
                foreach (var package in packages.OrderBy(package => package.Version))
                {
                    AnsiConsole.MarkupLine($"  {Markup.Escape(package.Version ?? "dev")}");
                }

                return 1;
            }

            var selectedVersion = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"Select version to remove for [green]{Markup.Escape(packageId)}[/]:")
                    .AddChoices(
                        packages
                            .OrderBy(package => package.Version)
                            .Select(package => package.Version ?? "dev")
                    )
            );
            packages = packages
                .Where(package => (package.Version ?? "dev") == selectedVersion)
                .ToList();
        }

        if (packages.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Package not found:[/] {Markup.Escape(selector)}");
            return 1;
        }

        var selectedPackage = packages[0];
        var removedCount = await packageStore.RemoveAsync(
            selectedPackage.PackageId,
            selectedPackage.Version ?? "dev"
        );
        AnsiConsole.MarkupLine(
            $"[green]Removed package:[/] {Markup.Escape(selectedPackage.PackageId)}@{Markup.Escape(selectedPackage.Version ?? "dev")} ({removedCount})"
        );
        return 0;
    }

    private async Task<int> RunSearchPackagesAsync(string[] args)
    {
        if (registryClient is null)
        {
            AnsiConsole.MarkupLine("[red]Registry client is unavailable.[/]");
            return 1;
        }

        if (args.Length < 3)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] search-packages <registry> <name> [version]");
            return 1;
        }

        var results = await registryClient.SearchAsync(
            args[1],
            args[2],
            args.Length > 3 ? args[3] : null
        );
        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No packages found.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Package");
        table.AddColumn("Version");
        table.AddColumn("Description");
        table.AddColumn("Size");
        foreach (var result in results)
        {
            table.AddRow(
                Markup.Escape(result.Name),
                Markup.Escape(result.Version),
                Markup.Escape(result.Description ?? "-"),
                result.Size is long size ? $"{size:N0} B" : "-"
            );
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private async Task<int> RunDownloadPackageAsync(string[] args)
    {
        if (args.Length < 4)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] download-package <registry> <name> <version>");
            return 1;
        }

        if (packageDownloadService is null)
        {
            AnsiConsole.MarkupLine("[red]Package download service is unavailable.[/]");
            return 1;
        }

        var path = await packageDownloadService.InstallRegistryPackageAsync(
            args[1],
            args[2],
            args[3]
        );
        AnsiConsole.MarkupLine($"[green]Installed package:[/] {Markup.Escape(path)}");
        return 0;
    }

    private async Task<int> RunInstallAsync(string[] args)
    {
        if (args.Length < 2)
        {
            AnsiConsole.WriteLine("Usage: install <registry/name|name|source> [version]");
            return 1;
        }

        var requested = args[1];
        var requestedVersion = args.Length > 2 ? args[2] : null;

        if (packageDownloadService is null)
        {
            AnsiConsole.MarkupLine("[red]Package download service is unavailable.[/]");
            return 1;
        }

        var packagePath = await packageDownloadService.InstallAsync(requested, requestedVersion);
        AnsiConsole.MarkupLine(
            $"[green]Built and installed package:[/] {Markup.Escape(packagePath)}"
        );
        return 0;
    }

    private static int ShowUnknownCommand(string command)
    {
        AnsiConsole.MarkupLine($"[red]Unknown command:[/] {Markup.Escape(command)}");
        ShowHelp();
        return 1;
    }

    private static int ShowHttpServeHelp()
    {
        AnsiConsole.WriteLine("serve-http is started by the web host entry point.");
        return 1;
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]ContextKit[/]");
        AnsiConsole.MarkupLine("Use [aqua]ck <command>[/].");
        AnsiConsole.MarkupLine("Command aliases such as [aqua]q[/] and [aqua]ls[/] are also supported.");
        AnsiConsole.MarkupLine("Commands:");
        AnsiConsole.WriteLine(
            "  add <source> [--path path] [--name name] [--pkg-version version] [--save path] [--tag tag] [--choose-tag]"
        );
        AnsiConsole.MarkupLine("  import <package-file>");
        AnsiConsole.MarkupLine("  export <package-id> <destination>");
        AnsiConsole.MarkupLine("  list");
        AnsiConsole.MarkupLine("  inspect <package-id>");
        AnsiConsole.WriteLine("  query <package-id> <topic> [--pretty] [--no-install]");
        AnsiConsole.WriteLine("  query \"<question>\" [--pretty] [--no-install]");
        AnsiConsole.MarkupLine("  refresh <package-id>");
        AnsiConsole.MarkupLine("  remove <package-id>");
        AnsiConsole.WriteLine("  serve [--libs package-a,package-b]");
        AnsiConsole.WriteLine("  serve-http [--urls http://localhost:3001]");
        AnsiConsole.WriteLine("  catalog [query]");
        AnsiConsole.WriteLine("  search-packages <registry> <name> [version]");
        AnsiConsole.WriteLine("  download-package <registry> <name> <version>");
        AnsiConsole.WriteLine("  install <registry/name|name|source> [version]");
    }

    private static string? TryReadOption(IReadOnlyList<string> args, string optionName)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (
                string.Equals(
                    NormalizeOption(args[index]),
                    optionName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string[] RemoveOption(IReadOnlyList<string> args, string optionName)
    {
        var remaining = new List<string>(args.Count);
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            remaining.Add(args[index]);
        }

        return [.. remaining];
    }

    /// <summary>
    /// Splits a query selector into registry, package name, and optional version.
    /// </summary>
    /// <remarks>
    /// A thin wrapper over <see cref="PackageReference.Parse"/> so the CLI, the download service, and
    /// every other entry point agree on what a selector means.
    /// </remarks>
    internal static (string Registry, string Name, string? Version) ParsePackageSelector(
        string selector
    )
    {
        var reference = PackageReference.Parse(selector);
        return (reference.Registry, reference.Name, reference.Version);
    }

    internal static string NormalizeCommand(string command) =>
        command.ToLowerInvariant() switch
        {
            "--add" => "add",
            "a" => "add",
            "--import" => "import",
            "im" => "import",
            "--export" => "export",
            "ex" or "exp" => "export",
            "--list" => "list",
            "l" or "ls" => "list",
            "--inspect" => "inspect",
            "ins" or "info" => "inspect",
            "--query" => "query",
            "q" => "query",
            "--refresh" => "refresh",
            "rf" or "ref" => "refresh",
            "--remove" => "remove",
            "rm" or "del" => "remove",
            "--serve" => "serve",
            "--serve-http" => "serve-http",
            "cat" or "c" => "catalog",
            "--search-packages" => "search-packages",
            "sp" or "search" => "search-packages",
            "--download-package" => "download-package",
            "dp" or "dl" or "download" => "download-package",
            "--install" => "install",
            "i" => "install",
            "--catalog" => "catalog",
            _ => command.ToLowerInvariant(),
        };

    internal static string NormalizeOption(string option) =>
        option.ToLowerInvariant() switch
        {
            "--docs-path" => "--path",
            "-p" => "--path",
            "-n" => "--name",
            "-v" or "-pv" => "--pkg-version",
            "-s" => "--save",
            "-t" => "--tag",
            "-c" => "--choose-tag",
            _ => option,
        };

    private static bool HasFlag(IReadOnlyList<string> args, string optionName) =>
        args.Any(argument =>
            string.Equals(NormalizeOption(argument), optionName, StringComparison.OrdinalIgnoreCase)
        );
}
