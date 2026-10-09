namespace GroundKit.Core.Contracts;

/// <summary>
/// A package selector exactly as a user or agent writes it: an optional registry, a package name, and
/// an optional version.
/// </summary>
/// <remarks>
/// Every entry point that accepts a selector parses it here, so <c>install</c>, <c>download-package</c>,
/// <c>query</c>, and <c>remove</c> cannot drift apart on questions such as whether a leading <c>@</c>
/// is a scope or a registry.
/// </remarks>
public readonly record struct PackageReference(string Registry, string Name, string? Version)
{
    /// <summary>The registry used when a selector does not name one.</summary>
    public const string DefaultRegistry = "npm";

    /// <summary>
    /// Parses <c>name</c>, <c>registry/name</c>, <c>name@version</c>, or <c>registry/name@version</c>.
    /// </summary>
    /// <remarks>
    /// Two shapes need care. A leading <c>@</c> marks a scoped package name, so <c>@angular/core</c>
    /// is the npm package <c>@angular/core</c> rather than the <c>@angular</c> registry. And a version
    /// separator is ignored when what follows it contains <c>/</c>, so a value such as
    /// <c>git@github.com:org/repo.git</c> is not read as the package <c>git</c> at version
    /// <c>github.com:org/repo.git</c>.
    /// </remarks>
    public static PackageReference Parse(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return new PackageReference(DefaultRegistry, string.Empty, null);
        }

        var value = selector.Trim();
        var separator = value.LastIndexOf('@');
        var hasVersion =
            separator > 0
            && separator < value.Length - 1
            && !value.AsSpan(separator + 1).Contains('/');
        var reference = hasVersion ? value[..separator] : value;
        var version = hasVersion ? value[(separator + 1)..] : null;

        var registrySeparator = reference.IndexOf('/');
        return reference.StartsWith('@') || registrySeparator <= 0
            ? new PackageReference(DefaultRegistry, reference, version)
            : new PackageReference(
                reference[..registrySeparator],
                reference[(registrySeparator + 1)..],
                version
            );
    }

    /// <summary>Renders the <c>registry/name@version</c> form, which <see cref="Parse"/> round-trips.</summary>
    public override string ToString() =>
        Version is null ? $"{Registry}/{Name}" : $"{Registry}/{Name}@{Version}";
}
