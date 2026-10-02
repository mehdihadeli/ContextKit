namespace GroundKit.Core.Contracts;

public enum SourceKind
{
    Unknown = 0,
    LocalDirectory = 1,
    GitRepository = 2,
    LlmsText = 3,
    RawPage = 4,
    ZipArchive = 5,
    HtmlIndex = 6,
}

public sealed record DocumentationSource(
    SourceKind Kind,
    string CanonicalId,
    string DisplayName,
    string Location,
    string? DocsPath = null,
    string? Version = null,
    string? Tag = null,
    string? Branch = null,
    string? Fingerprint = null,
    DateTimeOffset? LastBuiltAt = null,
    DateTimeOffset? LastCheckedAt = null,
    IReadOnlyList<string>? ExcludePaths = null
);

public sealed record PackageManifest(
    string PackageId,
    string DisplayName,
    string? Version,
    SourceKind SourceKind,
    string SourceLocation,
    string SourceCanonicalId,
    string? SourceFingerprint,
    DateTimeOffset BuiltAt,
    string BuilderVersion,
    int DocumentCount,
    int ChunkCount,
    int WarningCount
);

public sealed record DocumentRecord(
    string DocumentId,
    string Title,
    string Path,
    string Content,
    string? Description = null,
    string? Language = null
);

public sealed record ChunkRecord(
    string ChunkId,
    string DocumentId,
    string DocumentTitle,
    string SectionTitle,
    string Path,
    string Content,
    int TokenEstimate,
    bool HasCode,
    string ContentHash,
    int Sequence,
    string? PreviousChunkId = null,
    string? NextChunkId = null
);
