using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GroundKit.Mcp;

public sealed class GroundKitToolResponseAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GroundKitToolResponseAgent(ILoggerFactory loggerFactory, IServiceProvider services)
    {
    }

    public Task<string> ComposeAsync(
        string toolName,
        object payload,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var text = GroundKitToolResponseFormatter.Format(toolName, data);
        return Task.FromResult(string.IsNullOrWhiteSpace(text) ? data.GetRawText() : text);
    }
}

internal static class GroundKitToolResponseFormatter
{
    public static string Format(string toolName, JsonElement payload) =>
        toolName switch
        {
            "resolve-source" => FormatResolveSource(payload),
            "query-docs" => FormatQueryDocs(payload),
            "find_libraries" => FormatFindLibraries(payload),
            "search-docs" => FormatSearchDocs(payload),
            "library_catalog" => FormatLibraryCatalog(payload),
            "search_packages" => FormatSearchPackages(payload),
            "download_package" => FormatDownloadPackage(payload),
            _ => payload.GetRawText(),
        };

    private static string FormatLibraryCatalog(JsonElement payload)
    {
        if (
            !payload.TryGetProperty("libraries", out var libraries)
            || libraries.GetArrayLength() == 0
        )
        {
            return "No catalog libraries matched.";
        }

        var builder = new StringBuilder("Starter documentation libraries:\n");
        foreach (var library in libraries.EnumerateArray())
        {
            builder
                .Append("- ")
                .Append(library.GetProperty("name").GetString())
                .Append(": ")
                .Append(library.GetProperty("description").GetString())
                .Append(". Build from ")
                .Append(library.GetProperty("repository").GetString())
                .AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatSearchPackages(JsonElement payload)
    {
        if (!payload.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            return "No registry packages matched.";
        }

        var builder = new StringBuilder("Registry packages:\n");
        foreach (var result in results.EnumerateArray())
        {
            builder
                .Append("- ")
                .Append(result.GetProperty("name").GetString())
                .Append('@')
                .Append(result.GetProperty("version").GetString());
            if (
                result.TryGetProperty("description", out var description)
                && description.ValueKind != JsonValueKind.Null
            )
            {
                builder.Append(": ").Append(description.GetString());
            }
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatDownloadPackage(JsonElement payload)
    {
        return $"Installed {payload.GetProperty("name").GetString()}@{payload.GetProperty("version").GetString()} at {payload.GetProperty("installedPath").GetString()}.";
    }

    private static string FormatResolveSource(JsonElement payload)
    {
        if (!payload.TryGetProperty("packages", out var packages) || packages.GetArrayLength() == 0)
        {
            return "No installed package matched the query.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Resolved {packages.GetArrayLength()} package match(es):");

        var rank = 1;
        foreach (var package in packages.EnumerateArray())
        {
            var packageId = package.GetProperty("packageId").GetString() ?? "unknown";
            var displayName = package.GetProperty("displayName").GetString() ?? packageId;
            var version = package.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : null;
            var documentCount = package.GetProperty("documentCount").GetInt32();
            var chunkCount = package.GetProperty("chunkCount").GetInt32();
            var score = package.GetProperty("score").GetDouble();

            builder.Append(rank++).Append(". ").Append(packageId).Append(" - ").Append(displayName);

            if (!string.IsNullOrWhiteSpace(version))
            {
                builder.Append(" (v").Append(version).Append(')');
            }

            builder
                .Append(". docs=")
                .Append(documentCount)
                .Append(", chunks=")
                .Append(chunkCount)
                .Append(", score=")
                .Append(score.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                .AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatQueryDocs(JsonElement payload)
    {
        var packageId = payload.GetProperty("packageId").GetString() ?? "unknown";
        var version = payload.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString()
            : null;
        var totalTokens = payload.TryGetProperty("totalTokens", out var tokensElement)
            ? tokensElement.GetInt32()
            : 0;

        if (!payload.TryGetProperty("hits", out var hits) || hits.GetArrayLength() == 0)
        {
            return $"No matching documentation found in {packageId}.";
        }

        var builder = new StringBuilder();
        builder.Append("Package ").Append(packageId);

        if (!string.IsNullOrWhiteSpace(version))
        {
            builder.Append(" (v").Append(version).Append(')');
        }

        builder
            .Append(" returned ")
            .Append(hits.GetArrayLength())
            .Append(" hit(s) within ")
            .Append(totalTokens)
            .AppendLine(" tokens:");

        foreach (var hit in hits.EnumerateArray())
        {
            var title = hit.GetProperty("documentTitle").GetString() ?? "Untitled";
            var section = hit.GetProperty("sectionTitle").GetString() ?? "Root";
            var content = hit.GetProperty("content").GetString() ?? string.Empty;
            var score = hit.GetProperty("score").GetDouble();
            var tokenEstimate = hit.GetProperty("tokenEstimate").GetInt32();
            var hasCode = hit.GetProperty("hasCode").GetBoolean();

            builder
                .Append("- ")
                .Append(title)
                .Append(" / ")
                .Append(section)
                .Append(". score=")
                .Append(score.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                .Append(", tokens=")
                .Append(tokenEstimate)
                .Append(", code=")
                .Append(hasCode ? "yes" : "no")
                .AppendLine();
            builder.Append("  ").AppendLine(Truncate(CompactWhitespace(content), 220));
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatFindLibraries(JsonElement payload)
    {
        if (!payload.TryGetProperty("libraries", out var libraries) || libraries.GetArrayLength() == 0)
        {
            return "No known documentation library matched the question.";
        }

        var builder = new StringBuilder("Candidate documentation libraries:\n");
        var rank = 1;
        foreach (var library in libraries.EnumerateArray())
        {
            var libraryId = library.GetProperty("libraryId").GetString() ?? "unknown";
            var installed = library.TryGetProperty("installed", out var installedElement)
                && installedElement.GetBoolean();
            var description = library.TryGetProperty("description", out var descriptionElement)
                && descriptionElement.ValueKind != JsonValueKind.Null
                ? descriptionElement.GetString()
                : null;

            builder
                .Append(rank++)
                .Append(". ")
                .Append(libraryId)
                .Append(installed ? " (installed)" : " (not installed)");
            if (!string.IsNullOrWhiteSpace(description))
            {
                builder.Append(": ").Append(description);
            }

            builder.Append(". Query it with query-docs and this libraryId.").AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatSearchDocs(JsonElement payload)
    {
        var question = payload.TryGetProperty("question", out var questionElement)
            ? questionElement.GetString()
            : null;
        var totalTokens = payload.TryGetProperty("totalTokens", out var tokensElement)
            ? tokensElement.GetInt32()
            : 0;

        if (!payload.TryGetProperty("hits", out var hits) || hits.GetArrayLength() == 0)
        {
            return string.IsNullOrWhiteSpace(question)
                ? "No matching documentation found in any library."
                : $"No matching documentation found for '{question}'.";
        }

        var builder = new StringBuilder();
        builder
            .Append("Answered from ")
            .Append(hits.GetArrayLength())
            .Append(" hit(s) within ")
            .Append(totalTokens)
            .AppendLine(" tokens:");

        foreach (var hit in hits.EnumerateArray())
        {
            var packageId = hit.GetProperty("packageId").GetString() ?? "unknown";
            var title = hit.GetProperty("documentTitle").GetString() ?? "Untitled";
            var section = hit.GetProperty("sectionTitle").GetString() ?? "Root";
            var content = hit.GetProperty("content").GetString() ?? string.Empty;
            var score = hit.GetProperty("score").GetDouble();
            var tokenEstimate = hit.GetProperty("tokenEstimate").GetInt32();

            builder
                .Append("- [")
                .Append(packageId)
                .Append("] ")
                .Append(title)
                .Append(" / ")
                .Append(section)
                .Append(". score=")
                .Append(score.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                .Append(", tokens=")
                .Append(tokenEstimate)
                .AppendLine();
            builder.Append("  ").AppendLine(Truncate(CompactWhitespace(content), 220));
        }

        return builder.ToString().TrimEnd();
    }

    private static string CompactWhitespace(string text)
    {
        return string.Join(
            ' ',
            text.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
        );
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 3)] + "...";
    }

}
