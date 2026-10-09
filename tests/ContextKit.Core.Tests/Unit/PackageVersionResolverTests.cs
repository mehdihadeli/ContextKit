using GroundKit.Core.Contracts;

namespace GroundKit.Tests.Unit;

/// <summary>
/// Covers the version matching that lets <c>query react@18</c> find <c>18.2.0</c> without the caller
/// knowing the exact patch version.
/// </summary>
public sealed class PackageVersionResolverTests
{
    private static readonly string[] AngularVersions = ["21.0.3", "20.3.15", "19.2.17"];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("LATEST")]
    [InlineData("*")]
    public void Should_Prefer_The_Latest_Tag_When_No_Version_Is_Asked_For(string? requested) =>
        PackageVersionResolver
            .Resolve(requested, ["19.2.17", "latest", "20.3.15"])
            .ShouldBe("latest");

    [Theory]
    [InlineData(null)]
    [InlineData("latest")]
    public void Should_Fall_Back_To_The_Highest_Version_Without_A_Latest_Tag(string? requested) =>
        PackageVersionResolver.Resolve(requested, AngularVersions).ShouldBe("21.0.3");

    [Theory]
    [InlineData("21.0.3")]
    [InlineData("v21.0.3")]
    [InlineData("=21.0.3")]
    [InlineData("^21.0.3")]
    [InlineData("~21.0.3")]
    public void Should_Match_An_Exact_Version_Whatever_Decoration_Is_Used(string requested) =>
        PackageVersionResolver.Resolve(requested, AngularVersions).ShouldBe("21.0.3");

    [Theory]
    [InlineData("19", "19.2.17")]
    [InlineData("20", "20.3.15")]
    [InlineData("21", "21.0.3")]
    [InlineData("^19", "19.2.17")]
    [InlineData("v19", "19.2.17")]
    public void Should_Resolve_A_Major_Line_To_Its_Highest_Version(string requested, string expected) =>
        PackageVersionResolver.Resolve(requested, AngularVersions).ShouldBe(expected);

    [Theory]
    [InlineData("19.2", "19.2.17")]
    [InlineData("19.2.0", null)]
    public void Should_Resolve_A_Partial_Minor_Only_When_A_Segment_Run_Matches(
        string requested,
        string? expected
    ) => PackageVersionResolver.Resolve(requested, ["19.2.17"]).ShouldBe(expected);

    [Fact]
    public void Should_Not_Match_A_Different_Major_Line_That_Merely_Starts_With_The_Same_Digits() =>
        PackageVersionResolver.Resolve("19", ["190.0.0"]).ShouldBeNull();

    [Fact]
    public void Should_Return_Nothing_For_An_Unpublished_Version() =>
        PackageVersionResolver.Resolve("18", AngularVersions).ShouldBeNull();

    [Fact]
    public void Should_Return_Nothing_When_No_Versions_Are_Available() =>
        PackageVersionResolver.Resolve("19", []).ShouldBeNull();

    [Fact]
    public void Should_Ignore_Named_Tags_When_Matching_A_Numeric_Request() =>
        PackageVersionResolver.Resolve("19", ["latest", "next", "19.2.17"]).ShouldBe("19.2.17");

    [Fact]
    public void Should_Order_Versions_Newest_First_With_Tags_Last() =>
        PackageVersionResolver
            .Order(["19.2.17", "latest", "21.0.3", "next", "20.3.15"])
            .ShouldBe(["21.0.3", "20.3.15", "19.2.17", "latest", "next"]);

    [Fact]
    public void Should_Preserve_The_Available_Spelling_When_Matching() =>
        PackageVersionResolver.Resolve("v19.2.17", ["V19.2.17"]).ShouldBe("V19.2.17");
}
