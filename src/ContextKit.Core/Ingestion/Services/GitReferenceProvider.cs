using System.Diagnostics;
using GroundKit.Core.Abstractions;

namespace GroundKit.Ingestion.Services;

public sealed class GitReferenceProvider : IGitReferenceProvider
{
    public static string? SelectLatestStableTag(IEnumerable<string> tags)
    {
        return tags.Select(tag => (Tag: tag, Version: TryParseStableVersion(tag)))
            .Where(candidate => candidate.Version is not null)
            .OrderByDescending(candidate => candidate.Version)
            .ThenByDescending(candidate => candidate.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Tag)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(
        string repositoryUrl,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("ls-remote");
        startInfo.ArgumentList.Add("--refs");
        startInfo.ArgumentList.Add("--tags");
        startInfo.ArgumentList.Add(repositoryUrl);

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git ls-remote process.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                return [];
            }

            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split('\t', 2).Last())
                .Where(reference => reference.StartsWith("refs/tags/", StringComparison.Ordinal))
                .Select(reference => reference["refs/tags/".Length..])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Where(tag => TryParseStableVersion(tag) is not null)
                .OrderByDescending(tag => TryParseStableVersion(tag))
                .ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return [];
        }
    }

    private static Version? TryParseStableVersion(string tag)
    {
        var value = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        var core = value.Split('+', 2)[0];
        if (core.Contains('-', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = core.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0))
        {
            return null;
        }

        if (parts.Any(part => part.Length > 1 && part[0] == '0'))
        {
            return null;
        }

        return
            int.TryParse(parts[0], out var major)
            && int.TryParse(parts[1], out var minor)
            && int.TryParse(parts[2], out var patch)
            ? new Version(major, minor, patch)
            : null;
    }
}
