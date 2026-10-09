namespace GroundKit.Core.Contracts;

/// <summary>
/// One hit from a merged, multi-package search, tagged with the package it came from.
/// </summary>
public sealed record FusedDocsHit(string PackageId, string? Version, DocsQueryHit Hit);

/// <summary>
/// Merges the ranked hits of several package queries into a single list.
/// </summary>
/// <remarks>
/// BM25 scores are only meaningful inside the corpus that produced them, so two packages cannot be
/// compared by score without calibrating one against the other. Reciprocal rank fusion sidesteps the
/// problem by combining <em>positions</em> instead: a hit earns <c>1 / (k + rank)</c> from the list it
/// appeared in, and the sums decide the merged order. Each hit keeps its original score, which is the
/// number a caller can still compare against other sections of the same package.
/// </remarks>
public static class RankFusion
{
    /// <summary>
    /// The rank damping constant from the original reciprocal rank fusion paper. It keeps the top of
    /// each list from dominating the merge when one package returns many more hits than another.
    /// </summary>
    public const double DefaultRankConstant = 60d;

    /// <summary>
    /// Orders the hits of every response together and trims the result to <paramref name="maxTokens"/>.
    /// </summary>
    /// <remarks>
    /// The best hit is kept whole even when it exceeds a positive budget. Later hits that do not fit
    /// are skipped, matching single-package retrieval without truncating examples.
    /// </remarks>
    public static IReadOnlyList<FusedDocsHit> Fuse(
        IReadOnlyList<DocsQueryResponse> responses,
        int maxTokens,
        double rankConstant = DefaultRankConstant
    )
    {
        ArgumentNullException.ThrowIfNull(responses);

        if (responses.Count == 0 || maxTokens <= 0)
        {
            return [];
        }

        var ranked = new List<(FusedDocsHit Hit, double Fused)>();
        foreach (var response in responses)
        {
            for (var index = 0; index < response.Hits.Count; index++)
            {
                ranked.Add((
                    new FusedDocsHit(response.PackageId, response.Version, response.Hits[index]),
                    1d / (rankConstant + index + 1)
                ));
            }
        }

        var selected = new List<FusedDocsHit>();
        var total = 0;
        foreach (var item in ranked.OrderByDescending(entry => entry.Fused))
        {
            if (selected.Count > 0 && (long)total + item.Hit.Hit.TokenEstimate > maxTokens)
            {
                continue;
            }

            selected.Add(item.Hit);
            total += item.Hit.Hit.TokenEstimate;
        }

        return selected;
    }
}
