using GroundKit.Core.Contracts;

namespace GroundKit.Core.Tests.Unit;

public sealed class PackageReferenceTests
{
    [Theory]
    [InlineData("react", "npm", "react", null)]
    [InlineData("npm/react", "npm", "react", null)]
    [InlineData("pip/django", "pip", "django", null)]
    [InlineData("react@19.1.0", "npm", "react", "19.1.0")]
    [InlineData("npm/react@19.1.0", "npm", "react", "19.1.0")]
    // A leading @ is a scope, not a registry.
    [InlineData("@angular/core", "npm", "@angular/core", null)]
    [InlineData("@angular/core@21.0.3", "npm", "@angular/core", "21.0.3")]
    // ...even when a registry is named explicitly.
    [InlineData("npm/@angular/core", "npm", "@angular/core", null)]
    [InlineData("npm/@angular/core@21.0.3", "npm", "@angular/core", "21.0.3")]
    // Non-numeric version labels are versions too.
    [InlineData("react@latest", "npm", "react", "latest")]
    [InlineData("npm/react@next", "npm", "react", "next")]
    public void Should_Parse_Package_Selectors(
        string selector,
        string registry,
        string name,
        string? version
    )
    {
        var reference = PackageReference.Parse(selector);

        reference.Registry.ShouldBe(registry);
        reference.Name.ShouldBe(name);
        reference.Version.ShouldBe(version);
    }

    [Theory]
    // A slash after the @ means the value is not a name@version pair, so the whole string stays the
    // reference rather than becoming the package "git" at version "github.com:org/repo.git".
    [InlineData("git@github.com:org/repo.git", "git@github.com:org", "repo.git")]
    [InlineData("https://example.com/a/b", "https:", "/example.com/a/b")]
    public void Should_Not_Treat_A_Slash_After_At_As_A_Version(
        string selector,
        string registry,
        string name
    )
    {
        var reference = PackageReference.Parse(selector);

        reference.Version.ShouldBeNull();
        reference.Registry.ShouldBe(registry);
        reference.Name.ShouldBe(name);
    }

    [Fact]
    public void Should_Treat_Selector_Without_Registry_As_Npm()
    {
        PackageReference.Parse("vite").Registry.ShouldBe(PackageReference.DefaultRegistry);
    }

    [Theory]
    [InlineData("react", "npm/react")]
    [InlineData("react@19.1.0", "npm/react@19.1.0")]
    [InlineData("@angular/core@21.0.3", "npm/@angular/core@21.0.3")]
    public void Should_Round_Trip_Through_ToString(string selector, string expected)
    {
        PackageReference.Parse(selector).ToString().ShouldBe(expected);
    }

    [Fact]
    public void Should_Tolerate_Blank_Selector()
    {
        var reference = PackageReference.Parse("  ");

        reference.Name.ShouldBeEmpty();
        reference.Version.ShouldBeNull();
        reference.Registry.ShouldBe(PackageReference.DefaultRegistry);
    }
}
