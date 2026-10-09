using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GroundKit.Configuration;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Observability.Telemetry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GroundKit.Mcp;

[McpServerToolType]
public static class GroundKitMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(
        Name = "resolve-source",
        Title = "Resolve Installed Documentation Sources",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ResolveSourcePayload)
    )]
    [Description("Resolve installed documentation packages by package id or display name.")]
    public static async Task<CallToolResult> ResolveSourceAsync(
        [Description("Package id or display name to match.")] string query,
        IPackageStore packageStore,
        GroundKitToolResponseAgent toolResponseAgent,
        CancellationToken cancellationToken,
        GroundKitOptions? options = null,
        [Description("Maximum number of matches to return.")] int maxResults = 5
    )
    {
        using var activity = GroundKitTelemetry.ActivitySource.StartActivity(
            GroundKitTelemetry.Activities.MctTool
        );
        activity?.SetTag("groundkit.mcp.method", "resolve-source");

        if (string.IsNullOrWhiteSpace(query))
        {
            return CreateErrorResult("Argument 'query' is required.");
        }

        if (maxResults <= 0)
        {
            return CreateErrorResult("Argument 'maxResults' must be greater than zero.");
        }

        var results = (await packageStore.ListAsync(cancellationToken))
            .Where(package =>
                (options ?? new GroundKitOptions()).IsLibraryAllowed(package.PackageId)
            )
            .Select(package => new
            {
                package.PackageId,
                package.DisplayName,
                package.Version,
                package.DocumentCount,
                package.ChunkCount,
                Score = ComputeMatchScore(query, package),
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(result => new ResolveSourcePackage(
                result.PackageId,
                result.DisplayName,
                result.Version,
                result.DocumentCount,
                result.ChunkCount,
                result.Score
            ))
            .ToArray();

        var payload = new ResolveSourcePayload(results);
        return await CreateSuccessResultAsync(
            toolResponseAgent,
            "resolve-source",
            payload,
            cancellationToken
        );
    }

    [McpServerTool(
        Name = "query-docs",
        Title = "Query Installed Documentation",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(QueryDocsPayload)
    )]
    [Description(
        "Query an installed documentation package for focused sections. If the package is not installed, this returns the command that installs it."
    )]
    public static async Task<CallToolResult> QueryDocsAsync(
        [Description("Installed package id.")] string packageId,
        [Description("Query text to search inside the package.")] string topic,
        IPackageStore packageStore,
        GroundKitToolResponseAgent toolResponseAgent,
        CancellationToken cancellationToken,
        GroundKitOptions? options = null,
        [Description("Maximum token budget for returned hits.")] int maxTokens = 2_000,
        [Description("Maximum number of hits to return.")] int maxHits = 8,
        [Description("Minimum relative score cutoff for returned hits.")]
            double relativeScoreCutoff = 0.5,
        IContextRegistryClient? registryClient = null,
        [Description("Optional search mode: lexical, semantic, or hybrid. Omitted uses configured default.")] string? searchMode = null
    )
    {
        using var activity = GroundKitTelemetry.ActivitySource.StartActivity(
            GroundKitTelemetry.Activities.MctTool
        );
        activity?.SetTag("groundkit.mcp.method", "query-docs");
        if (!TrySearchMode(searchMode, out var mode)) return CreateErrorResult("Argument 'searchMode' must be lexical, semantic, or hybrid.");

        if (string.IsNullOrWhiteSpace(packageId))
        {
            return CreateErrorResult("Argument 'packageId' is required.");
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            return CreateErrorResult("Argument 'topic' is required.");
        }

        if (!(options ?? new GroundKitOptions()).IsLibraryAllowed(packageId))
        {
            return CreateErrorResult($"Package '{packageId}' is not available.");
        }

        if (maxTokens <= 0)
        {
            return CreateErrorResult("Argument 'maxTokens' must be greater than zero.");
        }

        if (maxHits <= 0)
        {
            return CreateErrorResult("Argument 'maxHits' must be greater than zero.");
        }

        if (relativeScoreCutoff is < 0 or > 1)
        {
            return CreateErrorResult("Argument 'relativeScoreCutoff' must be between 0 and 1.");
        }

        // A miss is the most common failure an agent hits. Checking the local store first turns a
        // dead end into one actionable step instead of an opaque error, and never downloads
        // anything by itself: the agent still chooses the registry, package, and version.
        if (await packageStore.GetPackageAsync(packageId, cancellationToken) is null)
        {
            return CreateErrorResult(
                await DescribeMissingPackageAsync(
                    packageId,
                    registryClient,
                    packageStore,
                    cancellationToken
                )
            );
        }

        var (response, error) = await TryQueryAsync(
            packageStore,
            new DocsQueryRequest(
                packageId,
                topic,
                new RetrievalOptions(maxTokens, maxHits, relativeScoreCutoff, mode)
            ),
            cancellationToken
        );

        if (error is not null) return CreateErrorResult(error);
        return await CreateQueryResultAsync(toolResponseAgent, response!, cancellationToken);
    }

    [McpServerTool(
        Name = "ask-docs",
        Title = "Ask Installed or Published Documentation",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(QueryDocsPayload)
    )]
    [Description(
        "Ask a documentation question without naming a package. The package is detected from the question; an installed copy is used first, otherwise a published package is downloaded on demand. If nothing matches, the response names the package to add."
    )]
    public static async Task<CallToolResult> AskDocsAsync(
        [Description("Natural-language documentation question, such as 'what are components in angular?'.")]
            string question,
        IPackageStore packageStore,
        GroundKitToolResponseAgent toolResponseAgent,
        CancellationToken cancellationToken,
        GroundKitOptions? options = null,
        [Description("Maximum token budget for returned hits.")] int maxTokens = 2_000,
        [Description("Maximum number of hits to return.")] int maxHits = 8,
        [Description("Minimum relative score cutoff for returned hits.")]
            double relativeScoreCutoff = 0.5,
        IContextRegistryClient? registryClient = null,
        IPackageDownloadService? packageDownloadService = null,
        [Description("Optional search mode: lexical, semantic, or hybrid. Omitted uses configured default.")] string? searchMode = null
    )
    {
        using var activity = GroundKitTelemetry.ActivitySource.StartActivity(
            GroundKitTelemetry.Activities.MctTool
        );
        activity?.SetTag("groundkit.mcp.method", "ask-docs");
        if (!TrySearchMode(searchMode, out var mode)) return CreateErrorResult("Argument 'searchMode' must be lexical, semantic, or hybrid.");

        if (string.IsNullOrWhiteSpace(question))
        {
            return CreateErrorResult("Argument 'question' is required.");
        }

        if (maxTokens <= 0)
        {
            return CreateErrorResult("Argument 'maxTokens' must be greater than zero.");
        }

        if (maxHits <= 0)
        {
            return CreateErrorResult("Argument 'maxHits' must be greater than zero.");
        }

        if (relativeScoreCutoff is < 0 or > 1)
        {
            return CreateErrorResult("Argument 'relativeScoreCutoff' must be between 0 and 1.");
        }

        var selector = await DetectPackageAsync(question, packageStore, options, cancellationToken);
        if (selector is null)
        {
            return CreateErrorResult(
                await DescribeUndetectedPackageAsync(question, packageStore, cancellationToken)
            );
        }

        // A local copy is preferred: detection returns the installed id when the same package is
        // already present, and `GetPackageAsync` resolves a registry-prefixed selector to it too.
        if (await packageStore.GetPackageAsync(selector, cancellationToken) is null)
        {
            var installedSelector = await InstallForAskAsync(
                selector,
                registryClient,
                packageDownloadService,
                packageStore,
                cancellationToken
            );
            if (installedSelector is null)
            {
                return CreateErrorResult(
                    await DescribeMissingPackageAsync(
                        selector,
                        registryClient,
                        packageStore,
                        cancellationToken
                    )
                );
            }

            selector = installedSelector;
        }

        var (response, error) = await TryQueryAsync(
            packageStore,
            new DocsQueryRequest(
                selector,
                question,
                new RetrievalOptions(maxTokens, maxHits, relativeScoreCutoff, mode)
            ),
            cancellationToken
        );

        if (error is not null) return CreateErrorResult(error);
        return await CreateQueryResultAsync(toolResponseAgent, response!, cancellationToken);
    }

    /// <summary>How many libraries one question may span, matching the hint limit the tools document.</summary>
    private const int MaxLibrariesPerQuery = 4;

    [McpServerTool(
        Name = "find_libraries",
        Title = "Find Documentation Libraries For A Question",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(FindLibrariesPayload)
    )]
    [Description(
        "Resolve which documentation libraries a question is about before querying them. Returns library ids that query-docs accepts, ranked by relevance and preferring installed packages. Use this when the library is unclear or the question could span several."
    )]
    public static async Task<CallToolResult> FindLibrariesAsync(
        [Description("Natural-language question or task used to rank candidate libraries.")]
            string question,
        IPackageStore packageStore,
        GroundKitToolResponseAgent toolResponseAgent,
        CancellationToken cancellationToken,
        GroundKitOptions? options = null,
        [Description("Optional name or id that must appear in a candidate name.")]
            string? libraryName = null,
        [Description("Maximum number of candidates to return.")] int maxResults = 5
    )
    {
        using var activity = GroundKitTelemetry.ActivitySource.StartActivity(
            GroundKitTelemetry.Activities.MctTool
        );
        activity?.SetTag("groundkit.mcp.method", "find_libraries");

        if (string.IsNullOrWhiteSpace(question))
        {
            return CreateErrorResult("Argument 'question' is required.");
        }

        if (maxResults <= 0)
        {
            return CreateErrorResult("Argument 'maxResults' must be greater than zero.");
        }

        var allowed = options ?? new GroundKitOptions();
        var installed = (await packageStore.ListAsync(cancellationToken))
            .Where(package => allowed.IsLibraryAllowed(package.PackageId))
            .ToArray();
        var candidates = BuildCandidates(installed, allowed);

        // A name filter is an extra constraint, not a replacement for the question: it narrows the
        // pool, and the question still decides the order inside it.
        var pool = string.IsNullOrWhiteSpace(libraryName)
            ? candidates
            : candidates
                .Where(candidate =>
                    candidate.Contains(libraryName, StringComparison.OrdinalIgnoreCase)
                )
                .ToArray();

        var ranked = PackageMentionDetector.MatchAll(question, pool, maxResults);
        var names = ranked.Count > 0
            ? ranked.Select(match => (match.Name, Score: (double)match.Score))
            : pool.Take(maxResults).Select(name => (Name: name, Score: 1.0d));

        var libraries = names
            .Select(item => ToLibraryCandidate(item.Name, item.Score, installed))
            .ToArray();

        return await CreateSuccessResultAsync(
            toolResponseAgent,
            "find_libraries",
            new FindLibrariesPayload(libraries),
            cancellationToken
        );
    }

    [McpServerTool(
        Name = "search-docs",
        Title = "Search Documentation Across Libraries",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(SearchDocsPayload)
    )]
    [Description(
        "Answer a documentation question in one call. The question selects the libraries; up to four optional library hints pin them. Installed packages are used first and a published package is downloaded once. Hits from every library are fused and tagged with their package."
    )]
    public static async Task<CallToolResult> SearchDocsAsync(
        [Description("Natural-language documentation question.")] string question,
        IPackageStore packageStore,
        GroundKitToolResponseAgent toolResponseAgent,
        CancellationToken cancellationToken,
        GroundKitOptions? options = null,
        [Description("Optional library names or ids to pin, up to four.")]
            string[]? libraries = null,
        [Description("Maximum token budget for returned hits.")] int maxTokens = 2_000,
        [Description("Maximum number of hits to return.")] int maxHits = 8,
        [Description("Minimum relative score cutoff for returned hits.")]
            double relativeScoreCutoff = 0.5,
        IContextRegistryClient? registryClient = null,
        IPackageDownloadService? packageDownloadService = null,
        [Description("Optional search mode: lexical, semantic, or hybrid. Omitted uses configured default.")] string? searchMode = null
    )
    {
        using var activity = GroundKitTelemetry.ActivitySource.StartActivity(
            GroundKitTelemetry.Activities.MctTool
        );
        activity?.SetTag("groundkit.mcp.method", "search-docs");
        if (!TrySearchMode(searchMode, out var mode)) return CreateErrorResult("Argument 'searchMode' must be lexical, semantic, or hybrid.");

        if (string.IsNullOrWhiteSpace(question))
        {
            return CreateErrorResult("Argument 'question' is required.");
        }

        if (maxTokens <= 0)
        {
            return CreateErrorResult("Argument 'maxTokens' must be greater than zero.");
        }

        if (maxHits <= 0)
        {
            return CreateErrorResult("Argument 'maxHits' must be greater than zero.");
        }

        if (relativeScoreCutoff is < 0 or > 1)
        {
            return CreateErrorResult("Argument 'relativeScoreCutoff' must be between 0 and 1.");
        }

        var selectors = await ResolveQuerySelectorsAsync(
            question,
            libraries,
            packageStore,
            options,
            cancellationToken
        );

        if (selectors.Count == 0)
        {
            return CreateErrorResult(
                await DescribeUndetectedPackageAsync(question, packageStore, cancellationToken)
            );
        }

        var responses = new List<DocsQueryResponse>();
        var resolved = new List<string>();

        // Each library is resolved independently: one that cannot be installed or has no matching
        // section must not stop the others from answering.
        foreach (var selector in selectors)
        {
            var querySelector = selector;
            if (await packageStore.GetPackageAsync(selector, cancellationToken) is null)
            {
                var installedSelector = await InstallForAskAsync(
                    selector,
                    registryClient,
                    packageDownloadService,
                    packageStore,
                    cancellationToken
                );
                if (installedSelector is null)
                {
                    continue;
                }

                querySelector = installedSelector;
            }

            var (response, error) = await TryQueryAsync(
                packageStore,
                new DocsQueryRequest(
                    querySelector,
                    question,
                    new RetrievalOptions(maxTokens, maxHits, relativeScoreCutoff, mode)
                ),
                cancellationToken
            );

            if (error is not null) return CreateErrorResult(error);
            resolved.Add(response!.PackageId);
            if (response.Hits.Count > 0)
            {
                responses.Add(response);
            }
        }

        if (resolved.Count == 0)
        {
            return CreateErrorResult(
                await DescribeMissingPackageAsync(
                    selectors[0],
                    registryClient,
                    packageStore,
                    cancellationToken
                )
            );
        }

        var hits = RankFusion
            .Fuse(responses, maxTokens)
            .Select(fused => new SearchDocsHit(
                fused.PackageId,
                fused.Version,
                fused.Hit.DocumentTitle,
                fused.Hit.SectionTitle,
                fused.Hit.Content,
                fused.Hit.TokenEstimate,
                fused.Hit.HasCode,
                fused.Hit.Score
            ))
            .ToArray();
        var payload = new SearchDocsPayload(
            question,
            resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            hits.Sum(hit => hit.TokenEstimate),
            hits
        );

        return await CreateSuccessResultAsync(
            toolResponseAgent,
            "search-docs",
            payload,
            cancellationToken
        );
    }

    /// <summary>
    /// Decides which packages a one-call search should span: explicit hints first, then whatever the
    /// question names. Installed ids are preferred so a local copy answers without the registry.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResolveQuerySelectorsAsync(
        string question,
        string[]? libraries,
        IPackageStore packageStore,
        GroundKitOptions? options,
        CancellationToken cancellationToken
    )
    {
        var allowed = options ?? new GroundKitOptions();
        var installed = (await packageStore.ListAsync(cancellationToken))
            .Where(package => allowed.IsLibraryAllowed(package.PackageId))
            .ToArray();

        var selectors = new List<string>();
        foreach (var hint in libraries ?? [])
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                continue;
            }

            AddSelector(selectors, BuildSelector(hint, installed));
            if (selectors.Count >= MaxLibrariesPerQuery)
            {
                return selectors;
            }
        }

        if (selectors.Count > 0)
        {
            return selectors;
        }

        var candidates = BuildCandidates(installed, allowed);
        foreach (var match in PackageMentionDetector.MatchAll(
            question,
            candidates,
            MaxLibrariesPerQuery
        ))
        {
            AddSelector(selectors, BuildSelector(match.Name, installed));
        }

        return selectors;
    }

    private static void AddSelector(List<string> selectors, string? selector)
    {
        if (
            !string.IsNullOrWhiteSpace(selector)
            && !selectors.Contains(selector, StringComparer.OrdinalIgnoreCase)
        )
        {
            selectors.Add(selector);
        }
    }

    /// <summary>
    /// Turns a name, id, or hint into the selector the rest of the pipeline understands, preferring an
    /// installed package so a local copy is never replaced by a download.
    /// </summary>
    private static string? BuildSelector(string hint, IReadOnlyList<PackageSummary> installed)
    {
        var reference = PackageReference.Parse(hint);
        if (string.IsNullOrWhiteSpace(reference.Name))
        {
            return null;
        }

        var installedMatch = installed.FirstOrDefault(package =>
            string.Equals(package.PackageId, reference.Name, StringComparison.OrdinalIgnoreCase)
        );
        if (installedMatch is not null)
        {
            var version = reference.Version ?? installedMatch.Version;
            return string.IsNullOrWhiteSpace(version)
                ? installedMatch.PackageId
                : $"{installedMatch.PackageId}@{version}";
        }

        var curated = LibraryCatalog.Find(reference.Name);
        var registry = curated?.Registry ?? reference.Registry;
        var name = curated?.Name ?? reference.Name;
        return reference.Version is null
            ? $"{registry}/{name}"
            : $"{registry}/{name}@{reference.Version}";
    }

    /// <summary>
    /// The library names worth considering for a question: installed packages first, then the curated
    /// catalog. The order is deliberate, because the detector breaks equal scores by candidate order.
    /// </summary>
    private static string[] BuildCandidates(
        IReadOnlyList<PackageSummary> installed,
        GroundKitOptions allowed
    ) =>
        installed
            .Select(package => package.PackageId)
            .Concat(
                LibraryCatalog
                    .StarterLibraries.Where(entry => allowed.IsLibraryAllowed(entry.Name))
                    .Select(entry => entry.Name)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static LibraryCandidate ToLibraryCandidate(
        string name,
        double score,
        IReadOnlyList<PackageSummary> installed
    )
    {
        var curated = LibraryCatalog.Find(name);
        var installedMatch = installed.FirstOrDefault(package =>
            string.Equals(package.PackageId, name, StringComparison.OrdinalIgnoreCase)
        );

        if (installedMatch is not null)
        {
            return new LibraryCandidate(
                string.IsNullOrWhiteSpace(installedMatch.Version)
                    ? installedMatch.PackageId
                    : $"{installedMatch.PackageId}@{installedMatch.Version}",
                installedMatch.PackageId,
                installedMatch.Version,
                Installed: true,
                curated?.Registry,
                curated?.Description,
                curated?.Repository,
                curated?.DocsPath,
                score
            );
        }

        var registry = curated?.Registry ?? PackageReference.DefaultRegistry;
        var canonicalName = curated?.Name ?? name;
        return new LibraryCandidate(
            $"{registry}/{canonicalName}",
            canonicalName,
            Version: null,
            Installed: false,
            registry,
            curated?.Description,
            curated?.Repository,
            curated?.DocsPath,
            score
        );
    }

    /// <summary>Projects a single-package query result into the shared MCP payload.</summary>
    private static async Task<CallToolResult> CreateQueryResultAsync(
        GroundKitToolResponseAgent toolResponseAgent,
        DocsQueryResponse response,
        CancellationToken cancellationToken
    )
    {
        var payload = new QueryDocsPayload(
            response.PackageId,
            response.Version,
            response.TotalTokens,
            response
                .Hits.Select(hit => new QueryDocsHit(
                    hit.DocumentTitle,
                    hit.SectionTitle,
                    hit.Content,
                    hit.TokenEstimate,
                    hit.HasCode,
                    hit.Score
                ))
                .ToArray()
        );

        return await CreateSuccessResultAsync(
            toolResponseAgent,
            "query-docs",
            payload,
            cancellationToken
        );
    }

    /// <summary>
    /// Infers the package a question is about, preferring an installed package over a curated but
    /// not-yet-downloaded one so the registry is only consulted when it has something to add.
    /// </summary>
    private static async Task<string?> DetectPackageAsync(
        string question,
        IPackageStore packageStore,
        GroundKitOptions? options,
        CancellationToken cancellationToken
    )
    {
        var installed = await packageStore.ListAsync(cancellationToken);
        var allowed = options ?? new GroundKitOptions();

        var candidates = installed
            .Select(package => package.PackageId)
            .Where(allowed.IsLibraryAllowed)
            .Concat(
                LibraryCatalog
                    .StarterLibraries.Where(entry => allowed.IsLibraryAllowed(entry.Name))
                    .Select(entry => entry.Name)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var matched = PackageMentionDetector.Detect(question, candidates);
        if (matched is null)
        {
            return null;
        }

        var installedMatch = installed.FirstOrDefault(package =>
            string.Equals(package.PackageId, matched, StringComparison.OrdinalIgnoreCase)
        );
        if (installedMatch is not null)
        {
            return installedMatch.PackageId;
        }

        var curated = LibraryCatalog.Find(matched);
        return curated is null ? matched : $"{curated.Registry}/{curated.Name}";
    }

    /// <summary>
    /// Downloads the detected package from the registry so the question can be answered in one
    /// step. Non-network failures and an offline registry both fall through to the guidance path.
    /// </summary>
    /// <returns>The selector to query with, or null when nothing could be installed.</returns>
    private static async Task<string?> InstallForAskAsync(
        string selector,
        IContextRegistryClient? registryClient,
        IPackageDownloadService? packageDownloadService,
        IPackageStore packageStore,
        CancellationToken cancellationToken
    )
    {
        if (registryClient is null || packageDownloadService is null)
        {
            return null;
        }

        var (registry, name, requestedVersion) = SplitSelector(selector);
        IReadOnlyList<RegistryPackage> matches;
        try
        {
            matches = await registryClient.SearchAsync(
                registry,
                name,
                requestedVersion,
                cancellationToken
            );

            // The registry matches versions exactly, so a partial or absent version needs a second
            // lookup for every published version before the shared resolver can choose one.
            if (matches.Count == 0 && requestedVersion is not null)
            {
                matches = await registryClient.SearchAsync(
                    registry,
                    name,
                    version: null,
                    cancellationToken
                );
            }
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or TaskCanceledException
                        or InvalidOperationException
            )
        {
            return null;
        }

        var resolvedVersion = PackageVersionResolver.Resolve(
            requestedVersion,
            matches.Select(candidate => candidate.Version)
        );
        var match = resolvedVersion is null
            ? null
            : matches.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Version,
                    resolvedVersion,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        if (match is null)
        {
            return null;
        }

        var installedPath = await packageDownloadService.InstallRegistryPackageAsync(
            match.Registry,
            match.Name,
            match.Version,
            cancellationToken
        );

        // The manifest inside the artifact is the authority on the id and version label, so resolve
        // through the store instead of trusting the catalog entry's spelling.
        var installed = (await packageStore.ListAsync(cancellationToken)).FirstOrDefault(package =>
            string.Equals(
                Path.GetFullPath(package.PackagePath),
                Path.GetFullPath(installedPath),
                StringComparison.OrdinalIgnoreCase
            )
        );

        // A package with no version is keyed by its id alone; appending a made-up label would ask
        // the store for a version that does not exist and turn a resolvable query into a miss.
        return installed is null
            ? match.Name
            : string.IsNullOrWhiteSpace(installed.Version)
                ? installed.PackageId
                : $"{installed.PackageId}@{installed.Version}";
    }

    private static (string Registry, string Name, string? Version) SplitSelector(string selector)
    {
        var reference = PackageReference.Parse(selector);
        return (reference.Registry, reference.Name, reference.Version);
    }

    private static async Task<string> DescribeUndetectedPackageAsync(
        string question,
        IPackageStore packageStore,
        CancellationToken cancellationToken
    )
    {
        var builder = new StringBuilder();
        builder.Append("Could not tell which package '")
            .Append(question)
            .AppendLine("' is about.");

        var installed = await packageStore.ListAsync(cancellationToken);
        var knownNames = installed
            .Select(package => package.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (knownNames.Length > 0)
        {
            builder.Append("Installed packages: ")
                .Append(string.Join(", ", knownNames))
                .AppendLine();
        }

        builder.AppendLine()
            .AppendLine("Name the package explicitly, or add its documentation first:")
            .AppendLine("  ck query <package-id> '<topic>'")
            .AppendLine("  ck add <repository-or-path> --docs-path <folder> --name <name>");
        return builder.ToString();
    }

    [McpServerTool(
        Name = "get_docs",
        Title = "Get Library Documentation",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(QueryDocsPayload)
    )]
    [Description(
        "Primary documentation lookup. Resolve a package first, then search it with a short API name or topic. If the package is not installed, this returns the command that installs it."
    )]
    public static Task<CallToolResult> GetDocsAsync(
        [Description("Installed package id returned by resolve-source.")] string library,
        [Description("Short API name, keyword, or phrase. Extra words narrow results.")]
            string topic,
        IPackageStore packageStore,
        GroundKitToolResponseAgent toolResponseAgent,
        CancellationToken cancellationToken,
        GroundKitOptions? options = null,
        [Description("Maximum token budget for returned hits.")] int maxTokens = 2_000,
        [Description("Maximum number of hits to return.")] int maxHits = 8,
        [Description("Minimum relative score cutoff from 0 to 1.")] double relativeScoreCutoff = 0.5,
        IContextRegistryClient? registryClient = null,
        [Description("Optional search mode: lexical, semantic, or hybrid. Omitted uses configured default.")] string? searchMode = null
    ) =>
        QueryDocsAsync(
            library,
            topic,
            packageStore,
            toolResponseAgent,
            cancellationToken,
            options,
            maxTokens,
            maxHits,
            relativeScoreCutoff,
            registryClient,
            searchMode
        );

    private static bool TrySearchMode(string? value, out SearchMode? mode)
    {
        mode = value?.ToLowerInvariant() switch
        {
            "lexical" => SearchMode.Lexical,
            "semantic" => SearchMode.Semantic,
            "hybrid" => SearchMode.Hybrid,
            _ => null,
        };
        return value is null || mode is not null;
    }

    private static async Task<(DocsQueryResponse? Response, string? Error)> TryQueryAsync(
        IPackageStore store, DocsQueryRequest request, CancellationToken cancellationToken
    )
    {
        try { return (await store.QueryAsync(request, cancellationToken), null); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            return (null, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "Local embedding provider timed out. Check ck semantic status or retry with searchMode lexical.");
        }
    }

    [McpServerTool(
        Name = "library_catalog",
        Title = "Browse Starter Library Catalog",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(LibraryCatalogPayload)
    )]
    [Description(
        "Browse 19 curated library documentation sources available for local package building."
    )]
    public static async Task<CallToolResult> LibraryCatalogAsync(
        [Description("Optional library name or keyword filter.")] string? query,
        GroundKitToolResponseAgent toolResponseAgent,
        CancellationToken cancellationToken
    )
    {
        var entries = LibraryCatalog
            .StarterLibraries.Where(entry =>
                string.IsNullOrWhiteSpace(query)
                || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();

        return await CreateSuccessResultAsync(
            toolResponseAgent,
            "library_catalog",
            new LibraryCatalogPayload(entries),
            cancellationToken
        );
    }

    [McpServerTool(
        Name = "search_packages",
        Title = "Search Documentation Packages",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(SearchPackagesPayload)
    )]
    [Description(
        "Search the hosted documentation registry. Use short package names, then download a matching version."
    )]
    public static async Task<CallToolResult> SearchPackagesAsync(
        [Description("Package registry, such as npm, pip, cargo, or maven.")] string registry,
        [Description("Short package name, such as react, next, or fastapi.")] string name,
        IContextRegistryClient registryClient,
        GroundKitToolResponseAgent toolResponseAgent,
        CancellationToken cancellationToken,
        [Description("Optional exact package version.")] string? version = null
    )
    {
        if (string.IsNullOrWhiteSpace(registry) || string.IsNullOrWhiteSpace(name))
        {
            return CreateErrorResult("Arguments 'registry' and 'name' are required.");
        }

        var results = await registryClient.SearchAsync(registry, name, version, cancellationToken);
        return await CreateSuccessResultAsync(
            toolResponseAgent,
            "search_packages",
            new SearchPackagesPayload(results),
            cancellationToken
        );
    }

    [McpServerTool(
        Name = "download_package",
        Title = "Download Documentation Package",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(DownloadPackagePayload)
    )]
    [Description(
        "Download a hosted documentation package and install it locally for get_docs queries."
    )]
    public static async Task<CallToolResult> DownloadPackageAsync(
        [Description("Package registry, such as npm or pip.")] string registry,
        [Description("Package name.")] string name,
        [Description("Exact package version.")] string version,
        IContextRegistryClient registryClient,
        IPackageStore packageStore,
        IPackageDownloadService packageDownloadService,
        GroundKitToolResponseAgent toolResponseAgent,
        CancellationToken cancellationToken
    )
    {
        if (
            string.IsNullOrWhiteSpace(registry)
            || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(version)
        )
        {
            return CreateErrorResult("Arguments 'registry', 'name', and 'version' are required.");
        }

        var installedPath = await packageDownloadService.InstallRegistryPackageAsync(
            registry,
            name,
            version,
            cancellationToken
        );
        var payload = new DownloadPackagePayload(name, version, installedPath);
        return await CreateSuccessResultAsync(
            toolResponseAgent,
            "download_package",
            payload,
            cancellationToken
        );
    }

    /// <summary>
    /// Explains a missing package and, when the registry answers, lists the versions that could be
    /// installed. Registry problems are swallowed on purpose: the guidance for a missing local
    /// package is still useful when the network is unavailable.
    /// </summary>
    private static async Task<string> DescribeMissingPackageAsync(
        string packageSelector,
        IContextRegistryClient? registryClient,
        IPackageStore packageStore,
        CancellationToken cancellationToken
    )
    {
        var separator = packageSelector.LastIndexOf('@');
        var name = separator > 0 ? packageSelector[..separator] : packageSelector;
        var requestedVersion = separator > 0 ? packageSelector[(separator + 1)..] : null;

        // Bare names default to the npm registry, the same convention the CLI uses for `install`.
        var registrySeparator = name.IndexOf('/');
        var registry = registrySeparator > 0 ? name[..registrySeparator] : "npm";
        name = registrySeparator > 0 ? name[(registrySeparator + 1)..] : name;

        var builder = new StringBuilder();
        builder.Append("Package '")
            .Append(packageSelector)
            .AppendLine("' is not installed locally.");

        var installed = await packageStore.ListAsync(cancellationToken);
        var knownNames = installed
            .Select(package => package.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (knownNames.Length > 0)
        {
            builder.Append("Installed packages: ")
                .Append(string.Join(", ", knownNames))
                .AppendLine();
        }

        IReadOnlyList<RegistryPackage> matches = [];
        var registryError = false;
        if (registryClient is not null)
        {
            try
            {
                matches = await registryClient.SearchAsync(
                    registry,
                    name,
                    requestedVersion,
                    cancellationToken
                );

                // The registry matches versions exactly, so a version miss hides the versions that
                // do exist. Re-asking without a version is what turns "not found" into a next step.
                if (matches.Count == 0 && requestedVersion is not null)
                {
                    builder.Append("Version '")
                        .Append(requestedVersion)
                        .Append("' of ")
                        .Append(registry)
                        .Append('/')
                        .Append(name)
                        .AppendLine(" is not published.");
                    requestedVersion = null;
                    matches = await registryClient.SearchAsync(
                        registry,
                        name,
                        version: null,
                        cancellationToken
                    );
                }
            }
            catch (Exception exception)
                when (exception
                        is HttpRequestException
                            or TaskCanceledException
                            or InvalidOperationException
                )
            {
                registryError = true;
                builder.Append("The registry could not be reached while looking for ")
                    .Append(registry)
                    .Append('/')
                    .Append(name)
                    .AppendLine(".");
            }
        }

        if (matches.Count > 0)
        {
            builder.AppendLine().AppendLine("Available in the registry:");
            foreach (var match in matches.Take(8))
            {
                builder.Append("  ")
                    .Append(match.Registry)
                    .Append('/')
                    .Append(match.Name)
                    .Append('@')
                    .Append(match.Version);
                if (match.Size is long size)
                {
                    builder.Append(" (")
                        .Append(size.ToString("N0", CultureInfo.InvariantCulture))
                        .Append(" bytes)");
                }

                builder.AppendLine();
            }

            var selected = requestedVersion is not null
                ? matches.FirstOrDefault(match => match.Version == requestedVersion) ?? matches[0]
                : matches[0];
            builder.AppendLine()
                .AppendLine("Install one, then query again:")
                .Append("  ck install ")
                .Append(selected.Registry)
                .Append('/')
                .Append(selected.Name)
                .Append(' ')
                .AppendLine(selected.Version);
            builder.Append("  ck query '")
                .Append(selected.Name)
                .Append('@')
                .Append(selected.Version)
                .AppendLine("' '<topic>'");
            builder.AppendLine()
                .AppendLine("Or call the download_package tool with registry, name, and version.")
                .Append("The package is only needed once: queries run entirely from the local store.");
            return builder.ToString();
        }

        if (registryError)
        {
            builder.AppendLine()
                .AppendLine("Install it without a catalog lookup, or build it from source:")
                .Append("  ck install ")
                .Append(registry)
                .Append('/')
                .AppendLine(name)
                .Append("  ck add <repository-or-path>");
            return builder.ToString();
        }

        builder.AppendLine()
            .AppendLine("Build or install it first, then query again:")
            .Append("  ck install ")
            .Append(registry)
            .Append('/')
            .AppendLine(name)
            .Append("  ck add <repository-or-path>  # or build from any documentation source");
        return builder.ToString();
    }

    private static async Task<CallToolResult> CreateSuccessResultAsync<TPayload>(
        GroundKitToolResponseAgent toolResponseAgent,
        string toolName,
        TPayload payload,
        CancellationToken cancellationToken
    )
        where TPayload : notnull
    {
        var text = await toolResponseAgent.ComposeAsync(toolName, payload, cancellationToken);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = JsonSerializer.SerializeToElement(payload, JsonOptions),
        };
    }

    private static CallToolResult CreateErrorResult(string message)
    {
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }],
        };
    }

    private static double ComputeMatchScore(string query, PackageSummary package)
    {
        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return 0;
        }

        if (package.PackageId.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        if (package.DisplayName.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 0.95;
        }

        if (package.PackageId.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 0.85;
        }

        if (package.DisplayName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 0.75;
        }

        var queryTerms = normalizedQuery.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (queryTerms.Length == 0)
        {
            return 0;
        }

        var matchedTerms = queryTerms.Count(term =>
            package.PackageId.Contains(term, StringComparison.OrdinalIgnoreCase)
            || package.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
        );

        return matchedTerms == 0 ? 0 : matchedTerms / (double)queryTerms.Length * 0.6;
    }

    public sealed record ResolveSourcePayload(IReadOnlyList<ResolveSourcePackage> Packages);

    public sealed record ResolveSourcePackage(
        string PackageId,
        string DisplayName,
        string? Version,
        int DocumentCount,
        int ChunkCount,
        double Score
    );

    public sealed record QueryDocsPayload(
        string PackageId,
        string? Version,
        int TotalTokens,
        IReadOnlyList<QueryDocsHit> Hits
    );

    public sealed record QueryDocsHit(
        string DocumentTitle,
        string SectionTitle,
        string Content,
        int TokenEstimate,
        bool HasCode,
        double Score
    );

    public sealed record FindLibrariesPayload(IReadOnlyList<LibraryCandidate> Libraries);

    public sealed record LibraryCandidate(
        string LibraryId,
        string Name,
        string? Version,
        bool Installed,
        string? Registry,
        string? Description,
        string? Repository,
        string? DocsPath,
        double Score
    );

    public sealed record SearchDocsPayload(
        string Question,
        IReadOnlyList<string> Packages,
        int TotalTokens,
        IReadOnlyList<SearchDocsHit> Hits
    );

    public sealed record SearchDocsHit(
        string PackageId,
        string? Version,
        string DocumentTitle,
        string SectionTitle,
        string Content,
        int TokenEstimate,
        bool HasCode,
        double Score
    );

    public sealed record LibraryCatalogPayload(IReadOnlyList<LibraryCatalogEntry> Libraries);

    public sealed record SearchPackagesPayload(IReadOnlyList<RegistryPackage> Results);

    public sealed record DownloadPackagePayload(string Name, string Version, string InstalledPath);
}
