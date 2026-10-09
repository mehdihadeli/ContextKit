using GroundKit.Core.Contracts;

namespace GroundKit.Tests.Unit;

/// <summary>
/// Covers the mention detection that lets <c>query "what are components in angular?"</c> resolve the
/// package from the question instead of demanding an explicit id.
/// </summary>
public sealed class PackageMentionDetectorTests
{
    /// <summary>The curated catalog names, which is what a real call site passes in.</summary>
    private static readonly string[] Candidates =
    [
        "angular",
        "react",
        "next",
        "vue",
        "tailwindcss",
        "nestjs",
        "typescript",
        "axios",
        "eslint",
        "playwright",
    ];

    [Theory]
    // Plain names, any case.
    [InlineData("what are components in angular?", "angular")]
    [InlineData("WHAT ARE COMPONENTS IN ANGULAR?", "angular")]
    [InlineData("how do i use hooks in react", "react")]
    [InlineData("composition api in vue", "vue")]
    [InlineData("utility classes in tailwind", "tailwindcss")]
    // Dotted and hyphenated spellings collapse to the same words.
    [InlineData("how does Next.js route?", "next")]
    [InlineData("vue.js reactivity explained", "vue")]
    [InlineData("tailwind-css purge config", "tailwindcss")]
    [InlineData("modules in nest.js", "nestjs")]
    // Aliases.
    [InlineData("angularjs digest cycle", "angular")]
    [InlineData("getting started with nest", "nestjs")]
    [InlineData("type script generics", "typescript")]
    [InlineData("es lint rules", "eslint")]
    // A version next to the name does not break the match.
    [InlineData("what changed in angular 19?", "angular")]
    [InlineData("react@18 hooks", "react")]
    public void Should_Detect_Package_From_Question(string question, string expected) =>
        PackageMentionDetector.Detect(question, Candidates).ShouldBe(expected);

    [Theory]
    // Typos are tolerated on single words of five characters or more.
    [InlineData("what are components in angualr?", "angular")]
    [InlineData("how do i use hooks in reactt?", "react")]
    [InlineData("typescrip generics", "typescript")]
    [InlineData("taiwind css purge", "tailwindcss")]
    public void Should_Detect_Package_Despite_A_Typo(string question, string expected) =>
        PackageMentionDetector.Detect(question, Candidates).ShouldBe(expected);

    [Theory]
    // Every word of a multi-word spelling may appear in any order.
    [InlineData("router for vue", "vue-router")]
    [InlineData("vue router guards", "vue-router")]
    [InlineData("css tailwind purge", "tailwindcss")]
    [InlineData("js nest interceptors", "nestjs")]
    public void Should_Detect_MultiWord_Package_Regardless_Of_Word_Order(
        string question,
        string expected
    ) =>
        PackageMentionDetector.Detect(question, ["vue-router", "tailwindcss", "nestjs"])
            .ShouldBe(expected);

    [Fact]
    public void Should_Prefer_An_Exact_Mention_Over_An_All_Words_Mention() =>
        // "vue" is named outright, so it beats the package that merely shares both words.
        PackageMentionDetector.Detect("router for vue", ["vue-router", "vue"])
            .ShouldBe("vue");

    [Fact]
    public void Should_Not_Tolerate_A_Typo_Shorter_Than_Five_Characters() =>
        // "nest" is one character from "next", so short words are matched exactly or not at all.
        PackageMentionDetector.Detect("how do i use nest", ["next"]).ShouldBeNull();

    [Fact]
    public void Should_Prefer_The_Longest_Exact_Mention() =>
        // "react" (5 letters) beats the bare word "next" (4), even though both appear.
        PackageMentionDetector.Detect("what is next after mounting in react", Candidates)
            .ShouldBe("react");

    [Fact]
    public void Should_Prefer_An_Exact_Mention_Over_A_Typo() =>
        PackageMentionDetector.Detect("next steps in angualr", Candidates).ShouldBe("next");

    [Fact]
    public void Should_Detect_A_Scoped_Package_By_Its_Scope() =>
        PackageMentionDetector.Detect("how to use components in angular", ["@angular/core"])
            .ShouldBe("@angular/core");

    [Fact]
    public void Should_Keep_The_First_Candidate_When_Scores_Tie() =>
        // Callers pass installed packages first, so an installed copy must win a tie.
        PackageMentionDetector
            .Detect("vue composition api", ["vue", "vue-router"])
            .ShouldBe("vue");

    [Theory]
    [InlineData("how creative is the actor")]
    [InlineData("how do i configure the flux capacitor")]
    [InlineData("what is a promise")]
    public void Should_Return_Null_When_Nothing_Matches(string question) =>
        PackageMentionDetector.Detect(question, Candidates).ShouldBeNull();

    [Fact]
    public void Should_Not_Confuse_Preact_With_React() =>
        // One insertion away, but the first letter differs, so it is a different library.
        PackageMentionDetector.Detect("preact signals", ["react"]).ShouldBeNull();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Should_Ignore_An_Empty_Question(string question) =>
        PackageMentionDetector.Detect(question, Candidates).ShouldBeNull();

    [Fact]
    public void Should_Return_Null_When_There_Are_No_Candidates() =>
        PackageMentionDetector.Detect("angular components", []).ShouldBeNull();

    [Fact]
    public void Should_Rank_Every_Package_A_Question_Mentions() =>
        // A question can span libraries, and a caller that can query several of them needs all the
        // named packages, not just the winner.
        PackageMentionDetector
            .MatchAll("how do i use typescript types in vue?", Candidates)
            .Select(match => match.Name)
            .ShouldBe(["typescript", "vue"]);

    [Fact]
    public void Should_Sort_Matches_By_Specificity() =>
        // An exact mention outranks a near miss, even though both are reported.
        PackageMentionDetector
            .MatchAll("typescript decorators in angualr", Candidates)
            .Select(match => match.Name)
            .ShouldBe(["typescript", "angular"]);

    [Fact]
    public void Should_Keep_The_Candidates_Order_When_Scores_Tie() =>
        // "axios" and "react" are both five letters, so their exact mentions score the same and the
        // caller's order decides — which is how an installed package keeps its edge over a curated one.
        PackageMentionDetector
            .MatchAll("axios interceptor in react", Candidates)
            .Select(match => match.Name)
            .ShouldBe(["react", "axios"]);

    [Fact]
    public void Should_Limit_The_Number_Of_Matches() =>
        PackageMentionDetector
            .MatchAll("react vue angular next", Candidates, maxResults: 2)
            .Count.ShouldBe(2);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("what is a promise")]
    public void Should_Return_No_Matches_When_Nothing_Is_Named(string question) =>
        PackageMentionDetector.MatchAll(question, Candidates).ShouldBeEmpty();

    [Fact]
    public void Should_Return_No_Matches_For_A_Non_Positive_Limit() =>
        PackageMentionDetector.MatchAll("angular components", Candidates, maxResults: 0)
            .ShouldBeEmpty();

    [Fact]
    public void Should_Agree_With_Detect_On_The_Winner() =>
        PackageMentionDetector
            .MatchAll("how do i use hooks in react", Candidates)
            .Select(match => match.Name)
            .First()
            .ShouldBe(PackageMentionDetector.Detect("how do i use hooks in react", Candidates));
}
