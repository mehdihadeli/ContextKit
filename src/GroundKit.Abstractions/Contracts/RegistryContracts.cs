namespace GroundKit.Core.Contracts;

public sealed record RegistryPackage(
    string Name,
    string Registry,
    string Version,
    string? Description,
    long? Size
);

public sealed record RegistryPackageMetadata(
    string Registry,
    string Name,
    string Version,
    string? SourceCommit
);

public sealed record RegistryCatalog(
    int SchemaVersion,
    IReadOnlyList<RegistryCatalogEntry> Packages
);

public sealed record RegistryCatalogEntry(
    string Registry,
    string Name,
    string Version,
    string? Description,
    string DownloadUrl,
    long Size,
    string Sha256,
    string? SourceCommit = null
);
