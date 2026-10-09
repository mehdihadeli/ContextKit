using System.Text;

namespace GroundKit.Core.Contracts;

/// <summary>
/// One candidate a question mentions, with the detector's raw specificity score.
/// </summary>
/// <remarks>
/// The score is a tiered value (an exact mention scores in the 30 000s, an all-words match in the
/// 20 000s, a near miss in the 10 000s) and is only meaningful for ordering candidates of the same
/// question. It is not a probability and must not be compared across questions.
/// </remarks>
public sealed record PackageMention(string Name, int Score);

/// <summary>
/// Finds which known package a free-text documentation question is about, so a query such as
/// "what are components in angular?" can be answered without naming the package explicitly.
/// </summary>
/// <remarks>
/// Detection is local and deterministic: it matches the question against a caller supplied set of
/// candidate names (installed packages plus the curated catalog) and their common spellings. It
/// never touches the network, which keeps <c>query</c> fast and usable offline and leaves the
/// registry only to resolve and download the package that was already detected.
/// </remarks>
public static class PackageMentionDetector
{
    /// <summary>Extra spellings that should resolve to a canonical package name.</summary>
    /// <remarks>
    /// Only names people genuinely write differently are listed. A package whose id already reads
    /// like the word they would use needs no entry, and every entry is matched on word boundaries,
    /// so an alias can never fire inside a longer word.
    /// </remarks>
    private static readonly Dictionary<string, string[]> Aliases = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["angular"] = ["angularjs", "angular.js", "angular js", "angular 2"],
        ["react"] = ["reactjs", "react.js", "react js"],
        ["vue"] = ["vuejs", "vue.js", "vue js"],
        ["next"] = ["nextjs", "next.js", "next js"],
        ["nestjs"] = ["nest", "nest.js", "nest js"],
        ["typescript"] = ["type script"],
        ["tailwindcss"] = ["tailwind", "tailwind css"],
        ["graphql"] = ["graph ql"],
        ["express"] = ["expressjs", "express.js"],
        ["eslint"] = ["es lint"],
        ["playwright"] = ["play wright"],
    };

    /// <summary>
    /// The shortest spelling that may be matched fuzzily. Below this, a one-character difference
    /// links unrelated words often enough to be noise rather than intelligence.
    /// </summary>
    private const int MinimumFuzzyLength = 5;

    /// <summary>
    /// Returns the candidate the question mentions, or <see langword="null"/> when none is found.
    /// </summary>
    /// <remarks>
    /// Candidates compete on a score, so the most specific mention wins:
    /// <list type="number">
    /// <item>An exact mention of a name or alias, longest spelling first, so
    /// "angular" beats "next" in "next steps in angular".</item>
    /// <item>Every word of a multi-word name present, in any order, so "router for vue" finds
    /// <c>vue-router</c>.</item>
    /// <item>A near miss on a single word, so a typo such as "angualr" still resolves. The first
    /// letter must match and the word must be at least five characters, which stops
    /// "preact" from resolving to "react".</item>
    /// </list>
    /// An exact mention always outranks a near miss. Ties keep the earlier candidate, which lets
    /// callers rank installed packages ahead of merely curated ones.
    /// </remarks>
    public static string? Detect(string question, IEnumerable<string> candidates)
    {
        var matches = MatchAll(question, candidates, maxResults: 1);
        return matches.Count == 0 ? null : matches[0].Name;
    }

    /// <summary>
    /// Returns the candidates the question mentions, most specific first, up to
    /// <paramref name="maxResults"/>.
    /// </summary>
    /// <remarks>
    /// A single question can legitimately span more than one library ("stream an OpenAI response
    /// from a Next.js route names two), so callers that can query several packages need the whole
    /// ranking rather than just the winner. Ordering, tiering, and tie-breaking are identical to
    /// <see cref="Detect"/>; because the sort is stable and the candidate sequence is preserved,
    /// an equally specific tie still keeps the earlier candidate — which is what lets a caller rank
    /// installed packages ahead of merely curated ones.
    /// </remarks>
    public static IReadOnlyList<PackageMention> MatchAll(
        string question,
        IEnumerable<string> candidates,
        int maxResults = 4
    )
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (string.IsNullOrWhiteSpace(question) || maxResults <= 0)
        {
            return [];
        }

        var haystack = Normalize(question);
        if (haystack.Length == 0)
        {
            return [];
        }

        var questionTokens = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ranked = new List<PackageMention>();

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bestScore = 0;
            foreach (var phrase in Phrases(candidate))
            {
                var needle = Normalize(phrase);
                if (needle.Length == 0 || needle.Contains('@'))
                {
                    continue;
                }

                bestScore = Math.Max(bestScore, Score(needle, haystack, questionTokens));
            }

            if (bestScore > 0)
            {
                ranked.Add(new PackageMention(candidate, bestScore));
            }
        }

        return ranked.OrderByDescending(match => match.Score).Take(maxResults).ToArray();
    }

    /// <summary>
    /// Scores one spelling of one candidate. Higher is more specific; zero means no match.
    /// </summary>
    private static int Score(string needle, string haystack, string[] questionTokens)
    {
        // Tier dominates, so an exact mention is never beaten by a longer fuzzy one.
        const int ExactTier = 3;
        const int AllWordsTier = 2;
        const int NearMissTier = 1;

        var needleTokens = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (ContainsWholePhrase(haystack, needle))
        {
            return (ExactTier * 10_000) + (needleTokens.Length * 100) + needle.Length;
        }

        if (
            needleTokens.Length > 1
            && needleTokens.All(token => questionTokens.Contains(token, StringComparer.Ordinal))
        )
        {
            return (AllWordsTier * 10_000) + (needleTokens.Length * 100) + needle.Length;
        }

        if (
            needleTokens.Length == 1
            && needle.Length >= MinimumFuzzyLength
            && questionTokens.Any(token => IsNearMiss(token, needle))
        )
        {
            return (NearMissTier * 10_000) + needle.Length;
        }

        return 0;
    }

    /// <summary>
    /// Every spelling worth matching for a candidate: the id itself, its aliases, and — for a
    /// scoped id such as <c>@angular/core</c> — the scope, which is the part a question names.
    /// </summary>
    private static IEnumerable<string> Phrases(string candidate)
    {
        yield return candidate;

        var scopeSeparator = candidate.IndexOf('/');
        if (candidate.StartsWith('@') && scopeSeparator > 1)
        {
            yield return candidate[1..scopeSeparator];
        }

        if (Aliases.TryGetValue(candidate, out var aliases))
        {
            foreach (var alias in aliases)
            {
                yield return alias;
            }
        }
    }

    private static bool ContainsWholePhrase(string haystack, string needle) =>
        $" {haystack} ".Contains($" {needle} ", StringComparison.Ordinal);

    /// <summary>
    /// True when two words differ by at most one insert, delete, substitute, or adjacent
    /// transposition, and begin with the same letter.
    /// </summary>
    /// <remarks>
    /// The shared first letter is what makes this safe: without it "preact" is one insertion away
    /// from "react" and would be detected as the wrong library.
    /// </remarks>
    private static bool IsNearMiss(string word, string expected)
    {
        if (word.Length == 0 || word[0] != expected[0])
        {
            return false;
        }

        if (Math.Abs(word.Length - expected.Length) > 1 || word.Length < MinimumFuzzyLength)
        {
            return false;
        }

        return OptimalStringAlignmentWithinOne(word, expected);
    }

    /// <summary>
    /// Levenshtein distance with adjacent transpositions, cut off at one edit.
    /// </summary>
    private static bool OptimalStringAlignmentWithinOne(string left, string right)
    {
        var previousPrevious = new int[right.Length + 1];
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            var rowMinimum = current[0];

            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                var value = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + cost
                );

                if (
                    row > 1
                    && column > 1
                    && left[row - 1] == right[column - 2]
                    && left[row - 2] == right[column - 1]
                )
                {
                    value = Math.Min(value, previousPrevious[column - 2] + 1);
                }

                current[column] = value;
                rowMinimum = Math.Min(rowMinimum, value);
            }

            // Every later row can only grow, so a row already past the limit can stop the scan.
            if (rowMinimum > 1)
            {
                return false;
            }

            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }

        return previous[right.Length] <= 1;
    }

    /// <summary>
    /// Lowercases and reduces every run of non-alphanumeric characters to a single space, so
    /// <c>Next.js</c>, <c>next js</c>, and <c>NEXT-JS</c> all compare equal while still matching on
    /// word boundaries (a naive substring test would find "act" inside "react").
    /// </summary>
    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                pendingSpace = false;
            }
            else if (!pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
                pendingSpace = true;
            }
        }

        while (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }

        return builder.ToString();
    }
}
