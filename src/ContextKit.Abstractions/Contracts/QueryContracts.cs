namespace GroundKit.Core.Contracts;

public sealed record PackageResolutionRequest(
    string Query,
    string? VersionHint = null,
    int MaxResults = 5
);

public sealed record PackageResolutionResult(
    string PackageId,
    string DisplayName,
    string? Version,
    string Summary,
    double Score
);

public sealed record DocsQueryRequest(
    string PackageId,
    string Topic,
    RetrievalOptions? Options = null
);

public sealed record DocsQueryResponse(
    string PackageId,
    string? Version,
    IReadOnlyList<DocsQueryHit> Hits,
    int TotalTokens
);

public sealed record DocsQueryHit(
    string DocumentTitle,
    string SectionTitle,
    string Content,
    int TokenEstimate,
    bool HasCode,
    double Score,
    /// <summary>
    /// The document's path inside its source, so a caller can cite or open what it was told.
    /// Appended with a default to keep existing positional construction source compatible.
    /// </summary>
    string? Path = null,
    string? ChunkId = null
);

public enum SearchMode
{
    Lexical,
    Semantic,
    Hybrid,
}

public sealed record RetrievalOptions(
    int MaxTokens = 2_000,
    int MaxHits = 8,
    double RelativeScoreCutoff = 0.5,
    SearchMode? SearchMode = null,
    bool IncludeAdjacentChunks = false,
    int AdjacentChunkCount = 1,
    bool IncludeReferences = false
);
