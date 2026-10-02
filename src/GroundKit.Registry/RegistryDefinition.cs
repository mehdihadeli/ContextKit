using GroundKit.Core.Contracts;
using YamlDotNet.RepresentationModel;

namespace GroundKit.Registry;

public sealed record RegistryDefinition(
    string Name,
    string Registry,
    string Description,
    string SourceType,
    string Source,
    string? DocsPath,
    string? Ref,
    string TagPattern,
    IReadOnlyList<RegistryVersion> Versions,
    IReadOnlyList<string>? ExcludePaths = null
)
{
    public string PackageId => Name;

    public SourceKind Kind =>
        SourceType.ToLowerInvariant() switch
        {
            "git" => SourceKind.GitRepository,
            "local" or "directory" => SourceKind.LocalDirectory,
            "llms.txt" or "llms" => SourceKind.LlmsText,
            "raw" or "page" => SourceKind.RawPage,
            "zip" or "archive" => SourceKind.ZipArchive,
            "html-index" or "html_index" => SourceKind.HtmlIndex,
            _ => SourceKind.Unknown,
        };

    public bool IsVersioned => Versions.Count > 0;

    public IReadOnlyList<(string Version, string? Tag)> ResolveVersions()
    {
        if (!IsVersioned)
        {
            string? tag = Ref;
            return new List<(string Version, string? Tag)> { ("latest", tag) };
        }

        return Versions
            .Select<RegistryVersion, (string Version, string? Tag)>(version =>
            {
                var tag = version.Tag
                    ?? version.Ref
                    ?? (version.TagPattern ?? TagPattern).Replace("{version}", version.Version);
                return (version.Version, tag);
            })
            .ToArray();
    }
}

public sealed record RegistryVersion(
    string Version,
    string? Tag,
    string? SourceType = null,
    string? Source = null,
    string? DocsPath = null,
    string? TagPattern = null,
    IReadOnlyList<string>? ExcludePaths = null,
    string? Ref = null
);

public static class RegistryDefinitionLoader
{
    public const string DefaultRegistry = "packages";
    public const string RegistryRoot = "registry";

    public static IReadOnlyList<RegistryDefinition> LoadDirectory(
        string directory,
        string registry = RegistryRoot
    )
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Registry directory was not found: {directory}");
        }

        return Directory
            .EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var resolvedRegistry = ResolveRegistry(path, directory, registry);
                return LoadFile(path, resolvedRegistry, GetPackageName(path, directory, resolvedRegistry));
            })
            .ToArray();
    }

    private static string GetPackageName(string path, string directory, string registry)
    {
        var segments = Path.GetRelativePath(directory, path)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (string.Equals(registry, RegistryRoot, StringComparison.OrdinalIgnoreCase) is false
            && segments.Count > 1)
        {
            segments.RemoveAt(0);
        }

        var relative = string.Join('/', segments);
        return relative.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            ? relative[..^5]
            : relative;
    }

    private static string ResolveRegistry(string path, string directory, string registry)
    {
        if (!string.Equals(registry, RegistryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return registry;
        }

        var relative = Path.GetRelativePath(directory, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        return relative.Length > 1 ? relative[0] : DefaultRegistry;
    }

    public static RegistryDefinition LoadFile(string path, string registry = DefaultRegistry)
    {
        return LoadFile(path, registry, null);
    }

    private static RegistryDefinition LoadFile(
        string path,
        string registry,
        string? expectedPackageName
    )
    {
        using var reader = File.OpenText(path);
        var yaml = new YamlStream();
        yaml.Load(reader);
        var root =
            yaml.Documents.SingleOrDefault()?.RootNode as YamlMappingNode
            ?? throw Invalid(path, "root must be a mapping");

        var name = Required(root, "name", path);
        var description = Optional(root, "description") ?? string.Empty;
        var sourceNode = root.Children.TryGetValue(new YamlScalarNode("source"), out var node)
            ? node as YamlMappingNode
            : null;
        if (
            sourceNode is null
            && root.Children.TryGetValue(new YamlScalarNode("versions"), out var versionsNode)
            && versionsNode is YamlSequenceNode versionSequence
            && versionSequence.Children.FirstOrDefault() is YamlMappingNode firstVersion
            && firstVersion.Children.TryGetValue(new YamlScalarNode("source"), out var nestedSource)
        )
        {
            sourceNode = nestedSource as YamlMappingNode;
        }
        if (sourceNode is null)
        {
            throw Invalid(path, "source mapping or version source mapping is required");
        }

        var sourceType = Required(sourceNode, "type", path, "source");
        var source =
            Optional(sourceNode, "url")
            ?? Optional(sourceNode, "path")
            ?? throw Invalid(path, "source.url or source.path is required");
        var docsPath = Optional(sourceNode, "docs_path");
        var sourceRef = Optional(sourceNode, "ref");
        var tagPattern = Optional(root, "tag_pattern") ?? "v{version}";
        var excludePaths = ReadStringSequence(sourceNode, "exclude_paths", path, "source");
        var versions = ReadVersions(root, path);
        if (versions.Count > 0 && root.Children.ContainsKey(new YamlScalarNode("source")))
        {
            throw Invalid(path, "versioned definitions must put source inside each version");
        }
        var expectedName = expectedPackageName ?? Path.GetFileNameWithoutExtension(path);
        if (
            !string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
        )
        {
            throw Invalid(path, $"name '{name}' does not match filename '{expectedName}'");
        }

        var definition = new RegistryDefinition(
            name,
            registry,
            description,
            sourceType,
            source,
            docsPath,
            sourceRef,
            tagPattern,
            versions,
            excludePaths
        );
        Validate(definition, path);
        return definition;
    }

    private static IReadOnlyList<RegistryVersion> ReadVersions(YamlMappingNode root, string path)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("versions"), out var node))
        {
            return [];
        }

        if (node is not YamlSequenceNode sequence || sequence.Children.Count == 0)
        {
            throw Invalid(path, "versions must be a non-empty sequence");
        }

        return sequence
            .Children.Select(item =>
            {
                if (item is not YamlMappingNode mapping)
                {
                    throw Invalid(path, "each version must be a mapping");
                }

                var versionValues = Optional(mapping, "version") is { } singleVersion
                    ? [singleVersion]
                    : Optional(mapping, "min_version") is { } minimumVersion
                        ? [minimumVersion]
                        : ReadStringSequence(mapping, "versions", path, "versions");
                if (versionValues.Count == 0)
                {
                    throw Invalid(path, "versions.version, versions.min_version, or versions.versions is required");
                }

                var versionSource = mapping.Children.TryGetValue(new YamlScalarNode("source"), out var sourceNode)
                    ? sourceNode as YamlMappingNode
                    : null;
                var sourceType = versionSource is null ? null : Required(versionSource, "type", path, "versions.source");
                var source = versionSource is null
                    ? null
                    : Optional(versionSource, "url") ?? Optional(versionSource, "path");
                var docsPath = versionSource is null ? null : Optional(versionSource, "docs_path");
                var sourceRef = versionSource is null ? null : Optional(versionSource, "ref");
                var excludePaths = versionSource is null
                    ? null
                    : ReadStringSequence(versionSource, "exclude_paths", path, "versions.source");
                return versionValues.Select(version => new RegistryVersion(
                    version,
                    Optional(mapping, "tag"),
                    sourceType,
                    source,
                    docsPath,
                    Optional(mapping, "tag_pattern"),
                    excludePaths,
                    sourceRef
                )).ToArray();
            })
            .SelectMany(versions => versions)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadStringSequence(
        YamlMappingNode mapping,
        string key,
        string path,
        string prefix
    )
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out var node))
        {
            return [];
        }

        if (node is not YamlSequenceNode sequence)
        {
            throw Invalid(path, $"{prefix}.{key} must be a sequence");
        }

        return sequence.Children
            .Select(item => (item as YamlScalarNode)?.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    public static void Validate(IReadOnlyList<RegistryDefinition> definitions)
    {
        var duplicates = definitions
            .GroupBy(definition => definition.PackageId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate registry definitions: {string.Join(", ", duplicates)}"
            );
        }
    }

    private static void Validate(RegistryDefinition definition, string path)
    {
        if (definition.Kind == SourceKind.Unknown)
        {
            throw Invalid(path, $"unsupported source type '{definition.SourceType}'");
        }

        if (string.IsNullOrWhiteSpace(definition.Registry))
        {
            throw Invalid(path, "registry name is required");
        }
    }

    private static string Required(
        YamlMappingNode mapping,
        string key,
        string path,
        string? prefix = null
    ) =>
        Optional(mapping, key)
        ?? throw Invalid(
            path,
            $"{(prefix is null ? string.Empty : prefix + ".")}{key} is required"
        );

    private static string? Optional(YamlMappingNode mapping, string key)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            ? (node as YamlScalarNode)?.Value?.Trim()
            : null;
    }

    private static InvalidDataException Invalid(string path, string message) =>
        new($"Invalid registry definition '{path}': {message}.");
}
