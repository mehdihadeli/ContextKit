namespace GroundKit.Core.Contracts;

/// <summary>
/// Chooses the best available version for a requested one.
/// </summary>
/// <remarks>
/// A caller rarely knows the exact published version. <c>query react@18 "useEffect"</c> asks for a
/// major line, and a package that is installed but pinned at <c>18.2.0</c> should still be found
/// instead of falling through to the registry or reporting a miss. The rules below are deliberately
/// narrow, so a requested version can never resolve to an unrelated one:
/// <list type="number">
/// <item>Nothing asked for, <c>latest</c>, or <c>*</c> resolves to the <c>latest</c> tag, else to the highest version.</item>
/// <item>An exact match wins, ignoring a leading <c>v</c> and range prefixes such as <c>^</c>, <c>~</c> and <c>=</c>.</item>
/// <item>A request that covers a whole leading run of segments resolves to the highest such version, so
/// <c>19</c> finds <c>19.2.17</c> and <c>19.2</c> finds <c>19.2.17</c>. Segment boundaries are
/// respected, so <c>19</c> never matches <c>190.0.0</c>.</item>
/// <item>Anything else resolves to nothing, which tells the caller the request is genuinely absent.</item>
/// </list>
/// Every method returns the version exactly as the caller supplied it, never a rewritten copy, so the
/// result can be used directly to look an entry back up.
/// </remarks>
public static class PackageVersionResolver
{
    /// <summary>The tag that means "whatever the newest published version is".</summary>
    public const string LatestTag = "latest";

    /// <summary>
    /// Returns the available version that best satisfies <paramref name="requested"/>, or
    /// <see langword="null"/> when nothing is a defensible match.
    /// </summary>
    public static string? Resolve(string? requested, IEnumerable<string?> available)
    {
        ArgumentNullException.ThrowIfNull(available);

        var candidates = Candidates(available);
        if (candidates.Count == 0)
        {
            return null;
        }

        var wanted = Normalize(requested);
        if (wanted is null || wanted == "*" || wanted == LatestTag)
        {
            var latest = candidates.FirstOrDefault(candidate => IsLatest(candidate.Normalized));
            return latest.Normalized is null ? Newest(candidates) : latest.Original;
        }

        var exact = candidates
            .Where(candidate =>
                string.Equals(candidate.Normalized, wanted, StringComparison.OrdinalIgnoreCase)
            )
            .Select(candidate => candidate.Original)
            .FirstOrDefault();
        if (exact is not null)
        {
            return exact;
        }

        var matching = candidates
            .Where(candidate => IsLeadingSegmentRun(wanted, candidate.Normalized))
            .ToList();

        return matching.Count == 0 ? null : Newest(matching);
    }

    /// <summary>
    /// Orders versions newest first, with non-numeric tags such as <c>latest</c> and <c>next</c> last.
    /// Used to describe what does exist when a request cannot be satisfied.
    /// </summary>
    public static IReadOnlyList<string> Order(IEnumerable<string?> available)
    {
        ArgumentNullException.ThrowIfNull(available);

        return Candidates(available)
            .OrderByDescending(candidate => Parse(candidate.Normalized) is not null)
            .ThenByDescending(candidate => Parse(candidate.Normalized) ?? new Version(0, 0))
            .ThenByDescending(candidate => IsLatest(candidate.Normalized))
            .ThenByDescending(candidate => candidate.Normalized, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Original)
            .ToList();
    }

    private static List<(string Original, string Normalized)> Candidates(
        IEnumerable<string?> available
    ) =>
        available
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => version!)
            .Select(version => (Original: version, Normalized: Normalize(version)!))
            .Where(candidate => candidate.Normalized.Length > 0)
            .DistinctBy(candidate => candidate.Normalized, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsLatest(string version) =>
        string.Equals(version, LatestTag, StringComparison.OrdinalIgnoreCase);

    private static string Newest(IReadOnlyList<(string Original, string Normalized)> candidates)
    {
        var parsed = candidates.Where(candidate => Parse(candidate.Normalized) is not null).ToList();
        var pool = parsed.Count > 0 ? parsed : candidates;

        return pool
            .OrderByDescending(candidate => Parse(candidate.Normalized))
            .ThenByDescending(candidate => candidate.Normalized, StringComparer.OrdinalIgnoreCase)
            .First()
            .Original;
    }

    /// <summary>
    /// True when <paramref name="requested"/> covers the first whole segments of
    /// <paramref name="candidate"/>, so <c>19</c> covers <c>19.2.17</c> but not <c>190.0.0</c>.
    /// </summary>
    private static bool IsLeadingSegmentRun(string requested, string candidate) =>
        candidate.StartsWith(requested + ".", StringComparison.OrdinalIgnoreCase)
        && (requested.Contains('.') || IsNumeric(requested));

    private static bool IsNumeric(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit);

    private static Version? Parse(string version) =>
        Version.TryParse(version, out var parsed) ? parsed : null;

    /// <summary>
    /// Drops a leading <c>v</c> and the range prefixes people write out of npm habit, so
    /// <c>v19.2.17</c>, <c>^19</c> and <c>=19</c> all compare as <c>19</c>-something.
    /// </summary>
    private static string? Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var value = version.Trim().TrimStart('v', 'V').TrimStart('^', '~', '=', ' ').Trim();
        return value.Length == 0 ? null : value;
    }
}
