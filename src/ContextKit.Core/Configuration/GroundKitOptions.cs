using System.Text.Json;

namespace GroundKit.Configuration;

public sealed class GroundKitOptions
{
    public string? RegistryUrl { get; init; }
    public Dictionary<string, string>? HttpHeaders { get; init; }
    public string[]? AllowedLibraries { get; init; }
    public int LlmsMaxLinks { get; init; } = 12;
    public int LlmsConcurrency { get; init; } = 4;
    public int HttpTimeoutSeconds { get; init; } = 60;
    public long MaxResponseBytes { get; init; } = 10 * 1024 * 1024;

    public static GroundKitOptions Load(string? basePath = null)
    {
        basePath ??= Directory.GetCurrentDirectory();
        var path = Path.Combine(basePath, ".groundkit", "config.json");
        var options = File.Exists(path)
            ? JsonSerializer.Deserialize<GroundKitOptions>(File.ReadAllText(path)) ?? new()
            : new();

        return new GroundKitOptions
        {
            RegistryUrl =
                Environment.GetEnvironmentVariable("GROUNDKIT_REGISTRY_URL") ?? options.RegistryUrl,
            AllowedLibraries =
                ParseLibraries(Environment.GetEnvironmentVariable("GROUNDKIT_ALLOWED_LIBRARIES"))
                ?? options.AllowedLibraries,
            LlmsMaxLinks = ReadInt("GROUNDKIT_LLMS_MAX_LINKS", options.LlmsMaxLinks, 1, 500),
            LlmsConcurrency = ReadInt("GROUNDKIT_LLMS_CONCURRENCY", options.LlmsConcurrency, 1, 32),
            HttpTimeoutSeconds = ReadInt(
                "GROUNDKIT_HTTP_TIMEOUT_SECONDS",
                options.HttpTimeoutSeconds,
                1,
                600
            ),
            MaxResponseBytes = ReadLong(
                "GROUNDKIT_MAX_RESPONSE_BYTES",
                options.MaxResponseBytes,
                1_024,
                100 * 1024 * 1024
            ),
        };
    }

    public bool IsLibraryAllowed(string packageId) =>
        AllowedLibraries is null
        || AllowedLibraries.Length == 0
        || AllowedLibraries.Any(library =>
            string.Equals(library, packageId, StringComparison.OrdinalIgnoreCase)
        );

    private static string[]? ParseLibraries(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );

    private static int ReadInt(string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
        && value >= minimum
        && value <= maximum
            ? value
            : fallback;

    private static long ReadLong(string name, long fallback, long minimum, long maximum) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value)
        && value >= minimum
        && value <= maximum
            ? value
            : fallback;
}
