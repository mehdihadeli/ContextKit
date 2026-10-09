namespace GroundKit.Core.Abstractions;

public interface IGitReferenceProvider
{
    Task<IReadOnlyList<string>> GetTagsAsync(
        string repositoryUrl,
        CancellationToken cancellationToken = default
    );
}
