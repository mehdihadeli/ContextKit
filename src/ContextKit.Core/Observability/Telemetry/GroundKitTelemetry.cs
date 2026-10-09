using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GroundKit.Observability.Telemetry;

public static class GroundKitTelemetry
{
    public const string ActivitySourceName = "GroundKit";
    public const string MeterName = "GroundKit";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static class Activities
    {
        public const string SourceDetect = "source-detect";
        public const string GitFetch = "git-fetch";
        public const string LlmsFetch = "llms-fetch";
        public const string PageFetch = "page-fetch";
        public const string Normalize = "normalize";
        public const string Chunk = "chunk";
        public const string PackageBuild = "package-build";
        public const string Search = "search";
        public const string MctTool = "mcp-tool";
    }

    public static class Metrics
    {
        public static readonly Histogram<double> BuildDurationMs = Meter.CreateHistogram<double>(
            "groundkit.build.duration",
            unit: "ms"
        );

        public static readonly Histogram<double> SearchDurationMs = Meter.CreateHistogram<double>(
            "groundkit.search.duration",
            unit: "ms"
        );

        public static readonly Counter<long> PackagesInstalled = Meter.CreateCounter<long>(
            "groundkit.packages.installed",
            unit: "packages"
        );

        public static readonly Counter<long> ChunksCreated = Meter.CreateCounter<long>(
            "groundkit.chunks.created",
            unit: "chunks"
        );

        public static readonly Counter<long> ResultsReturned = Meter.CreateCounter<long>(
            "groundkit.results.returned",
            unit: "results"
        );

        public static readonly Counter<long> TokenBudgetTrims = Meter.CreateCounter<long>(
            "groundkit.token_budget.trims",
            unit: "trims"
        );

        public static readonly Counter<long> IngestionFailures = Meter.CreateCounter<long>(
            "groundkit.ingestion.failures",
            unit: "failures"
        );

        public static readonly Counter<long> RefreshFailures = Meter.CreateCounter<long>(
            "groundkit.refresh.failures",
            unit: "failures"
        );
    }
}
