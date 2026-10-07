using System.Security.Cryptography;
using System.Text.Json;
using GroundKit.Core.Contracts;
using GroundKit.Oci;
using Microsoft.Data.Sqlite;

namespace GroundKit.Registry;

public sealed class RegistryCatalogService
{
    /// <summary>
    /// Builds the static catalog.
    /// </summary>
    /// <param name="assetBaseUrl">
    /// Release asset base URL. Required unless <paramref name="ociReferences"/> is supplied, in
    /// which case packages are served from the OCI registry instead.
    /// </param>
    /// <param name="ociReferences">
    /// Maps <c>registry/name@version</c> onto an <c>oci://</c> reference. When set, no local asset
    /// copies are written and every package must have been pushed.
    /// </param>
    public async Task<string> CreateAsync(
        string definitionDirectory,
        string packageDirectory,
        string destinationDirectory,
        string? assetBaseUrl,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? ociReferences = null
    )
    {
        var usesOci = ociReferences is not null;
        var baseUri = default(Uri);
        if (!usesOci)
        {
            if (!Uri.TryCreate((assetBaseUrl ?? string.Empty).TrimEnd('/') + "/", UriKind.Absolute, out baseUri)
                || baseUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException("Asset base URL must be an absolute HTTPS URL.", nameof(assetBaseUrl));
            }
        }

        var definitions = RegistryDefinitionLoader.LoadDirectory(definitionDirectory);
        RegistryDefinitionLoader.Validate(definitions);
        var entries = new List<RegistryCatalogEntry>();
        var assets = Path.Combine(destinationDirectory, "assets");
        Directory.CreateDirectory(destinationDirectory);
        if (!usesOci)
        {
            Directory.CreateDirectory(assets);
        }

        foreach (var path in Directory.EnumerateFiles(packageDirectory, "*.db")
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT package_id, version FROM manifest";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException($"Package '{path}' has no manifest.");
            }
            var name = reader.GetString(0);
            var version = reader.GetString(1);
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException($"Package '{path}' has multiple manifests.");
            }
            var matches = definitions.Where(definition => definition.Name == name
                && definition.ResolveVersions().Any(item => item.Version == version)).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException($"Package '{name}@{version}' must match exactly one registry definition.");
            }
            var definition = matches[0];
            if (entries.Any(entry => entry.Registry == definition.Registry
                && entry.Name == name && entry.Version == version))
            {
                throw new InvalidDataException($"Duplicate package '{definition.Registry}/{name}@{version}'.");
            }

            var size = new FileInfo(path).Length;
            var hash = await ComputeHashAsync(path, cancellationToken);
            if (usesOci)
            {
                if (!ociReferences!.TryGetValue(
                        RegistryOciPublisher.Key(definition.Registry, name, version),
                        out var reference))
                {
                    throw new InvalidDataException(
                        $"Package '{definition.Registry}/{name}@{version}' has no OCI reference. Re-run 'registry push-oci'."
                    );
                }

                var parsed = OciReference.Parse(reference);
                entries.Add(new RegistryCatalogEntry(
                    definition.Registry, name, version, definition.Description,
                    $"https://{parsed.Host}/v2/{parsed.Repository}/manifests/{parsed.Reference}",
                    size, hash, OciReference: reference
                ));
                continue;
            }

            var assetName = hash + ".db";
            File.Copy(path, Path.Combine(assets, assetName), overwrite: true);
            entries.Add(new RegistryCatalogEntry(
                definition.Registry, name, version, definition.Description,
                new Uri(baseUri!, assetName).AbsoluteUri, size, hash
            ));
        }
        if (entries.Count == 0)
        {
            throw new InvalidDataException("No packages found for the static catalog.");
        }

        var catalog = new RegistryCatalog(1, entries.OrderBy(entry => entry.Registry, StringComparer.Ordinal)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Version, StringComparer.Ordinal).ToArray());
        var indexPath = Path.Combine(destinationDirectory, "index.json");
        await File.WriteAllTextAsync(indexPath,
            JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            cancellationToken);
        return indexPath;
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}

