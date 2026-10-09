namespace GroundKit.Core.Contracts;

public sealed record PackageSummary(
    string PackageId,
    string DisplayName,
    string? Version,
    int DocumentCount,
    int ChunkCount,
    DateTimeOffset BuiltAt,
    string PackagePath,
    int WarningCount = 0);