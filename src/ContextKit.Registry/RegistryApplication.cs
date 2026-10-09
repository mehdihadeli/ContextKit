using System.IO.Compression;
using System.Text.RegularExpressions;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Oci;
using GroundKit.Storage.Sqlite;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace GroundKit.Registry;

public sealed class RegistryApplication(
    IDocumentPackageBuilder packageBuilder,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    HttpClient? ociHttpClient = null
)
{
    private readonly RegistryBundleService _bundleService = new();

    internal static string[] NormalizeArguments(string[] args)
    {
        if (args.Length == 0)
        {
            return args;
        }

        var normalized = args.ToArray();
        normalized[0] = NormalizeCommand(normalized[0]) ?? string.Empty;

        for (var index = 1; index < normalized.Length; index++)
        {
            normalized[index] = NormalizeOption(normalized[index]);
        }

        return normalized;
    }

    public async Task<int> RunAsync(string[] args)
    {
        args = NormalizeArguments(args);

        try
        {
            return NormalizeCommand(args.FirstOrDefault()) switch
            {
                "list" => List(args),
                "validate" => Validate(args),
                "build" => await BuildAsync(args),
                "build-all" => await BuildAllAsync(args),
                "bundle" => await BundleAsync(args),
                "import-bundle" => await ImportBundleAsync(args),
                "catalog-index" => await CatalogIndexAsync(args),
                "push-oci" => await PushOciAsync(args),
                _ => ShowHelp(),
            };
        }
        catch (Exception exception)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(exception.Message)}");
            return 1;
        }
    }

    private static int List(string[] args)
    {
        foreach (var definition in Load(args))
        {
            AnsiConsole.MarkupLine(
                $"[aqua]{definition.Registry}/{definition.Name}[/] "
                    + $"({(definition.IsVersioned ? "versioned" : "latest")}) {Markup.Escape(definition.Source)}"
            );
        }
        return 0;
    }

    private static string? NormalizeCommand(string? command) =>
        command?.ToLowerInvariant() switch
        {
            "--list" => "list",
            "l" or "ls" => "list",
            "--validate" => "validate",
            "v" or "val" => "validate",
            "--build" => "build",
            "b" => "build",
            "--build-all" => "build-all",
            "ba" => "build-all",
            "--bundle" => "bundle",
            "bd" or "bun" => "bundle",
            "--import-bundle" => "import-bundle",
            "ib" => "import-bundle",
            "--push-oci" => "push-oci",
            "po" => "push-oci",
            _ => command?.ToLowerInvariant(),
        };

    internal static string NormalizeOption(string option) =>
        option.ToLowerInvariant() switch
        {
            "-d" => "--dir",
            "-o" => "--output",
            "-f" => "--format",
            "-t" => "--destination",
            _ => option,
        };

    private static int Validate(string[] args)
    {
        var definitions = Load(args);
        AnsiConsole.MarkupLine($"[green]Validated {definitions.Count} registry definition(s).[/]");
        return 0;
    }

    private async Task<int> BuildAsync(string[] args)
    {
        if (args.Length < 2)
        {
            AnsiConsole.MarkupLine(
                "[red]Usage:[/] "
                    + Markup.Escape("build <name> [version] [--dir <path>] [--output <path>]")
            );
            return 1;
        }

        var definition = Find(Load(args), args[1]);
        var selected = SelectVersion(definition, args);
        var path = await BuildDefinitionAsync(
            definition,
            Option(args, "--output") ?? "./dist-packages",
            selected
        );
        AnsiConsole.MarkupLine($"[green]Built:[/] {Markup.Escape(path)}");
        return 0;
    }

    private async Task<int> BuildAllAsync(string[] args)
    {
        var definitions = Load(args);
        var output = Option(args, "--output") ?? "./dist-packages";
        var allowFailures = Flag(args, "--allow-failures");
        Directory.CreateDirectory(output);
        var failures = 0;
        var succeeded = 0;

        foreach (var definition in definitions)
        foreach (var selected in definition.ResolveVersions())
        {
            try
            {
                await BuildDefinitionAsync(definition, output, selected);
                succeeded++;
                AnsiConsole.MarkupLine(
                    $"[green]Built:[/] {definition.Registry}/{definition.Name}@{selected.Version}"
                );
            }
            catch (Exception exception)
            {
                failures++;
                AnsiConsole.MarkupLine(
                    $"[red]Failed {definition.Registry}/{definition.Name}@{selected.Version}:[/] {Markup.Escape(exception.Message)}"
                );
            }
        }

        AnsiConsole.MarkupLine($"Summary: {succeeded} succeeded, {failures} failed.");
        AnsiConsole.MarkupLine($"RegistryBuildFailures={failures}");
        if (failures == 0)
        {
            return 0;
        }

        return allowFailures && succeeded > 0 ? 0 : 1;
    }

    private async Task<int> BundleAsync(string[] args)
    {
        var output = Option(args, "--output") ?? "./dist-packages";
        var format = Option(args, "--format")?.ToLowerInvariant() ?? "zip";
        var extension = format switch
        {
            "zip" => ".zip",
            "tar.gz" or "tgz" => ".tar.gz",
            _ => throw new InvalidOperationException("Bundle format must be zip or tar.gz."),
        };
        var destination =
            Option(args, "--destination") ?? Path.Combine(output, "groundkit-registry" + extension);
        var path = await _bundleService.CreateAsync(output, destination);
        AnsiConsole.MarkupLine($"[green]Bundle created:[/] {Markup.Escape(path)}");
        return 0;
    }

    private async Task<int> CatalogIndexAsync(string[] args)
    {
        var ociRepository = Option(args, "--oci-repository");
        var referencesPath = Option(args, "--oci-references");
        var ociReferences = await ResolveOciReferencesAsync(args, referencesPath, ociRepository);
        var baseUrl = Option(args, "--base-url");
        if (ociReferences is null && string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException(
                "--base-url is required unless --oci-repository or --oci-references is supplied for catalog-index."
            );
        }

        var index = await new RegistryCatalogService().CreateAsync(
            Option(args, "--dir") ?? "registry",
            Option(args, "--output") ?? "./dist-packages",
            Option(args, "--destination") ?? "./dist-catalog",
            baseUrl,
            cancellationToken: default,
            ociReferences
        );
        if (ociReferences is not null)
        {
            AnsiConsole.MarkupLine(
                $"[green]Catalog references {ociReferences.Count} OCI artifact(s).[/]"
            );
        }

        AnsiConsole.MarkupLine($"[green]Catalog created:[/] {Markup.Escape(index)}");
        return 0;
    }

    /// <summary>
    /// Pushes every built package to an OCI registry and writes the reference map that
    /// <c>catalog-index</c> consumes.
    /// </summary>
    private async Task<int> PushOciAsync(string[] args)
    {
        var ociRepository =
            Option(args, "--oci-repository")
            ?? throw new ArgumentException("--oci-repository is required for push-oci.");
        var output = Option(args, "--output") ?? "./dist-packages";
        var referencesPath = Option(args, "--oci-references") ?? "oci-references.json";
        var (host, prefix) = RegistryOciPublisher.Split(ociRepository);

        if (ociHttpClient is null)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.BaseAddress = new Uri($"https://{host}/");
            return await PushOciCoreAsync(args, client, host, prefix, output, referencesPath);
        }

        ociHttpClient.BaseAddress ??= new Uri($"https://{host}/");
        return await PushOciCoreAsync(args, ociHttpClient, host, prefix, output, referencesPath);
    }

    private async Task<int> PushOciCoreAsync(
        string[] args,
        HttpClient httpClient,
        string host,
        string prefix,
        string output,
        string referencesPath
    )
    {
        var publisher = new RegistryOciPublisher(
            new OciRegistryClient(httpClient, OciCredential.FromEnvironment(host)),
            host,
            prefix
        );
        var references = await publisher.PushAllAsync(
            Load(args),
            output,
            pushed => AnsiConsole.MarkupLine($"[green]Pushed:[/] {Markup.Escape(pushed)}")
        );
        await RegistryOciPublisher.WriteAsync(references, referencesPath);
        AnsiConsole.MarkupLine(
            $"[green]Pushed {references.Packages.Count} package(s) to {Markup.Escape(references.Repository)}.[/]"
        );
        AnsiConsole.MarkupLine($"[green]OCI references:[/] {Markup.Escape(referencesPath)}");
        return 0;
    }

    /// <summary>
    /// Prefers the digest-pinned map produced by <c>push-oci</c>. When only a repository is known,
    /// falls back to tag references, which remain safe because the catalog still pins each
    /// package's SHA-256.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>?> ResolveOciReferencesAsync(
        string[] args,
        string? referencesPath,
        string? ociRepository
    )
    {
        if (!string.IsNullOrWhiteSpace(referencesPath))
        {
            return RegistryOciPublisher.ToLookup(
                await RegistryOciPublisher.ReadAsync(referencesPath)
            );
        }

        if (string.IsNullOrWhiteSpace(ociRepository))
        {
            return null;
        }

        var (host, prefix) = RegistryOciPublisher.Split(ociRepository);
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Load(args))
        {
            foreach (var (version, _) in definition.ResolveVersions())
            {
                var repository = $"{prefix}/{OciNames.RepositoryName(definition.Registry, definition.Name)}";
                var tag = OciNames.Sanitize(version);
                references[RegistryOciPublisher.Key(definition.Registry, definition.Name, version)] =
                    $"oci://{host}/{repository}:{tag}";
            }
        }

        return references;
    }

    private async Task<int> ImportBundleAsync(string[] args)
    {
        if (args.Length < 2)
        {
            AnsiConsole.MarkupLine(
                "[red]Usage:[/] " + Markup.Escape("import-bundle <path> [--output <path>]")
            );
            return 1;
        }
        var output = Option(args, "--output") ?? "./dist-packages";
        var paths = await _bundleService.ImportAsync(args[1], output);
        AnsiConsole.MarkupLine($"[green]Imported {paths.Count} package(s).[/]");
        return 0;
    }

    private async Task<string> BuildDefinitionAsync(
        RegistryDefinition definition,
        string output,
        (string Version, string? Tag) selected
    )
    {
        var versionDefinition = definition.Versions.FirstOrDefault(item => item.Version == selected.Version);
        var source = versionDefinition?.Source ?? definition.Source;
        var docsPath = versionDefinition?.DocsPath ?? definition.DocsPath;
        var sourceType = versionDefinition?.SourceType ?? definition.SourceType;
        var excludePaths = versionDefinition?.ExcludePaths ?? definition.ExcludePaths;
        var temporaryDirectory = (string?)null;

        if (string.Equals(sourceType, "zip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceType, "archive", StringComparison.OrdinalIgnoreCase))
        {
            temporaryDirectory = await ExtractArchiveAsync(
                source.Replace("{version}", selected.Version, StringComparison.Ordinal),
                excludePaths ?? []
            );
            source = temporaryDirectory;
        }

        if (!Directory.Exists(source) && !Uri.TryCreate(source, UriKind.Absolute, out _))
        {
            throw new DirectoryNotFoundException(
                $"Source directory was not found: {source}"
            );
        }

        try
        {
            var result = await packageBuilder.BuildAsync(
                source,
                docsPath?.Replace("{version}", selected.Version, StringComparison.Ordinal),
                default,
                selected.Version,
                selected.Tag
            );
            var displayName = string.IsNullOrWhiteSpace(definition.Description)
                ? definition.Name
                : definition.Description;
            var normalized = result with
            {
                Source = result.Source with
                {
                    CanonicalId = definition.Name,
                    DisplayName = displayName,
                },
                Manifest = result.Manifest with
                {
                    PackageId = definition.Name,
                    DisplayName = displayName,
                    Version = selected.Version,
                    SourceCanonicalId = definition.Name,
                },
            };
            var store = new SqlitePackageStore(
                new PackageStoreOptions(Path.GetFullPath(output)),
                loggerFactory.CreateLogger<SqlitePackageStore>()
            );
            return await store.SaveAsync(normalized);
        }
        finally
        {
            if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private async Task<string> ExtractArchiveAsync(
        string source,
        IReadOnlyList<string> excludePaths
    )
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"groundkit-{Guid.NewGuid():N}.zip");
        var extractPath = Path.Combine(Path.GetTempPath(), $"groundkit-{Guid.NewGuid():N}");
        try
        {
            using var client = httpClientFactory.CreateClient();
            await using (var response = await client.GetStreamAsync(source))
            await using (var archive = File.Create(archivePath))
            {
                await response.CopyToAsync(archive);
            }

            Directory.CreateDirectory(extractPath);
            ZipFile.ExtractToDirectory(archivePath, extractPath);
            foreach (var file in Directory.EnumerateFiles(extractPath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(extractPath, file).Replace('\\', '/');
                var relativeWithoutArchiveRoot = relative[(relative.IndexOf('/') + 1)..];
                if (
                    excludePaths.Any(pattern =>
                        GlobMatches(pattern, relative)
                        || GlobMatches(pattern, relativeWithoutArchiveRoot)
                    )
                )
                {
                    File.Delete(file);
                }
            }
            return extractPath;
        }
        catch
        {
            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, recursive: true);
            }
            throw;
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    private static bool GlobMatches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static (string Version, string? Tag) SelectVersion(
        RegistryDefinition definition,
        string[] args
    )
    {
        var requested =
            args.Length > 2 && !args[2].StartsWith("-", StringComparison.Ordinal) ? args[2] : null;
        var versions = definition.ResolveVersions();
        var selected = versions.SingleOrDefault(item =>
            item.Version == (requested ?? versions[0].Version)
        );
        return selected == default
            ? throw new InvalidOperationException(
                $"Version '{requested}' is not defined for {definition.Name}."
            )
            : selected;
    }

    private static RegistryDefinition Find(
        IReadOnlyList<RegistryDefinition> definitions,
        string name
    ) =>
        definitions.SingleOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                $"{item.Registry}/{item.Name}",
                name,
                StringComparison.OrdinalIgnoreCase
            )
        ) ?? throw new InvalidOperationException($"Definition '{name}' was not found.");

    private static IReadOnlyList<RegistryDefinition> Load(string[] args)
    {
        var definitions = RegistryDefinitionLoader.LoadDirectory(
            Option(args, "--dir") ?? "registry"
        );
        RegistryDefinitionLoader.Validate(definitions);
        return definitions;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(
            args,
            arg => string.Equals(NormalizeOption(arg), name, StringComparison.OrdinalIgnoreCase)
        );
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool Flag(string[] args, string name) =>
        Array.Exists(
            args,
            arg => string.Equals(NormalizeOption(arg), name, StringComparison.OrdinalIgnoreCase)
        );

    private static int ShowHelp()
    {
        AnsiConsole.MarkupLine(
            Markup.Escape(
                "ck registry list|validate|build|build-all|push-oci|bundle|import-bundle|catalog-index [--dir <path>] [--output <path>] [--allow-failures]"
            )
        );
        return 0;
    }
}
