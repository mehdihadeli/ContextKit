using GroundKit.Core.Contracts;
using GroundKit.Ingestion.Services;
using GroundKit.Semantic;
using GroundKit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroundKit.Storage.Sqlite.Tests.Integration;

public sealed class SemanticSearchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "groundkit-semantic-search", Guid.NewGuid().ToString("N"));
    private readonly SemanticRuntime runtime;
    private readonly FakeEmbeddingProvider embeddings = new();

    public SemanticSearchTests() => runtime = new SemanticRuntime(Path.Combine(root, "semantic"));

    [Fact]
    public async Task Should_Keep_Default_Lexical_Without_Embedding_Calls()
    {
        var (store, id) = await CreateStore();
        var response = await Query(store, id, "cleanup", null);
        response.Hits.ShouldHaveSingleItem().Path.ShouldBe("lifecycle.md");
        embeddings.Calls.ShouldBe(0);
        Directory.Exists(runtime.CachePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_Find_Paraphrase_That_Lexical_Search_Misses()
    {
        var (store, id) = await CreateStore();
        var lexical = await Query(store, id, "stop background work", SearchMode.Lexical);
        lexical.Hits.ShouldBeEmpty();
        var semantic = await Query(store, id, "stop background work", SearchMode.Semantic);
        var hit = semantic.Hits.ShouldHaveSingleItem();
        hit.Path.ShouldBe("lifecycle.md");
        hit.Content.ShouldContain("Cancel timers");
        hit.Score.ShouldBe(1);
        hit.ChunkId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Should_Deduplicate_Hybrid_Hits_And_Use_Rank_Scores()
    {
        var (store, id) = await CreateStore();
        var response = await Query(store, id, "cleanup", SearchMode.Hybrid);
        var hit = response.Hits.ShouldHaveSingleItem();
        hit.Path.ShouldBe("lifecycle.md");
        hit.Score.ShouldBe(2d / 61, 0.0000001);
        response.TotalTokens.ShouldBe(hit.TokenEstimate);
    }

    [Fact]
    public async Task Should_Reuse_Document_Embeddings_For_Second_Query()
    {
        var (store, id) = await CreateStore();
        var first = await Query(store, id, "cleanup", SearchMode.Semantic);
        var callsAfterBuild = embeddings.Calls;
        var second = await Query(store, id, "stop background work", SearchMode.Semantic);
        embeddings.Calls.ShouldBe(callsAfterBuild + 1);
        second.Hits.Select(hit => hit.ChunkId).ShouldBe(first.Hits.Select(hit => hit.ChunkId));
        Directory.GetFiles(runtime.CachePath, "*.db").Length.ShouldBe(1);
    }

    [Fact]
    public async Task Should_Invalidate_Cache_When_Package_Content_Changes()
    {
        var (store, id) = await CreateStore();
        await Query(store, id, "cleanup", SearchMode.Semantic);
        var previousCalls = embeddings.Calls;
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(Path.Combine(root, "docs", "lifecycle.md"), "# Lifecycle\n\nCancel subscriptions during component cleanup.", TestContext.Current.CancellationToken);
        await BuildAndSave(store);
        var response = await Query(store, id, "stop background work", SearchMode.Semantic);
        response.Hits.ShouldHaveSingleItem().Content.ShouldContain("Cancel subscriptions");
        embeddings.Calls.ShouldBeGreaterThan(previousCalls + 1);
        Directory.GetFiles(runtime.CachePath, "*.db").Length.ShouldBe(2);
    }

    [Fact]
    public async Task Should_Embed_Later_Windows_Of_Long_Sections()
    {
        var (store, id) = await CreateStore();
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(Path.Combine(root, "docs", "lifecycle.md"), "# Lifecycle\n\n" + new string('x', 3000) + "\nCancel timers during cleanup.", TestContext.Current.CancellationToken);
        await BuildAndSave(store);
        var response = await Query(store, id, "stop background work", SearchMode.Semantic);
        response.Hits.ShouldHaveSingleItem().Content.ShouldContain("Cancel timers");
        embeddings.DocumentTexts.ShouldContain(text => text.Contains("Cancel timers"));
        embeddings.DocumentTexts.ShouldAllBe(text => text.Length <= 1400);
    }

    [Fact]
    public async Task Should_Report_Missing_Setup_Instead_Of_Silent_Fallback()
    {
        var (store, id) = await CreateStore();
        var noProvider = new SqlitePackageStore(new PackageStoreOptions(Path.Combine(root, "packages")), NullLogger<SqlitePackageStore>.Instance);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => Query(noProvider, id, "cleanup", SearchMode.Hybrid));
        error.Message.ShouldContain("provider install onnx");
        embeddings.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Should_Keep_Lexical_Override_When_Configured_Mode_Is_Hybrid()
    {
        var (store, id) = await CreateStore();
        Directory.CreateDirectory(runtime.RootPath);
        await File.WriteAllTextAsync(Path.Combine(runtime.RootPath, "settings.json"), "{\"Mode\":\"Hybrid\",\"Model\":\"bge-micro-v2\"}", TestContext.Current.CancellationToken);
        (await Query(store, id, "cleanup", SearchMode.Lexical)).Hits.ShouldHaveSingleItem();
        embeddings.Calls.ShouldBe(0);
        (await Query(store, id, "stop background work", null)).Hits.ShouldHaveSingleItem().Path.ShouldBe("lifecycle.md");
        embeddings.Calls.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_Expand_An_Exact_Hit_With_Bounded_Adjacent_Context()
    {
        var (store, id) = await CreateStore();
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(
            Path.Combine(root, "docs", "lifecycle.md"),
            "# Lifecycle\n\n## Before\n\nContext before.\n\n## Target\n\nUnique target phrase.\n\n## After\n\nContext after.",
            TestContext.Current.CancellationToken
        );
        await BuildAndSave(store);

        var response = await QueryWithOptions(
            store,
            id,
            "unique target phrase",
            new RetrievalOptions(MaxHits: 3, RelativeScoreCutoff: 0.1, IncludeAdjacentChunks: true)
        );

        response.Hits.Select(hit => hit.SectionTitle).ShouldBe(["Target", "Before", "After"]);
        response.Hits.Select(hit => hit.ChunkId).Distinct().Count().ShouldBe(3);
        response.Hits.ShouldContain(hit => hit.Content.Contains("Context before"));
        response.Hits.ShouldContain(hit => hit.Content.Contains("Context after"));
    }

    [Fact]
    public async Task Should_Expand_Only_Verified_Local_References()
    {
        var (store, id) = await CreateStore();
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(
            Path.Combine(root, "docs", "lifecycle.md"),
            "# Lifecycle\n\nSee [guide](guide.md) and [missing](missing.md).",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            Path.Combine(root, "docs", "guide.md"),
            "# Guide\n\nVerified linked guidance.",
            TestContext.Current.CancellationToken
        );
        await BuildAndSave(store);

        var response = await QueryWithOptions(
            store,
            id,
            "see guide",
            new RetrievalOptions(MaxHits: 2, RelativeScoreCutoff: 0.1, IncludeReferences: true)
        );

        response.Hits.Select(hit => hit.Path).ShouldContain("guide.md");
        response.Hits.Select(hit => hit.Path).ShouldNotContain("missing.md");
        response.Hits.Select(hit => hit.ChunkId).Distinct().Count().ShouldBe(response.Hits.Count);
    }

    [Fact]
    public void Should_Boost_Consensus_Without_Comparing_Raw_Scores()
    {
        var first = Hit("first", 1000);
        var consensus = Hit("consensus", 0.1);
        var semantic = Hit("semantic", 0.9);
        var fused = SemanticSearchIndex.Fuse([first, consensus], [semantic, consensus]);
        fused.Select(hit => hit.ChunkId).ShouldBe(["consensus", "first", "semantic"]);
        fused[0].Score.ShouldBe(2d / 62, 0.0000001);
        fused.Count.ShouldBe(3);
    }

    private static DocsQueryHit Hit(string id, double score) => new(id, "section", "body", 10, false, score, "doc.md", id);

    private async Task<(SqlitePackageStore Store, string Id)> CreateStore()
    {
        var docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        await File.WriteAllTextAsync(Path.Combine(docs, "lifecycle.md"), "# Lifecycle\n\nCancel timers during component cleanup.", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(docs, "routing.md"), "# Routing\n\nNavigate between application pages.", TestContext.Current.CancellationToken);
        var store = new SqlitePackageStore(new PackageStoreOptions(Path.Combine(root, "packages")), NullLogger<SqlitePackageStore>.Instance, runtime, new SemanticSearchIndex(runtime, embeddings));
        var id = await BuildAndSave(store);
        return (store, id);
    }

    private async Task<string> BuildAndSave(SqlitePackageStore store)
    {
        var builder = new DocumentPackageBuilder(new SourceDetector(), new TestHttpClientFactory(), NullLogger<DocumentPackageBuilder>.Instance);
        var result = await builder.BuildAsync(Path.Combine(root, "docs"), cancellationToken: TestContext.Current.CancellationToken);
        await store.SaveAsync(result, TestContext.Current.CancellationToken);
        return result.Manifest.PackageId;
    }

    private static Task<DocsQueryResponse> Query(
        SqlitePackageStore store,
        string id,
        string topic,
        SearchMode? mode
    ) => QueryWithOptions(store, id, topic, new RetrievalOptions(SearchMode: mode));

    private static Task<DocsQueryResponse> QueryWithOptions(
        SqlitePackageStore store,
        string id,
        string topic,
        RetrievalOptions options
    ) => store.QueryAsync(new(id, topic, options), TestContext.Current.CancellationToken);

    public void Dispose()
    {
        runtime.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class FakeEmbeddingProvider : ISemanticEmbeddingProvider
    {
        public int Calls { get; private set; }
        public List<string> DocumentTexts { get; } = [];
        public Task<float[][]> EmbedAsync(SemanticModelProfile model, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (texts.Count > 1) DocumentTexts.AddRange(texts);
            return Task.FromResult(texts.Select(text =>
            {
                var vector = new float[model.Dimensions];
                vector[text.Contains("Cancel", StringComparison.OrdinalIgnoreCase) || text.Contains("cleanup") || text.Contains("stop background") ? 0 : 1] = 1;
                return vector;
            }).ToArray());
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Local fixtures must not make HTTP requests.");
    }
}