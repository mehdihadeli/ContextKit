namespace GroundKit.Core.Contracts;

public sealed record BuildResult(
    DocumentationSource Source,
    PackageManifest Manifest,
    IReadOnlyList<BuildWarning> Warnings,
    IReadOnlyList<DocumentRecord> Documents,
    IReadOnlyList<ChunkRecord> Chunks
);

public sealed record BuildWarning(BuildWarningCode Code, string Message, string? SourcePath = null);

public enum BuildWarningCode
{
    Unknown = 0,
    LowSectionCount = 1,
    DocsPathNotExplicit = 2,
    SourceFetchFallback = 3,
    EmptyDocumentSkipped = 4,
    DuplicateChunkSkipped = 5,
    ReadmeOnly = 6,
}
