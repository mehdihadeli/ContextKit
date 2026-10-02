using System.Security.Cryptography;
using System.Text.Json;
using GroundKit.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace GroundKit.Registry;

public sealed class RegistryCatalogService
{
    public async Task<string> CreateAsync(
        string definitionDirectory,
        string packageDirectory,
        string destinationDirectory,
        string assetBaseUrl,
        CancellationToken cancellationToken = default
    )
    {
        if (!Uri.TryCreate(assetBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Asset base URL must be an absolute HTTPS URL.", nameof(assetBaseUrl));
        }

        var definitions = RegistryDefinitionLoader.LoadDirectory(definitionDirectory);
        RegistryDefinitionLoader.Validate(definitions);
        var entries = new List<RegistryCatalogEntry>();
        var assets = Path.Combine(destinationDirectory, "assets");
        Directory.CreateDirectory(assets);
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

            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            var assetName = hash + ".db";
            File.Copy(path, Path.Combine(assets, assetName), overwrite: true);
            entries.Add(new RegistryCatalogEntry(
                definition.Registry, name, version, definition.Description,
                new Uri(baseUri, assetName).AbsoluteUri, stream.Length, hash
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
}
