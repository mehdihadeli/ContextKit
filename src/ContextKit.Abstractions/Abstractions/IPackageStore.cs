using GroundKit.Core.Contracts;

namespace GroundKit.Core.Abstractions;

public interface IPackageStore
{
    Task<string> SaveAsync(BuildResult buildResult, CancellationToken cancellationToken = default);

    Task<string> ImportAsync(string packageFilePath, CancellationToken cancellationToken = default);

    Task<string> ExportAsync(
        string packageId,
        string destinationPath,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<PackageSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<PackageSummary?> GetPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default
    );

    Task<int> RemoveAsync(string packageId, CancellationToken cancellationToken = default);

    Task<int> RemoveAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default
    );

    Task<DocumentationSource?> GetSourceAsync(
        string packageId,
        CancellationToken cancellationToken = default
    );

    Task<DocsQueryResponse> QueryAsync(
        DocsQueryRequest request,
        CancellationToken cancellationToken = default
    );
}
