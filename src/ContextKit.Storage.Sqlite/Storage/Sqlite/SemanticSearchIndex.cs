using System.Security.Cryptography;
using System.Text;
using GroundKit.Core.Contracts;
using GroundKit.Semantic;
using Microsoft.Data.Sqlite;

namespace GroundKit.Storage.Sqlite;

public sealed class SemanticSearchIndex(ISemanticSearchContext runtime, ISemanticEmbeddingProvider provider)
{
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    public async Task<IReadOnlyList<DocsQueryHit>> SearchAsync(
        string packagePath,
        string topic,
        int maxCandidates,
        CancellationToken cancellationToken = default
    )
    {
        var model = runtime.Model;
        var queryVectors = await provider.EmbedAsync(model, [topic], cancellationToken);
        var query = Normalize(queryVectors.Single(), model.Dimensions);
        var packageHash = await HashFileAsync(packagePath, cancellationToken);
        var chunks = await ReadChunksAsync(packagePath, cancellationToken);
        if (await HashFileAsync(packagePath, cancellationToken) != packageHash)
            throw new InvalidOperationException("Package changed while reading chunks. Retry semantic query.");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packageHash + model.Identity)));
        Directory.CreateDirectory(runtime.CachePath);
        var indexPath = Path.Combine(runtime.CachePath, key + ".db");
        await _buildGate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(indexPath)) await BuildAsync(indexPath, packagePath, packageHash, chunks, model, cancellationToken);
        }
        finally { _buildGate.Release(); }

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        await using var connection = OpenConnection(indexPath, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk_id, vector FROM embeddings ORDER BY chunk_id, window_index";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var chunkId = reader.GetString(0);
            var bytes = (byte[])reader.GetValue(1);
            if (bytes.Length != model.Dimensions * sizeof(float)) throw new InvalidDataException("Embedding cache dimensions mismatch.");
            using var binary = new BinaryReader(new MemoryStream(bytes));
            double score = 0;
            for (var dimension = 0; dimension < query.Length; dimension++) score += query[dimension] * binary.ReadSingle();
            if (!double.IsFinite(score)) throw new InvalidDataException("Embedding cache contains non-finite values.");
            if (!scores.TryGetValue(chunkId, out var existing) || score > existing) scores[chunkId] = score;
        }
        return chunks.Where(hit => scores.ContainsKey(hit.ChunkId!))
            .Select(hit => hit with { Score = scores[hit.ChunkId!] })
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.ChunkId, StringComparer.Ordinal)
            .Take(maxCandidates)
            .ToList();
    }

    private async Task BuildAsync(
        string indexPath,
        string packagePath,
        string packageHash,
        IReadOnlyList<DocsQueryHit> chunks,
        SemanticModelProfile model,
        CancellationToken cancellationToken
    )
    {
        var temporary = indexPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var connection = OpenConnection(temporary, SqliteOpenMode.ReadWriteCreate))
            {
                await connection.OpenAsync(cancellationToken);
                await using var schema = connection.CreateCommand();
                schema.CommandText = "CREATE TABLE embeddings(chunk_id TEXT NOT NULL, window_index INTEGER NOT NULL, vector BLOB NOT NULL, PRIMARY KEY(chunk_id, window_index));";
                await schema.ExecuteNonQueryAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                var windows = chunks.SelectMany(hit => Windows(hit).Select((text, index) => (Hit: hit, Text: text, Index: index)));
                foreach (var batch in windows.Chunk(32))
                {
                    var vectors = await provider.EmbedAsync(model, batch.Select(window => window.Text).ToArray(), cancellationToken);
                    if (vectors.Length != batch.Length) throw new InvalidDataException("Embedding count mismatch.");
                    for (var index = 0; index < batch.Length; index++)
                    {
                        var vector = Normalize(vectors[index], model.Dimensions);
                        using var memory = new MemoryStream();
                        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
                            foreach (var value in vector) writer.Write(value);
                        await using var insert = connection.CreateCommand();
                        insert.Transaction = (SqliteTransaction)transaction;
                        insert.CommandText = "INSERT INTO embeddings VALUES ($chunk, $window, $vector)";
                        insert.Parameters.AddWithValue("$chunk", batch[index].Hit.ChunkId!);
                        insert.Parameters.AddWithValue("$window", batch[index].Index);
                        insert.Parameters.AddWithValue("$vector", memory.ToArray());
                        await insert.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
                if (await HashFileAsync(packagePath, cancellationToken) != packageHash)
                    throw new InvalidOperationException("Package changed while indexing. Retry semantic query.");
                await transaction.CommitAsync(cancellationToken);
            }
            try { File.Move(temporary, indexPath); }
            catch (IOException) when (File.Exists(indexPath)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IEnumerable<string> Windows(DocsQueryHit hit)
    {
        var prefix = hit.DocumentTitle + "\n" + hit.SectionTitle + "\n";
        const int windowLength = 1200;
        const int stride = 1000;
        for (var offset = 0; offset < hit.Content.Length; offset += stride)
        {
            var count = Math.Min(windowLength, hit.Content.Length - offset);
            yield return prefix[..Math.Min(prefix.Length, 200)] + hit.Content.Substring(offset, count);
            if (offset + count >= hit.Content.Length) break;
        }
    }

    private static float[] Normalize(float[] vector, int dimensions)
    {
        if (vector.Length != dimensions || vector.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Invalid embedding vector.");
        var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
        if (norm <= 0) throw new InvalidDataException("Embedding vector has zero length.");
        return vector.Select(value => (float)(value / norm)).ToArray();
    }

    private static async Task<List<DocsQueryHit>> ReadChunksAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_title, section_title, content, token_estimate, has_code, path, chunk_id FROM chunks ORDER BY path, sequence";
        var hits = new List<DocsQueryHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            hits.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4), 0, reader.GetString(5), reader.GetString(6)));
        return hits;
    }

    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Pooling = false,
    }.ToString());

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    public static IReadOnlyList<DocsQueryHit> Fuse(IReadOnlyList<DocsQueryHit> lexical, IReadOnlyList<DocsQueryHit> semantic)
    {
        var hits = new Dictionary<string, (DocsQueryHit Hit, double Score)>(StringComparer.Ordinal);
        foreach (var ranking in new[] { lexical, semantic })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < ranking.Count; index++)
            {
                var hit = ranking[index];
                var id = hit.ChunkId ?? throw new InvalidDataException("Hybrid candidate lacks chunk identity.");
                if (!seen.Add(id)) continue;
                var score = 1d / (60 + index + 1);
                hits[id] = hits.TryGetValue(id, out var previous)
                    ? (previous.Hit, previous.Score + score)
                    : (hit, score);
            }
        }
        return hits.Values.OrderByDescending(value => value.Score)
            .ThenBy(value => value.Hit.ChunkId, StringComparer.Ordinal)
            .Select(value => value.Hit with { Score = value.Score }).ToList();
    }
}