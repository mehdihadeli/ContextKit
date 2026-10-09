using GroundKit.Core.Contracts;

namespace GroundKit.Core.Abstractions;

public interface IContextRegistryClient
{
    Task<IReadOnlyList<RegistryPackage>> SearchAsync(
        string registry,
        string name,
        string? version = null,
        CancellationToken cancellationToken = default
    );

    Task<RegistryPackageMetadata> GetMetadataAsync(
        string registry,
        string name,
        string version,
        CancellationToken cancellationToken = default
    );

    Task DownloadAsync(
        string registry,
        string name,
        string version,
        string destinationPath,
        CancellationToken cancellationToken = default
    );
}
