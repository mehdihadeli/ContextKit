using GroundKit.Core.Contracts;

namespace GroundKit.Ingestion.Services;

public interface ISourceDetector
{
    DocumentationSource Detect(string input);
}

public sealed class SourceDetector : ISourceDetector
{
    private static readonly string[] GitHosts =
    [
        "github.com",
        "gitlab.com",
        "bitbucket.org",
        "codeberg.org",
    ];
    private static readonly string[] RawDocumentExtensions =
    [
        ".md",
        ".mdx",
        ".markdown",
        ".txt",
        ".html",
        ".htm",
    ];

    public DocumentationSource Detect(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        if (Directory.Exists(input))
        {
            var fullPath = Path.GetFullPath(input);
            var displayName = Path.GetFileName(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            );
            return new DocumentationSource(
                SourceKind.LocalDirectory,
                Canonicalize(displayName),
                displayName,
                fullPath
            );
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Unsupported source input: '{input}'.");
        }

        if (IsGitHubBlob(uri))
        {
            var blobDisplay = GetDisplayName(uri);
            return new DocumentationSource(
                SourceKind.RawPage,
                Canonicalize(blobDisplay),
                blobDisplay,
                input
            );
        }

        if (IsGitRepository(uri, input))
        {
            var repositoryUri = uri;
            string? gitRef = null;
            if (TryParseGitHubTreeUrl(uri, out var parsedRepositoryUri, out var parsedRef))
            {
                repositoryUri = parsedRepositoryUri;
                gitRef = parsedRef;
            }

            var repositoryDisplay = GetDisplayName(repositoryUri);
            return new DocumentationSource(
                SourceKind.GitRepository,
                Canonicalize(repositoryDisplay),
                repositoryDisplay,
                repositoryUri.ToString(),
                Tag: gitRef
            );
        }

        var display = GetDisplayName(uri);
        var canonicalId = Canonicalize(display);

        if (IsLlmsText(uri))
        {
            return new DocumentationSource(SourceKind.LlmsText, canonicalId, display, input);
        }

        return new DocumentationSource(SourceKind.RawPage, canonicalId, display, input);
    }

    private static bool IsGitRepository(Uri uri, string input)
    {
        if (input.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!GitHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (RawDocumentExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsLlmsText(uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2;
    }

    private static bool IsGitHubBlob(Uri uri) =>
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Take(3)
            .LastOrDefault()
            ?.Equals("blob", StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryParseGitHubTreeUrl(Uri uri, out Uri repositoryUri, out string? gitRef)
    {
        repositoryUri = uri;
        gitRef = null;

        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || !segments[2].Equals("tree", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        gitRef = Uri.UnescapeDataString(string.Join('/', segments.Skip(3)));
        repositoryUri = new Uri(
            $"{uri.Scheme}://{uri.Authority}/{segments[0]}/{segments[1]}",
            UriKind.Absolute
        );
        return !string.IsNullOrWhiteSpace(gitRef);
    }

    private static bool IsLlmsText(Uri uri)
    {
        return uri.AbsolutePath.EndsWith("/llms.txt", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith("/llms-full.txt", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath == "/";
    }

    private static string GetDisplayName(Uri uri)
    {
        var segment = uri
            .AbsolutePath.Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(segment))
        {
            return uri.Host;
        }

        var extension = Path.GetExtension(segment);
        return string.IsNullOrWhiteSpace(extension)
            ? segment
            : Path.GetFileNameWithoutExtension(segment);
    }

    private static string Canonicalize(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();

        return string.Join(
            '-',
            new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries)
        );
    }
}
