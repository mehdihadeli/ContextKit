using GroundKit.Core.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GroundKit.Storage.Sqlite;

internal sealed class PackageDbContext(DbContextOptions<PackageDbContext> options)
    : DbContext(options)
{
    public DbSet<ManifestEntity> Manifests => Set<ManifestEntity>();
    public DbSet<SourceMetadataEntity> Sources => Set<SourceMetadataEntity>();
    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();
    public DbSet<ChunkEntity> Chunks => Set<ChunkEntity>();
    public DbSet<WarningEntity> Warnings => Set<WarningEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManifestEntity>(entity =>
        {
            entity.ToTable("manifest");
            entity.HasKey(item => item.PackageId);
            entity.Property(item => item.PackageId).HasColumnName("package_id");
            entity.Property(item => item.DisplayName).HasColumnName("display_name");
            entity.Property(item => item.Version).HasColumnName("version");
            entity.Property(item => item.SourceKind).HasColumnName("source_kind");
            entity.Property(item => item.SourceLocation).HasColumnName("source_location");
            entity.Property(item => item.SourceCanonicalId).HasColumnName("source_canonical_id");
            entity.Property(item => item.SourceFingerprint).HasColumnName("source_fingerprint");
            entity.Property(item => item.BuiltAt).HasColumnName("built_at");
            entity.Property(item => item.BuilderVersion).HasColumnName("builder_version");
            entity.Property(item => item.DocumentCount).HasColumnName("document_count");
            entity.Property(item => item.ChunkCount).HasColumnName("chunk_count");
            entity.Property(item => item.WarningCount).HasColumnName("warning_count");
        });

        modelBuilder.Entity<SourceMetadataEntity>(entity =>
        {
            entity.ToTable("source_metadata");
            entity.HasKey(item => item.PackageId);
            entity.Property(item => item.PackageId).HasColumnName("package_id");
            entity.Property(item => item.SourceKind).HasColumnName("source_kind");
            entity.Property(item => item.CanonicalId).HasColumnName("canonical_id");
            entity.Property(item => item.DisplayName).HasColumnName("display_name");
            entity.Property(item => item.Location).HasColumnName("location");
            entity.Property(item => item.DocsPath).HasColumnName("docs_path");
            entity.Property(item => item.Version).HasColumnName("version");
            entity.Property(item => item.Tag).HasColumnName("tag");
            entity.Property(item => item.Branch).HasColumnName("branch");
            entity.Property(item => item.Fingerprint).HasColumnName("fingerprint");
            entity.Property(item => item.LastBuiltAt).HasColumnName("last_built_at");
            entity.Property(item => item.LastCheckedAt).HasColumnName("last_checked_at");
        });

        modelBuilder.Entity<DocumentEntity>(entity =>
        {
            entity.ToTable("documents");
            entity.HasKey(item => item.DocumentId);
            entity.Property(item => item.DocumentId).HasColumnName("document_id");
            entity.Property(item => item.Title).HasColumnName("title");
            entity.Property(item => item.Path).HasColumnName("path");
            entity.Property(item => item.Description).HasColumnName("description");
            entity.Property(item => item.Language).HasColumnName("language");
            entity.Property(item => item.Content).HasColumnName("content");
        });

        modelBuilder.Entity<ChunkEntity>(entity =>
        {
            entity.ToTable("chunks");
            entity.HasKey(item => item.ChunkId);
            entity.Property(item => item.ChunkId).HasColumnName("chunk_id");
            entity.Property(item => item.DocumentId).HasColumnName("document_id");
            entity.Property(item => item.DocumentTitle).HasColumnName("document_title");
            entity.Property(item => item.SectionTitle).HasColumnName("section_title");
            entity.Property(item => item.Path).HasColumnName("path");
            entity.Property(item => item.Content).HasColumnName("content");
            entity.Property(item => item.TokenEstimate).HasColumnName("token_estimate");
            entity.Property(item => item.HasCode).HasColumnName("has_code");
            entity.Property(item => item.ContentHash).HasColumnName("content_hash");
            entity.Property(item => item.Sequence).HasColumnName("sequence");
            entity.Property(item => item.PreviousChunkId).HasColumnName("previous_chunk_id");
            entity.Property(item => item.NextChunkId).HasColumnName("next_chunk_id");
            entity.HasIndex(item => item.DocumentId);
            entity.HasIndex(item => item.ContentHash);
        });

        modelBuilder.Entity<WarningEntity>(entity =>
        {
            entity.ToTable("warnings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.Code).HasColumnName("code");
            entity.Property(item => item.Message).HasColumnName("message");
            entity.Property(item => item.SourcePath).HasColumnName("source_path");
        });
    }
}

internal sealed class ManifestEntity
{
    public required string PackageId { get; init; }
    public required string DisplayName { get; init; }
    public string? Version { get; init; }
    public SourceKind SourceKind { get; init; }
    public required string SourceLocation { get; init; }
    public required string SourceCanonicalId { get; init; }
    public string? SourceFingerprint { get; init; }
    public DateTimeOffset BuiltAt { get; init; }
    public required string BuilderVersion { get; init; }
    public int DocumentCount { get; init; }
    public int ChunkCount { get; init; }
    public int WarningCount { get; init; }
}

internal sealed class SourceMetadataEntity
{
    public required string PackageId { get; init; }
    public SourceKind SourceKind { get; init; }
    public required string CanonicalId { get; init; }
    public required string DisplayName { get; init; }
    public required string Location { get; init; }
    public string? DocsPath { get; init; }
    public string? Version { get; init; }
    public string? Tag { get; init; }
    public string? Branch { get; init; }
    public string? Fingerprint { get; init; }
    public DateTimeOffset? LastBuiltAt { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
}

internal sealed class DocumentEntity
{
    public required string DocumentId { get; init; }
    public required string Title { get; init; }
    public required string Path { get; init; }
    public required string Content { get; init; }
    public string? Description { get; init; }
    public string? Language { get; init; }
}

internal sealed class ChunkEntity
{
    public required string ChunkId { get; init; }
    public required string DocumentId { get; init; }
    public required string DocumentTitle { get; init; }
    public required string SectionTitle { get; init; }
    public required string Path { get; init; }
    public required string Content { get; init; }
    public int TokenEstimate { get; init; }
    public bool HasCode { get; init; }
    public required string ContentHash { get; init; }
    public int Sequence { get; init; }
    public string? PreviousChunkId { get; init; }
    public string? NextChunkId { get; init; }
}

internal sealed class WarningEntity
{
    public int Id { get; init; }
    public BuildWarningCode Code { get; init; }
    public required string Message { get; init; }
    public string? SourcePath { get; init; }
}
