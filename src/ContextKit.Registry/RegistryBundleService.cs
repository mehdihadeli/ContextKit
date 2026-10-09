using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace GroundKit.Registry;

public sealed record RegistryBundleEntry(string Path, long Size, string Sha256);

public sealed record RegistryBundleIndex(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    IReadOnlyList<RegistryBundleEntry> Packages
);

public sealed class RegistryBundleService
{
    private static readonly DateTimeOffset DeterministicTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<string> CreateAsync(
        string packageDirectory,
        string destinationPath,
        CancellationToken cancellationToken = default
    )
    {
        var packages = Directory
            .EnumerateFiles(packageDirectory, "*.db", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new RegistryBundleEntry(
                "packages/" + Path.GetFileName(path),
                new FileInfo(path).Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            ))
            .ToArray();

        if (packages.Length == 0)
        {
            throw new InvalidOperationException($"No .db packages found in '{packageDirectory}'.");
        }

        var index = new RegistryBundleIndex(1, DeterministicTimestamp, packages);
        var indexJson = JsonSerializer.Serialize(index, JsonOptions) + Environment.NewLine;
        var checksums =
            string.Join(
                Environment.NewLine,
                packages.Select(package => $"{package.Sha256}  {package.Path}")
            ) + Environment.NewLine;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

        if (destinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            CreateZip(packageDirectory, destinationPath, packages, indexJson, checksums);
        }
        else if (destinationPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            await CreateTarGzAsync(
                packageDirectory,
                destinationPath,
                packages,
                indexJson,
                checksums,
                cancellationToken
            );
        }
        else
        {
            throw new ArgumentException(
                "Bundle path must end with .zip or .tar.gz.",
                nameof(destinationPath)
            );
        }

        return destinationPath;
    }

    public async Task<IReadOnlyList<string>> ImportAsync(
        string bundlePath,
        string packageDirectory,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(bundlePath))
        {
            throw new FileNotFoundException("Bundle file was not found.", bundlePath);
        }

        var staging = Path.Combine(
            Path.GetTempPath(),
            "groundkit-bundle",
            Guid.NewGuid().ToString("n")
        );
        Directory.CreateDirectory(staging);
        try
        {
            if (bundlePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(bundlePath, staging);
            }
            else if (bundlePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var file = File.OpenRead(bundlePath);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, staging, overwriteFiles: false);
            }
            else
            {
                throw new ArgumentException(
                    "Bundle path must end with .zip or .tar.gz.",
                    nameof(bundlePath)
                );
            }

            var index =
                JsonSerializer.Deserialize<RegistryBundleIndex>(
                    await File.ReadAllTextAsync(
                        Path.Combine(staging, "index.json"),
                        cancellationToken
                    )
                ) ?? throw new InvalidDataException("Bundle index is invalid.");
            if (
                index.SchemaVersion != 1
                || index.Packages.Count == 0
                || !File.Exists(Path.Combine(staging, "SHA256SUMS"))
            )
            {
                throw new InvalidDataException("Bundle index or checksum file is invalid.");
            }

            var checksumEntries = (
                await File.ReadAllLinesAsync(Path.Combine(staging, "SHA256SUMS"), cancellationToken)
            )
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line =>
                    line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries)
                )
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);

            var destinations = new List<(string Source, string Destination)>();
            foreach (var package in index.Packages)
            {
                var relativePath = package.Path.Replace('/', Path.DirectorySeparatorChar);
                if (
                    Path.IsPathRooted(relativePath)
                    || relativePath.Contains(
                        ".." + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new InvalidDataException($"Unsafe bundle path '{package.Path}'.");
                }

                var sourcePath = Path.GetFullPath(Path.Combine(staging, relativePath));
                if (
                    !sourcePath.StartsWith(
                        Path.GetFullPath(staging) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase
                    ) || !File.Exists(sourcePath)
                )
                {
                    throw new InvalidDataException(
                        $"Bundle package is missing or outside staging path: '{package.Path}'."
                    );
                }

                var info = new FileInfo(sourcePath);
                var hash = Convert
                    .ToHexString(
                        SHA256.HashData(await File.ReadAllBytesAsync(sourcePath, cancellationToken))
                    )
                    .ToLowerInvariant();
                if (
                    !checksumEntries.TryGetValue(package.Path, out var listedHash)
                    || !listedHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)
                    || info.Length != package.Size
                    || !hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)
                )
                {
                    throw new InvalidDataException($"Checksum mismatch for '{package.Path}'.");
                }

                var destination = Path.Combine(packageDirectory, Path.GetFileName(sourcePath));
                if (
                    destinations.Any(item =>
                        item.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    throw new InvalidDataException($"Duplicate package path '{package.Path}'.");
                }
                destinations.Add((sourcePath, destination));
            }

            Directory.CreateDirectory(packageDirectory);
            foreach (var item in destinations)
            {
                File.Copy(item.Source, item.Destination, overwrite: false);
            }
            return destinations.Select(item => item.Destination).ToArray();
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static void CreateZip(
        string packageDirectory,
        string destinationPath,
        IReadOnlyList<RegistryBundleEntry> packages,
        string indexJson,
        string checksums
    )
    {
        using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        AddZipEntry(archive, "index.json", indexJson);
        AddZipEntry(archive, "SHA256SUMS", checksums);
        foreach (var package in packages)
        {
            var entry = archive.CreateEntry(package.Path, CompressionLevel.Optimal);
            entry.LastWriteTime = DeterministicTimestamp;
            using var source = File.OpenRead(
                Path.Combine(packageDirectory, Path.GetFileName(package.Path))
            );
            using var target = entry.Open();
            source.CopyTo(target);
        }
    }

    private static async Task CreateTarGzAsync(
        string packageDirectory,
        string destinationPath,
        IReadOnlyList<RegistryBundleEntry> packages,
        string indexJson,
        string checksums,
        CancellationToken cancellationToken
    )
    {
        await using var file = File.Create(destinationPath);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var tar = new TarWriter(gzip, leaveOpen: false);
        AddTarEntry(tar, "index.json", indexJson);
        AddTarEntry(tar, "SHA256SUMS", checksums);
        foreach (var package in packages)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, package.Path)
            {
                ModificationTime = DeterministicTimestamp,
                DataStream = File.OpenRead(
                    Path.Combine(packageDirectory, Path.GetFileName(package.Path))
                ),
            };
            tar.WriteEntry(entry);
            entry.DataStream?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void AddZipEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = DeterministicTimestamp;
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void AddTarEntry(TarWriter tar, string path, string content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
        {
            ModificationTime = DeterministicTimestamp,
            DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
        };
        tar.WriteEntry(entry);
        entry.DataStream?.Dispose();
    }
}
