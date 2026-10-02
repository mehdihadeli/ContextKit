using GroundKit.Core.Contracts;

namespace GroundKit.Core.Abstractions;

public interface IDocumentPackageBuilder
{
    Task<BuildResult> BuildAsync(
        string input,
        string? docsPath = null,
        CancellationToken cancellationToken = default
    );

    Task<BuildResult> BuildAsync(
        string input,
        string? docsPath,
        CancellationToken cancellationToken,
        string? version,
        string? gitRef
    ) => BuildAsync(input, docsPath, cancellationToken);

    Task<BuildResult> BuildAsync(
        string input,
        string? docsPath,
        CancellationToken cancellationToken,
        string? version,
        string? gitRef,
        string? packageName
    ) => BuildAsync(input, docsPath, cancellationToken, version, gitRef);

    Task<BuildResult> BuildAsync(
        string input,
        string? docsPath,
        CancellationToken cancellationToken,
        string? version,
        string? gitRef,
        string? packageName,
        IReadOnlyList<string>? excludePaths
    ) => BuildAsync(input, docsPath, cancellationToken, version, gitRef, packageName);
}
