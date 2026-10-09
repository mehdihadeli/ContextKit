namespace GroundKit.Core.Abstractions;

public interface IPackageDownloadService
{
    Task<string> InstallAsync(
        string packageOrSource,
        string? version = null,
        CancellationToken cancellationToken = default
    );

    Task<string> InstallRegistryPackageAsync(
        string registry,
        string name,
        string? version = null,
        CancellationToken cancellationToken = default
    );
}
