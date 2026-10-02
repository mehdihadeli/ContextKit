using System.Net.Http.Json;
using System.Security.Cryptography;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;

namespace GroundKit.Ingestion.Services;

public sealed class ContextRegistryClient(HttpClient httpClient) : IContextRegistryClient
{
    private const string DefaultRegistryUrl =
        "https://mehdihadeli.github.io/groundkit/registry/index.json";

    private bool UsesStaticCatalog => httpClient.BaseAddress?.AbsolutePath.TrimEnd('/')
        .EndsWith("/index.json", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<IReadOnlyList<RegistryPackage>> SearchAsync(
        string registry,
        string name,
        string? version = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (UsesStaticCatalog)
        {
            var catalog = await ReadCatalogAsync(cancellationToken);
            return catalog.Packages.Where(entry => entry.Registry == registry && entry.Name == name
                && (string.IsNullOrWhiteSpace(version) || entry.Version == version))
            .OrderByDescending(entry => entry.Version == "latest")
            .ThenByDescending(entry => Version.TryParse(entry.Version.TrimStart('v'), out var parsed) ? parsed : null)
            .ThenByDescending(entry => entry.Version, StringComparer.Ordinal)
            .Select(entry => new RegistryPackage(entry.Name, entry.Registry, entry.Version, entry.Description, entry.Size))
            .ToArray();
        }

        var query =
            $"search?registry={Uri.EscapeDataString(registry)}&name={Uri.EscapeDataString(name)}";
        if (!string.IsNullOrWhiteSpace(version))
        {
            query += $"&version={Uri.EscapeDataString(version)}";
        }

        using var response = await httpClient.GetAsync(query, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<RegistryPackage[]>(cancellationToken) ?? [];
    }

    public async Task<RegistryPackageMetadata> GetMetadataAsync(
        string registry,
        string name,
        string version,
        CancellationToken cancellationToken = default
    )
    {
        if (UsesStaticCatalog)
        {
            var entry = await FindCatalogEntryAsync(registry, name, version, cancellationToken);
            return new RegistryPackageMetadata(entry.Registry, entry.Name, entry.Version, entry.SourceCommit);
        }
        using var response = await httpClient.GetAsync(
            $"packages/{Uri.EscapeDataString(registry)}/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}",
            cancellationToken
        );
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<RegistryPackageMetadata>(cancellationToken)
            ?? throw new InvalidOperationException("Registry returned empty package metadata.");
    }

    public async Task DownloadAsync(
        string registry,
        string name,
        string version,
        string destinationPath,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (UsesStaticCatalog)
        {
            var entry = await FindCatalogEntryAsync(registry, name, version, cancellationToken);
            await DownloadCatalogEntryAsync(entry, destinationPath, cancellationToken);
            return;
        }
        using var response = await httpClient.GetAsync(
            $"packages/{Uri.EscapeDataString(registry)}/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}/download",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        await EnsureSuccessAsync(response, cancellationToken);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destinationPath);
        await input.CopyToAsync(output, cancellationToken);
    }

    private async Task<RegistryCatalog> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        var catalogUri = new UriBuilder(httpClient.BaseAddress!)
        {
            Path = httpClient.BaseAddress!.AbsolutePath.TrimEnd('/'),
        }.Uri;
        using var response = await httpClient.GetAsync(catalogUri, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var catalog = await response.Content.ReadFromJsonAsync<RegistryCatalog>(cancellationToken);
        if (catalog is null || catalog.SchemaVersion != 1 || catalog.Packages is null)
        {
            throw new InvalidDataException("Unsupported or invalid registry catalog.");
        }
        var identities = new HashSet<(string Registry, string Name, string Version)>();
        foreach (var entry in catalog.Packages)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Registry)
                || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Version)
                || entry.Size <= 0 || entry.Sha256 is null || entry.Sha256.Length != 64
                || !entry.Sha256.All(Uri.IsHexDigit)
                || !Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var downloadUri)
                || downloadUri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(downloadUri.UserInfo)
                || !identities.Add((entry.Registry, entry.Name, entry.Version)))
            {
                throw new InvalidDataException("Registry catalog contains an invalid or duplicate package.");
            }
        }
        return catalog;
    }

    private async Task<RegistryCatalogEntry> FindCatalogEntryAsync(
        string registry, string name, string version, CancellationToken cancellationToken)
    {
        var catalog = await ReadCatalogAsync(cancellationToken);
        return catalog.Packages.SingleOrDefault(entry => entry.Registry == registry
            && entry.Name == name && entry.Version == version)
            ?? throw new HttpRequestException($"No registry package found for '{registry}/{name}@{version}'.",
                null, System.Net.HttpStatusCode.NotFound);
    }

    private async Task DownloadCatalogEntryAsync(
        RegistryCatalogEntry entry, string destinationPath, CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await httpClient.GetAsync(entry.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            if (response.Content.Headers.ContentLength is { } length && length != entry.Size)
            {
                throw new InvalidDataException("Registry package size does not match the catalog.");
            }
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if (output.Length + read > entry.Size)
                    {
                        throw new InvalidDataException("Registry package exceeds the catalog size.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                output.Position = 0;
                var hash = await SHA256.HashDataAsync(output, cancellationToken);
                if (output.Length != entry.Size
                    || !CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(entry.Sha256)))
                {
                    throw new InvalidDataException("Registry package checksum or size does not match the catalog.");
                }
            }
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Registry request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}"
        );
    }

    internal static string ResolveBaseUrl() =>
        Environment.GetEnvironmentVariable("GROUNDKIT_REGISTRY_URL")?.TrimEnd('/')
        ?? DefaultRegistryUrl;
}
