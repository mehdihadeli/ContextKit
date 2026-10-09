using GroundKit.Core.Contracts;

namespace GroundKit.Tests.Unit;

/// <summary>
/// Covers the fusion that lets a single question span several documentation packages. BM25 scores
/// are only meaningful inside one package, so the merge orders on rank instead.
/// </summary>
public sealed class RankFusionTests
{
    [Fact]
    public void Should_Interleave_By_Rank_Not_By_Score()
    {
        // The second package's best hit has a much lower raw score, but rank 1 from each list must
        // still sit together at the top: comparing scores across packages is exactly what fusion
        // avoids.
        var first = Response("react", Hit("react-top", score: 9.5), Hit("react-next", score: 8.0));
        var second = Response("axios", Hit("axios-top", score: 0.4));

        var fused = RankFusion.Fuse([first, second], maxTokens: 10_000);

        fused.Select(hit => hit.Hit.DocumentTitle).ShouldBe(["react-top", "axios-top", "react-next"]);
    }

    [Fact]
    public void Should_Tag_Every_Hit_With_Its_Package()
    {
        var fused = RankFusion.Fuse(
            [Response("react", Hit("hooks")), Response("axios", Hit("interceptors"))],
            maxTokens: 10_000
        );

        fused.Select(hit => hit.PackageId).ShouldBe(["react", "axios"]);
        fused.ShouldAllBe(hit => hit.Version == "1.0.0");
    }

    [Fact]
    public void Should_Keep_The_Score_A_Hit_Earned_In_Its_Own_Package()
    {
        var fused = RankFusion.Fuse(
            [Response("react", Hit("hooks", score: 6.25))],
            maxTokens: 10_000
        );

        fused.Single().Hit.Score.ShouldBe(6.25);
    }

    [Fact]
    public void Should_Stop_At_The_Token_Budget()
    {
        var fused = RankFusion.Fuse(
            [Response("react", Hit("a", tokens: 40), Hit("b", tokens: 40))],
            maxTokens: 50
        );

        // The second hit would push the total to 80, so only the first is returned.
        fused.Select(hit => hit.Hit.DocumentTitle).ShouldBe(["a"]);
    }

    [Fact]
    public void Should_Keep_An_Oversized_Best_Hit_Without_Truncating_It()
    {
        var fused = RankFusion.Fuse(
            [Response("react", Hit("large", tokens: 100), Hit("small", tokens: 10))],
            maxTokens: 20
        );

        var hit = fused.ShouldHaveSingleItem();
        hit.Hit.DocumentTitle.ShouldBe("large");
        hit.Hit.TokenEstimate.ShouldBe(100);
        hit.Hit.Content.ShouldBe("content");
    }

    [Fact]
    public void Should_Skip_Hits_That_Do_Not_Fit_And_Keep_Later_Fitting_Hits()
    {
        var fused = RankFusion.Fuse(
            [Response("react", Hit("first", tokens: 10), Hit("large", tokens: 100), Hit("last", tokens: 10))],
            maxTokens: 20
        );

        fused.Select(hit => hit.Hit.DocumentTitle).ShouldBe(["first", "last"]);
        fused.Sum(hit => hit.Hit.TokenEstimate).ShouldBe(20);
    }

    [Fact]
    public void Should_Return_Nothing_Without_Responses() =>
        RankFusion.Fuse([], maxTokens: 1_000).ShouldBeEmpty();

    [Fact]
    public void Should_Return_Nothing_For_A_Non_Positive_Budget()
    {
        var fused = RankFusion.Fuse([Response("react", Hit("hooks"))], maxTokens: 0);
        fused.ShouldBeEmpty();
    }

    [Fact]
    public void Should_Ignore_Responses_That_Returned_No_Hits() =>
        RankFusion
            .Fuse([Response("react"), Response("axios", Hit("interceptors"))], maxTokens: 1_000)
            .Select(hit => hit.PackageId)
            .ShouldBe(["axios"]);

    private static DocsQueryHit Hit(string title, int tokens = 10, double score = 1.0) =>
        new(title, "Section", "content", tokens, false, score);

    private static DocsQueryResponse Response(string packageId, params DocsQueryHit[] hits) =>
        new(packageId, "1.0.0", hits, hits.Sum(hit => hit.TokenEstimate));
}
