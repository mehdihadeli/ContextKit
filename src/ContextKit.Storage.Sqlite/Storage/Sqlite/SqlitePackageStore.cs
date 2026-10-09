using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Observability.Telemetry;
using GroundKit.Semantic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GroundKit.Storage.Sqlite;

public sealed class SqlitePackageStore(
    PackageStoreOptions options,
    ILogger<SqlitePackageStore> logger,
    ISemanticSearchContext? semanticRuntime = null,
    SemanticSearchIndex? semanticIndex = null
) : IPackageStore
{
    public async Task<string> SaveAsync(
        BuildResult buildResult,
        CancellationToken cancellationToken = default
    )
    {
        Directory.CreateDirectory(options.RootPath);

        var versionLabel = string.IsNullOrWhiteSpace(buildResult.Manifest.Version)
            ? "dev"
            : buildResult.Manifest.Version;
        var packagePath = Path.Combine(
            options.RootPath,
            ResolvePackageFileName(buildResult.Manifest.PackageId, versionLabel)
        );

        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        using var activity = GroundKitTelemetry.ActivitySource.StartActivity(
            GroundKitTelemetry.Activities.PackageBuild
        );
        var startedAt = DateTimeOffset.UtcNow;

        await using var context = CreateDbContext(packagePath);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureSearchSchemaAsync(context, cancellationToken);

        context.Manifests.Add(MapManifest(buildResult.Manifest));
        context.Sources.Add(MapSource(buildResult.Source, buildResult.Manifest.PackageId));
        context.Documents.AddRange(buildResult.Documents.Select(MapDocument));
        context.Chunks.AddRange(buildResult.Chunks.Select(MapChunk));
        context.Warnings.AddRange(buildResult.Warnings.Select(MapWarning));

        await context.SaveChangesAsync(cancellationToken);
        await RebuildSearchIndexAsync(context, cancellationToken);

        GroundKitTelemetry.Metrics.PackagesInstalled.Add(1);
        GroundKitTelemetry.Metrics.BuildDurationMs.Record(
            (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
        );
        logger.LogInformation(
            "Saved package {PackageId} to {PackagePath}.",
            buildResult.Manifest.PackageId,
            packagePath
        );

        return packagePath;
    }

    public async Task<string> ImportAsync(
        string packageFilePath,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFilePath);

        if (!File.Exists(packageFilePath))
        {
            throw new FileNotFoundException("Package file was not found.", packageFilePath);
        }

        Directory.CreateDirectory(options.RootPath);

        await using var sourceContext = CreateDbContext(packageFilePath);
        var manifest = await sourceContext
            .Manifests.AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (manifest is null)
        {
            throw new InvalidOperationException(
                "Package file does not contain GroundKit manifest metadata."
            );
        }

        // The destination name comes from the package's own identity rather than from the caller's
        // file name: downloads arrive as a temporary "groundkit-<guid>.db", and keeping that name
        // would store the same package twice under two different names on every re-import.
        var versionLabel = string.IsNullOrWhiteSpace(manifest.Version) ? "dev" : manifest.Version;
        var destinationPath = Path.Combine(
            options.RootPath,
            ResolvePackageFileName(manifest.PackageId, versionLabel)
        );

        if (
            !Path.GetFullPath(packageFilePath)
                .Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase)
        )
        {
            File.Copy(packageFilePath, destinationPath, overwrite: true);
        }

        logger.LogInformation(
            "Imported package {PackageId}@{Version} from {SourcePath} to {DestinationPath}.",
            manifest.PackageId,
            versionLabel,
            packageFilePath,
            destinationPath
        );

        return destinationPath;
    }

    /// <summary>
    /// Builds the on-disk name for a package. Identity and version are the store's key, so the name
    /// is stable across rebuilds and re-imports, and invalid file name characters are replaced
    /// because the values come from an artifact the store did not create.
    /// </summary>
    private static string ResolvePackageFileName(string packageId, string versionLabel)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(packageId.Length + versionLabel.Length + 2);
        foreach (var character in packageId)
        {
            builder.Append(
                invalid.Contains(character) || character is '/' or '\\' ? '_' : character
            );
        }

        builder.Append('@');
        foreach (var character in versionLabel)
        {
            builder.Append(
                invalid.Contains(character) || character is '/' or '\\' ? '_' : character
            );
        }

        builder.Append(".db");
        return builder.ToString();
    }

    public async Task<string> ExportAsync(
        string packageId,
        string destinationPath,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var package = await FindPackageAsync(packageId, cancellationToken);
        if (package is null)
        {
            throw new InvalidOperationException($"Package '{packageId}' was not found.");
        }

        var exportPath = ResolveExportPath(destinationPath, package);
        var exportDirectory = Path.GetDirectoryName(exportPath);
        if (!string.IsNullOrWhiteSpace(exportDirectory))
        {
            Directory.CreateDirectory(exportDirectory);
        }

        File.Copy(package.PackagePath, exportPath, overwrite: true);
        logger.LogInformation(
            "Exported package {PackageId} from {PackagePath} to {ExportPath}.",
            package.PackageId,
            package.PackagePath,
            exportPath
        );

        return exportPath;
    }

    public async Task<IReadOnlyList<PackageSummary>> ListAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(options.RootPath))
        {
            return [];
        }

        var summaries = new List<PackageSummary>();
        foreach (
            var packagePath in Directory.EnumerateFiles(
                options.RootPath,
                "*.db",
                SearchOption.TopDirectoryOnly
            )
        )
        {
            await using var context = CreateDbContext(packagePath);
            var manifest = await context
                .Manifests.AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
            if (manifest is not null)
            {
                summaries.Add(MapSummary(manifest, packagePath));
            }
        }

        return summaries
            .OrderBy(summary => summary.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(summary => summary.BuiltAt)
            .ToList();
    }

    public Task<PackageSummary?> GetPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default
    ) => FindPackageAsync(packageId, cancellationToken);

    public async Task<int> RemoveAsync(
        string packageId,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(options.RootPath))
        {
            return 0;
        }

        var removedCount = 0;
        foreach (
            var packagePath in Directory
                .EnumerateFiles(options.RootPath, "*.db", SearchOption.TopDirectoryOnly)
                .ToList()
        )
        {
            await using var context = CreateDbContext(packagePath);
            var manifest = await context
                .Manifests.AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);

            if (
                manifest is null
                || !manifest.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            await context.DisposeAsync();
            File.Delete(packagePath);
            removedCount++;
        }

        if (removedCount > 0)
        {
            logger.LogInformation(
                "Removed {RemovedCount} package file(s) for {PackageId}.",
                removedCount,
                packageId
            );
        }

        return removedCount;
    }

    public async Task<int> RemoveAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!Directory.Exists(options.RootPath))
        {
            return 0;
        }

        foreach (
            var packagePath in Directory
                .EnumerateFiles(options.RootPath, "*.db", SearchOption.TopDirectoryOnly)
                .ToList()
        )
        {
            await using var context = CreateDbContext(packagePath);
            var manifest = await context
                .Manifests.AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);

            if (
                manifest is null
                || !manifest.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(manifest.Version, version, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            await context.DisposeAsync();
            File.Delete(packagePath);
            logger.LogInformation(
                "Removed package {PackageId}@{Version} from {PackagePath}.",
                packageId,
                version,
                packagePath
            );
            return 1;
        }

        return 0;
    }

    public async Task<DocumentationSource?> GetSourceAsync(
        string packageId,
        CancellationToken cancellationToken = default
    )
    {
        var package = await FindPackageAsync(packageId, cancellationToken);
        if (package is null)
        {
            return null;
        }

        await using var context = CreateDbContext(package.PackagePath);
        var source = await context.Sources.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return source is null ? null : MapSource(source);
    }

    public async Task<DocsQueryResponse> QueryAsync(
        DocsQueryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = GroundKitTelemetry.ActivitySource.StartActivity(
            GroundKitTelemetry.Activities.Search
        );
        activity?.SetTag("groundkit.package_id", request.PackageId);

        var package = await FindPackageAsync(request.PackageId, cancellationToken);

        if (package is null)
        {
            throw new InvalidOperationException($"Package '{request.PackageId}' was not found.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        await using var context = CreateDbContext(package.PackagePath);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var optionsValue = request.Options ?? new RetrievalOptions();
        var mode = optionsValue.SearchMode ?? semanticRuntime?.Mode ?? SearchMode.Lexical;
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unknown search mode.");
        if (optionsValue.MaxTokens <= 0 || optionsValue.MaxHits <= 0)
            throw new ArgumentException("Token and hit limits must be greater than zero.");
        if (optionsValue.AdjacentChunkCount is < 1 or > 3)
            throw new ArgumentException("Adjacent chunk count must be between 1 and 3.");
        if (!double.IsFinite(optionsValue.RelativeScoreCutoff) || optionsValue.RelativeScoreCutoff is < 0 or > 1)
            throw new ArgumentException("Relative score cutoff must be between 0 and 1.");
        if (string.IsNullOrWhiteSpace(request.Topic)) throw new ArgumentException("Query topic is required.");
        if (mode != SearchMode.Lexical && semanticIndex is null)
            throw new InvalidOperationException("Semantic search is not configured. Run ck semantic provider install onnx and ck semantic model install bge-micro-v2.");
        var rawHits = new List<DocsQueryHit>();
        if (mode != SearchMode.Semantic)
        {
            var ftsQuery = BuildFtsQuery(request.Topic);

            const string sql =
                @"
SELECT chunks.document_title,
       chunks.section_title,
       chunks.content,
       chunks.token_estimate,
       chunks.has_code,
    bm25(chunk_search, 5.0, 10.0, 1.0) AS score,
    chunks.path,
    chunks.chunk_id
FROM chunk_search
INNER JOIN chunks ON chunks.chunk_id = chunk_search.chunk_id
WHERE chunk_search MATCH $query
ORDER BY score, chunks.path, chunks.sequence
LIMIT $limit;";

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$query", ftsQuery);
            command.Parameters.AddWithValue("$limit", Math.Max(20, optionsValue.MaxHits));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rawHits.Add(
                    new DocsQueryHit(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.GetBoolean(4),
                        -reader.GetDouble(5),
                        reader.GetString(6),
                        reader.GetString(7)
                    )
                );
            }
        }

        if (mode != SearchMode.Lexical)
        {
            var semanticHits = await semanticIndex!.SearchAsync(
                package.PackagePath, request.Topic, Math.Max(20, optionsValue.MaxHits), cancellationToken
            );
            rawHits = mode == SearchMode.Hybrid
                ? SemanticSearchIndex.Fuse(rawHits, semanticHits).ToList()
                : semanticHits.ToList();
        }

        var filteredHits = ApplyRelativeCutoff(rawHits, optionsValue.RelativeScoreCutoff);
        if (optionsValue.IncludeAdjacentChunks)
        {
            filteredHits = ExpandAdjacentChunks(
                context,
                filteredHits,
                optionsValue.AdjacentChunkCount
            );
        }
        if (optionsValue.IncludeReferences)
        {
            filteredHits = ExpandReferences(context, filteredHits);
        }
        var selectedHits = TrimToTokenBudget(
            filteredHits,
            optionsValue.MaxTokens,
            optionsValue.MaxHits
        );
        var totalTokens = selectedHits.Sum(hit => hit.TokenEstimate);

        GroundKitTelemetry.Metrics.ResultsReturned.Add(selectedHits.Count);
        GroundKitTelemetry.Metrics.SearchDurationMs.Record(
            (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
        );

        return new DocsQueryResponse(package.PackageId, package.Version, selectedHits, totalTokens);
    }

    private async Task<PackageSummary?> FindPackageAsync(
        string packageSelector,
        CancellationToken cancellationToken
    )
    {
        var reference = PackageReference.Parse(packageSelector);
        var candidates = (await ListAsync(cancellationToken))
            .Where(summary =>
                summary.PackageId.Equals(reference.Name, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        if (reference.Version is null)
        {
            // A bare id keeps its long-standing meaning: the most recently built package.
            return candidates.OrderByDescending(summary => summary.BuiltAt).First();
        }

        // `react@18` and `react@18.2` are ordinary requests, so resolve them against the stored
        // versions instead of demanding an exact string. A request that matches nothing still returns
        // null, which is what lets a caller fall through to the registry.
        var resolved = PackageVersionResolver.Resolve(
            reference.Version,
            candidates.Select(summary => summary.Version)
        );

        return resolved is null
            ? null
            : candidates
                .Where(summary =>
                    string.Equals(summary.Version, resolved, StringComparison.OrdinalIgnoreCase)
                )
                .OrderByDescending(summary => summary.BuiltAt)
                .FirstOrDefault();
    }

    private static async Task EnsureSearchSchemaAsync(
        PackageDbContext context,
        CancellationToken cancellationToken
    )
    {
        await context.Database.ExecuteSqlRawAsync(
            @"
CREATE VIRTUAL TABLE IF NOT EXISTS chunk_search
USING fts5(chunk_id UNINDEXED, document_title, section_title, content);",
            cancellationToken
        );
    }

    private static async Task RebuildSearchIndexAsync(
        PackageDbContext context,
        CancellationToken cancellationToken
    )
    {
        await context.Database.ExecuteSqlRawAsync("DELETE FROM chunk_search;", cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            @"
INSERT INTO chunk_search (chunk_id, document_title, section_title, content)
SELECT chunk_id, document_title, section_title, content
FROM chunks;",
            cancellationToken
        );
    }

    private static PackageDbContext CreateDbContext(string packagePath)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PackageDbContext>();
        optionsBuilder.UseSqlite(
            new SqliteConnectionStringBuilder
            {
                DataSource = packagePath,
                Pooling = false,
            }.ToString()
        );
        return new PackageDbContext(optionsBuilder.Options);
    }

    private static PackageSummary MapSummary(ManifestEntity manifest, string packagePath)
    {
        return new PackageSummary(
            manifest.PackageId,
            manifest.DisplayName,
            manifest.Version,
            manifest.DocumentCount,
            manifest.ChunkCount,
            manifest.BuiltAt,
            packagePath,
            manifest.WarningCount
        );
    }

    private static ManifestEntity MapManifest(PackageManifest manifest)
    {
        return new ManifestEntity
        {
            PackageId = manifest.PackageId,
            DisplayName = manifest.DisplayName,
            Version = manifest.Version,
            SourceKind = manifest.SourceKind,
            SourceLocation = manifest.SourceLocation,
            SourceCanonicalId = manifest.SourceCanonicalId,
            SourceFingerprint = manifest.SourceFingerprint,
            BuiltAt = manifest.BuiltAt,
            BuilderVersion = manifest.BuilderVersion,
            DocumentCount = manifest.DocumentCount,
            ChunkCount = manifest.ChunkCount,
            WarningCount = manifest.WarningCount,
        };
    }

    private static SourceMetadataEntity MapSource(DocumentationSource source, string packageId)
    {
        return new SourceMetadataEntity
        {
            PackageId = packageId,
            SourceKind = source.Kind,
            CanonicalId = source.CanonicalId,
            DisplayName = source.DisplayName,
            Location = source.Location,
            DocsPath = source.DocsPath,
            Version = source.Version,
            Tag = source.Tag,
            Branch = source.Branch,
            Fingerprint = source.Fingerprint,
            LastBuiltAt = source.LastBuiltAt,
            LastCheckedAt = source.LastCheckedAt,
        };
    }

    private static DocumentationSource MapSource(SourceMetadataEntity source)
    {
        return new DocumentationSource(
            source.SourceKind,
            source.CanonicalId,
            source.DisplayName,
            source.Location,
            source.DocsPath,
            source.Version,
            source.Tag,
            source.Branch,
            source.Fingerprint,
            source.LastBuiltAt,
            source.LastCheckedAt
        );
    }

    private static DocumentEntity MapDocument(DocumentRecord document)
    {
        return new DocumentEntity
        {
            DocumentId = document.DocumentId,
            Title = document.Title,
            Path = document.Path,
            Content = document.Content,
            Description = document.Description,
            Language = document.Language,
        };
    }

    private static ChunkEntity MapChunk(ChunkRecord chunk)
    {
        return new ChunkEntity
        {
            ChunkId = chunk.ChunkId,
            DocumentId = chunk.DocumentId,
            DocumentTitle = chunk.DocumentTitle,
            SectionTitle = chunk.SectionTitle,
            Path = chunk.Path,
            Content = chunk.Content,
            TokenEstimate = chunk.TokenEstimate,
            HasCode = chunk.HasCode,
            ContentHash = chunk.ContentHash,
            Sequence = chunk.Sequence,
            PreviousChunkId = chunk.PreviousChunkId,
            NextChunkId = chunk.NextChunkId,
        };
    }

    private static WarningEntity MapWarning(BuildWarning warning)
    {
        return new WarningEntity
        {
            Code = warning.Code,
            Message = warning.Message,
            SourcePath = warning.SourcePath,
        };
    }

    private static IReadOnlyList<DocsQueryHit> ApplyRelativeCutoff(
        IReadOnlyList<DocsQueryHit> hits,
        double relativeScoreCutoff
    )
    {
        if (hits.Count == 0)
        {
            return hits;
        }

        var topScore = hits[0].Score;
        if (topScore <= 0)
        {
            return hits;
        }

        var cutoff = topScore * Math.Max(relativeScoreCutoff, 0.1d);
        return hits.Where(hit => hit.Score >= cutoff).ToList();
    }

    private static IReadOnlyList<DocsQueryHit> ExpandAdjacentChunks(
        PackageDbContext context,
        IReadOnlyList<DocsQueryHit> hits,
        int distance
    )
    {
        if (hits.Count == 0)
        {
            return hits;
        }

        var chunkIds = hits
            .Select(hit => hit.ChunkId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var chunks = context.Chunks.AsNoTracking().ToDictionary(chunk => chunk.ChunkId);
        var expanded = new List<DocsQueryHit>(hits.Count * (distance * 2 + 1));

        foreach (var hit in hits)
        {
            expanded.Add(hit);
            if (!chunks.TryGetValue(hit.ChunkId ?? string.Empty, out var current))
            {
                continue;
            }

            foreach (var direction in new[] { -1, 1 })
            {
                var neighbor = current;
                for (var step = 0; step < distance; step++)
                {
                    var neighborId = direction < 0 ? neighbor.PreviousChunkId : neighbor.NextChunkId;
                    if (neighborId is null || !chunks.TryGetValue(neighborId, out neighbor!))
                    {
                        break;
                    }

                    if (!chunkIds.Add(neighbor.ChunkId))
                    {
                        continue;
                    }

                    expanded.Add(
                        new DocsQueryHit(
                            neighbor.DocumentTitle,
                            neighbor.SectionTitle,
                            neighbor.Content,
                            neighbor.TokenEstimate,
                            neighbor.HasCode,
                            hit.Score - 0.000001d,
                            neighbor.Path,
                            neighbor.ChunkId
                        )
                    );
                }
            }
        }

        return expanded;
    }

    private static IReadOnlyList<DocsQueryHit> ExpandReferences(
        PackageDbContext context,
        IReadOnlyList<DocsQueryHit> hits
    )
    {
        var chunks = context.Chunks.AsNoTracking().ToList();
        var byPath = chunks
            .GroupBy(chunk => NormalizePackagePath(chunk.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(chunk => chunk.Sequence).First(), StringComparer.OrdinalIgnoreCase);
        var chunkIds = hits
            .Select(hit => hit.ChunkId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var expanded = new List<DocsQueryHit>(hits.Count * 2);

        foreach (var hit in hits)
        {
            expanded.Add(hit);
            if (hit.Path is null || hit.Content is null)
            {
                continue;
            }

            var basePath = NormalizePackagePath(hit.Path);
            var baseDirectory = basePath.Contains('/')
                ? basePath[..basePath.LastIndexOf('/')]
                : string.Empty;
            foreach (Match match in Regex.Matches(hit.Content, @"\[[^\]]+\]\(([^)\s]+)") )
            {
                var target = match.Groups[1].Value.Trim('<', '>', '"', '\'');
                if (Uri.TryCreate(target, UriKind.Absolute, out _)
                    || target.StartsWith('#')
                    || target.Contains(':', StringComparison.Ordinal))
                {
                    continue;
                }

                var targetPath = NormalizePackagePath(
                    string.IsNullOrEmpty(baseDirectory) ? target : $"{baseDirectory}/{target}"
                );
                var fragmentIndex = targetPath.IndexOf('#');
                if (fragmentIndex >= 0)
                {
                    targetPath = targetPath[..fragmentIndex];
                }

                if (!byPath.TryGetValue(targetPath, out var targetChunk) || !chunkIds.Add(targetChunk.ChunkId))
                {
                    continue;
                }

                expanded.Add(
                    new DocsQueryHit(
                        targetChunk.DocumentTitle,
                        targetChunk.SectionTitle,
                        targetChunk.Content,
                        targetChunk.TokenEstimate,
                        targetChunk.HasCode,
                        hit.Score - 0.000002d,
                        targetChunk.Path,
                        targetChunk.ChunkId
                    )
                );
                break;
            }
        }

        return expanded;
    }

    private static string NormalizePackagePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string ResolveExportPath(string destinationPath, PackageSummary package)
    {
        var fullDestinationPath = Path.GetFullPath(destinationPath);

        if (Directory.Exists(fullDestinationPath) || !Path.HasExtension(fullDestinationPath))
        {
            return Path.Combine(fullDestinationPath, Path.GetFileName(package.PackagePath));
        }

        return fullDestinationPath;
    }

    private static IReadOnlyList<DocsQueryHit> TrimToTokenBudget(
        IReadOnlyList<DocsQueryHit> hits,
        int maxTokens,
        int maxHits
    )
    {
        var total = 0;
        var selected = new List<DocsQueryHit>();

        foreach (var hit in hits)
        {
            if (selected.Count >= maxHits)
            {
                break;
            }

            if (selected.Count > 0 && (long)total + hit.TokenEstimate > maxTokens)
            {
                GroundKitTelemetry.Metrics.TokenBudgetTrims.Add(1);
                continue;
            }

            selected.Add(hit);
            total += hit.TokenEstimate;
        }

        return selected;
    }

    private static string BuildFtsQuery(string topic)
    {
        var normalized = Regex.Replace(topic, @"[^\p{L}\p{N}\p{M}_\s""]", " ");
        var terms = Regex.Matches(normalized, @"""[^""]*""|[^\s""]+")
            .Select(match => match.Value.Replace("\"", string.Empty).Trim())
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(term => $"\"{term}\"")
            .ToList();

        if (terms.Count == 0)
        {
            throw new InvalidOperationException(
                "Query topic did not contain any searchable terms."
            );
        }

        return string.Join(" OR ", terms);
    }
}
