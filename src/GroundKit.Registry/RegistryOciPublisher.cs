using System.Text.Json;
using GroundKit.Oci;

namespace GroundKit.Registry;

public sealed record RegistryOciPackage(
    string Registry,
    string Name,
    string Version,
    string Repository,
    string Digest,
    string Reference
);

public sealed record RegistryOciReferences(
    int SchemaVersion,
    string Repository,
    IReadOnlyList<RegistryOciPackage> Packages
);

/// <summary>
/// Publishes built packages to an OCI registry as single-layer artifacts. Every artifact is
/// content-addressed, so the catalog can pin a manifest digest and consumers verify the package by
/// its SHA-256 before use.
/// </summary>
public sealed class RegistryOciPublisher(OciRegistryClient client, string host, string prefix)
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Key(string registry, string name, string version) =>
        $"{registry}/{name}@{version}";

    /// <summary>
    /// Splits <c>ghcr.io/owner/repository</c> into its registry host and repository prefix.
    /// </summary>
    public static (string Host, string Prefix) Split(string ociRepository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ociRepository);
        var trimmed = ociRepository.Trim().TrimEnd('/');
        if (trimmed.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["oci://".Length..];
        }

        var separator = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            throw new ArgumentException(
                $"OCI repository '{ociRepository}' must be formatted as <host>/<repository>.",
                nameof(ociRepository)
            );
        }

        return (trimmed[..separator], trimmed[(separator + 1)..].ToLowerInvariant());
    }

    /// <summary>
    /// Pushes every resolvable package and returns the reference map consumed by
    /// <c>registry catalog-index --oci-references</c>.
    /// </summary>
    public async Task<RegistryOciReferences> PushAllAsync(
        IReadOnlyList<RegistryDefinition> definitions,
        string packageDirectory,
        Action<string>? onPushed = null,
        CancellationToken cancellationToken = default
    )
    {
        var packages = new List<RegistryOciPackage>();
        foreach (var definition in definitions)
        {
            foreach (var (version, _) in definition.ResolveVersions())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packagePath = Path.Combine(
                    packageDirectory,
                    $"{definition.Name}@{version}.db"
                );
                if (!File.Exists(packagePath))
                {
                    throw new FileNotFoundException(
                        $"Package for {definition.Registry}/{definition.Name}@{version} was not found. Run 'registry build-all' first.",
                        packagePath
                    );
                }

                var repository = $"{prefix}/{OciNames.RepositoryName(definition.Registry, definition.Name)}";
                var tag = OciNames.Sanitize(version);
                var digest = await client.PushAsync(
                    repository,
                    tag,
                    packagePath,
                    Annotations(definition, version),
                    cancellationToken
                );

                var fullRepository = $"{host}/{repository}";
                packages.Add(
                    new RegistryOciPackage(
                        definition.Registry,
                        definition.Name,
                        version,
                        fullRepository,
                        digest,
                        $"oci://{fullRepository}@{digest}"
                    )
                );
                onPushed?.Invoke($"{fullRepository}@{digest}");
            }
        }

        return new RegistryOciReferences(SchemaVersion, $"{host}/{prefix}", packages);
    }

    public static async Task WriteAsync(
        RegistryOciReferences references,
        string destinationPath,
        CancellationToken cancellationToken = default
    )
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            destinationPath,
            JsonSerializer.Serialize(references, JsonOptions) + Environment.NewLine,
            cancellationToken
        );
    }

    public static async Task<RegistryOciReferences> ReadAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        var references = JsonSerializer.Deserialize<RegistryOciReferences>(
            await File.ReadAllTextAsync(path, cancellationToken)
        );
        if (references is null || references.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException($"OCI reference map '{path}' is invalid or unsupported.");
        }

        return references;
    }

    /// <summary>Maps <c>registry/name@version</c> onto the pushed OCI reference.</summary>
    public static IReadOnlyDictionary<string, string> ToLookup(RegistryOciReferences references) =>
        references.Packages.ToDictionary(
            package => Key(package.Registry, package.Name, package.Version),
            package => package.Reference,
            StringComparer.OrdinalIgnoreCase
        );

    private static IReadOnlyDictionary<string, string> Annotations(
        RegistryDefinition definition,
        string version
    ) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["org.opencontainers.image.version"] = version,
            ["org.opencontainers.image.description"] = definition.Description,
            ["org.groundkit.registry"] = definition.Registry,
            ["org.groundkit.name"] = definition.Name,
        };
}
