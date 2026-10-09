using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using GroundKit.Configuration;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace GroundKit.Ingestion.Services;

public sealed class PackageDownloadService(
    IContextRegistryClient registryClient,
    IPackageStore packageStore,
    IDocumentPackageBuilder packageBuilder,
    IHttpClientFactory httpClientFactory,
    GroundKitOptions options,
    ILogger<PackageDownloadService> logger
) : IPackageDownloadService
{
    public async Task<string> InstallAsync(
        string packageOrSource,
        string? version = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageOrSource);

        if (
            File.Exists(packageOrSource)
            && packageOrSource.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
        )
        {
            return await packageStore.ImportAsync(packageOrSource, cancellationToken);
        }

        if (
            Uri.TryCreate(packageOrSource, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
        )
        {
            if (IsPackageUrl(uri))
            {
                return await DownloadUrlAsync(uri, cancellationToken);
            }

            return await BuildLocallyAsync(packageOrSource, version, cancellationToken);
        }

        var reference = PackageReference.Parse(packageOrSource);
        var requestedVersion = version ?? reference.Version;
        var local = await packageStore.GetPackageAsync(reference.Name, cancellationToken);
        if (
            local is not null
            && (
                requestedVersion is null
                || string.Equals(
                    local.Version,
                    requestedVersion,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            return local.PackagePath;
        }

        IReadOnlyList<RegistryPackage> matches;
        try
        {
            matches = await registryClient.SearchAsync(
                reference.Registry,
                reference.Name,
                requestedVersion,
                cancellationToken
            );
        }
        catch (Exception exception)
            when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                exception,
                "Registry unavailable for {PackageReference}; falling back to local build.",
                packageOrSource
            );
            return await BuildLocallyAsync(reference.Name, requestedVersion, cancellationToken);
        }

        var match = matches.FirstOrDefault();
        if (match is not null)
        {
            // Deliberately outside the fallback: a registry that answers but cannot serve the
            // artifact (missing manifest, size or checksum mismatch) is a broken catalog entry, and
            // reporting it as a failed local build would hide the real cause.
            return await InstallRegistryPackageAsync(
                match.Registry,
                match.Name,
                match.Version,
                cancellationToken
            );
        }

        return await BuildLocallyAsync(reference.Name, requestedVersion, cancellationToken);
    }

    public async Task<string> InstallRegistryPackageAsync(
        string registry,
        string name,
        string? version = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var targetVersion = version;
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            var matches = await registryClient.SearchAsync(
                registry,
                name,
                cancellationToken: cancellationToken
            );
            targetVersion =
                matches.FirstOrDefault()?.Version
                ?? throw new InvalidOperationException(
                    $"No registry package found for '{registry}/{name}'."
                );
        }

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"groundkit-{Guid.NewGuid():N}.db");
        try
        {
            await registryClient.DownloadAsync(
                registry,
                name,
                targetVersion,
                temporaryPath,
                cancellationToken
            );
            return await packageStore.ImportAsync(temporaryPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<string> DownloadUrlAsync(Uri uri, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"groundkit-{Guid.NewGuid():N}.db");
        try
        {
            using var response = await httpClientFactory
                .CreateClient("groundkit")
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            EnsureResponseSize(response.Content.Headers);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = File.Create(temporaryPath))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            return await packageStore.ImportAsync(temporaryPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<string> BuildLocallyAsync(
        string source,
        string? version,
        CancellationToken cancellationToken
    )
    {
        var catalogEntry = LibraryCatalog.Find(source);
        var result = await packageBuilder.BuildAsync(
            catalogEntry?.Repository ?? source,
            catalogEntry?.DocsPath,
            cancellationToken
        );
        if (!string.IsNullOrWhiteSpace(version))
        {
            result = result with { Manifest = result.Manifest with { Version = version } };
        }

        return await packageStore.SaveAsync(result, cancellationToken);
    }

    private void EnsureResponseSize(HttpContentHeaders headers)
    {
        if (headers.ContentLength > options.MaxResponseBytes)
        {
            throw new InvalidOperationException(
                $"Package response exceeds {options.MaxResponseBytes:N0} bytes."
            );
        }
    }

    private static bool IsPackageUrl(Uri uri)
    {
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
}
