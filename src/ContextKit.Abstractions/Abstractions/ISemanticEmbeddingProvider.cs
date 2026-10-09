namespace GroundKit.Semantic;

public interface ISemanticEmbeddingProvider
{
    Task<float[][]> EmbedAsync(
        SemanticModelProfile model,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default
    );
}