using System.Globalization;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Embeddings;
using Microsoft.Data.Sqlite;

namespace OllMem.Storage.Sqlite;

/// <summary>
/// SQLite-backed hybrid datastore (PRD REQ-3.1). A single <c>memories</c> table
/// is both the vector index and the knowledge graph: vector search scans the
/// embeddings, while graph queries read the subject/predicate/object columns.
/// Because both views share one row per memory, superseding a fact (flipping its
/// status) removes it from graph and vector results atomically.
/// </summary>
public sealed class SqliteHybridStore : IVectorStore, IGraphStore
{
    private readonly SqliteStoreContext _ctx;

    public SqliteHybridStore(SqliteStoreContext ctx) => _ctx = ctx;

    // ---- IVectorStore -------------------------------------------------------

    public async ValueTask UpsertAsync(MemoryNode node, CancellationToken ct = default)
    {
        await using var conn = _ctx.OpenConnection();
        await UpsertAsync(conn, node, ct);
    }

    public async ValueTask<MemoryNode?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM memories WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async ValueTask<IReadOnlyList<(MemoryNode Node, double Similarity)>> SearchAsync(
        Namespace ns, float[] queryEmbedding, int topK, CancellationToken ct = default)
    {
        var active = await ListActiveAsync(ns, ct);
        return active
            // Rows embedded by a different model (different dimensionality) are
            // in an incompatible vector space; skip them rather than crash.
            .Where(n => n.Embedding.Length == queryEmbedding.Length)
            .Select(n => (Node: n, Similarity: HashingEmbeddingService.CosineSimilarity(queryEmbedding, n.Embedding)))
            .OrderByDescending(x => x.Similarity)
            .Take(Math.Max(0, topK))
            .ToList();
    }

    public async ValueTask<IReadOnlyList<(MemoryNode Node, double Similarity)>> SearchOwnersAsync(
        IReadOnlySet<string> owners, float[] queryEmbedding, int topK, CancellationToken ct = default)
    {
        if (owners.Count == 0)
            return [];

        // Filter in SQL so rows belonging to other owners never get read back
        // and their embeddings never get decoded. lower() keeps the match
        // case-insensitive (the owner column preserves the caller's casing,
        // unlike namespace_key) without needing a schema migration.
        var normalized = owners.Select(o => o.Trim().ToLowerInvariant()).ToList();
        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {Columns} FROM memories " +
            $"WHERE status = 0 AND lower(owner) IN ({string.Join(", ", normalized.Select((_, i) => $"$o{i}"))})";
        for (var i = 0; i < normalized.Count; i++)
            cmd.Parameters.AddWithValue($"$o{i}", normalized[i]);

        var results = new List<(MemoryNode Node, double Similarity)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var node = Map(reader);
            if (node.Embedding.Length != queryEmbedding.Length)
                continue;
            results.Add((node, HashingEmbeddingService.CosineSimilarity(queryEmbedding, node.Embedding)));
        }

        return results
            .OrderByDescending(x => x.Similarity)
            .Take(Math.Max(0, topK))
            .ToList();
    }

    public async ValueTask<IReadOnlyList<MemoryNode>> ListActiveAsync(
        Namespace ns, CancellationToken ct = default)
    {
        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {Columns} FROM memories WHERE namespace_key = $ns AND status = 0";
        cmd.Parameters.AddWithValue("$ns", ns.Key);

        var list = new List<MemoryNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(Map(reader));
        return list;
    }

    public async ValueTask<IReadOnlyList<MemoryNode>> ListAllActiveAsync(CancellationToken ct = default)
    {
        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM memories WHERE status = 0";

        var list = new List<MemoryNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(Map(reader));
        return list;
    }

    public async ValueTask<IReadOnlyList<MemoryNode>> ListHistoryAsync(
        Namespace ns, CancellationToken ct = default)
    {
        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM memories WHERE namespace_key = $ns";
        cmd.Parameters.AddWithValue("$ns", ns.Key);

        var list = new List<MemoryNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(Map(reader));
        return list;
    }

    public async ValueTask<IReadOnlyList<MemoryNode>> ListOwnersHistoryAsync(
        IReadOnlySet<string> owners, CancellationToken ct = default)
    {
        if (owners.Count == 0)
            return [];

        // Filter in SQL for the same reason SearchOwnersAsync does: rows
        // belonging to other owners are never read back at all.
        var normalized = owners.Select(o => o.Trim().ToLowerInvariant()).ToList();
        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {Columns} FROM memories " +
            $"WHERE lower(owner) IN ({string.Join(", ", normalized.Select((_, i) => $"$o{i}"))})";
        for (var i = 0; i < normalized.Count; i++)
            cmd.Parameters.AddWithValue($"$o{i}", normalized[i]);

        var list = new List<MemoryNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(Map(reader));
        return list;
    }

    // ---- IGraphStore --------------------------------------------------------

    // The row is written by the vector upsert; the graph view is the same table,
    // so AddAsync is an idempotent upsert and keeps the two views consistent.
    public ValueTask AddAsync(Namespace ns, MemoryNode node, CancellationToken ct = default) =>
        UpsertAsync(node, ct);

    // Superseding flips status to 1, which graph queries already exclude, so the
    // edge "disappears" without losing the row (history is preserved, REQ-4.1).
    public ValueTask RemoveAsync(Namespace ns, Guid nodeId, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public async ValueTask<IReadOnlyList<GraphEdge>> NeighborsAsync(
        Namespace ns, string subject, string? predicate = null, CancellationToken ct = default)
    {
        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT subject, predicate, object FROM memories " +
            "WHERE namespace_key = $ns AND subject = $subject AND status = 0" +
            (predicate is null ? "" : " AND predicate = $predicate");
        cmd.Parameters.AddWithValue("$ns", ns.Key);
        cmd.Parameters.AddWithValue("$subject", subject.Trim().ToLowerInvariant());
        if (predicate is not null)
            cmd.Parameters.AddWithValue("$predicate", predicate.Trim().ToLowerInvariant());

        var edges = new List<GraphEdge>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            edges.Add(new GraphEdge(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return edges;
    }

    public async ValueTask<Guid?> FindByRelationAsync(
        Namespace ns, string subject, string predicate, CancellationToken ct = default)
    {
        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id FROM memories " +
            "WHERE namespace_key = $ns AND subject = $subject AND predicate = $predicate AND status = 0 " +
            "LIMIT 1";
        cmd.Parameters.AddWithValue("$ns", ns.Key);
        cmd.Parameters.AddWithValue("$subject", subject.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$predicate", predicate.Trim().ToLowerInvariant());

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? Guid.Parse(s) : null;
    }

    // ---- helpers ------------------------------------------------------------

    private const string Columns =
        "id, namespace_key, owner, scope, scope_id, statement, subject, predicate, object, " +
        "embedding, blob_ref, created_at, last_accessed_at, access_count, status, superseded_by, retired_at";

    private static async ValueTask UpsertAsync(SqliteConnection conn, MemoryNode node, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO memories
                (id, namespace_key, owner, scope, scope_id, statement, subject, predicate, object,
                 embedding, blob_ref, created_at, last_accessed_at, access_count, status, superseded_by,
                 retired_at)
            VALUES
                ($id, $ns, $owner, $scope, $scopeId, $statement, $subject, $predicate, $object,
                 $embedding, $blobRef, $createdAt, $lastAccessedAt, $accessCount, $status, $supersededBy,
                 $retiredAt)
            ON CONFLICT(id) DO UPDATE SET
                statement = excluded.statement,
                subject = excluded.subject,
                predicate = excluded.predicate,
                object = excluded.object,
                embedding = excluded.embedding,
                blob_ref = excluded.blob_ref,
                last_accessed_at = excluded.last_accessed_at,
                access_count = excluded.access_count,
                status = excluded.status,
                superseded_by = excluded.superseded_by,
                retired_at = excluded.retired_at;
            """;
        cmd.Parameters.AddWithValue("$id", node.Id.ToString());
        cmd.Parameters.AddWithValue("$ns", node.Namespace.Key);
        cmd.Parameters.AddWithValue("$owner", node.Namespace.Owner);
        cmd.Parameters.AddWithValue("$scope", (int)node.Namespace.Scope);
        cmd.Parameters.AddWithValue("$scopeId", node.Namespace.ScopeId);
        cmd.Parameters.AddWithValue("$statement", node.Statement);
        cmd.Parameters.AddWithValue("$subject", node.Edge.Subject);
        cmd.Parameters.AddWithValue("$predicate", node.Edge.Predicate);
        cmd.Parameters.AddWithValue("$object", node.Edge.Object);
        cmd.Parameters.AddWithValue("$embedding", EmbeddingCodec.ToBytes(node.Embedding));
        cmd.Parameters.AddWithValue("$blobRef", (object?)node.BlobRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", Iso(node.CreatedAt));
        cmd.Parameters.AddWithValue("$lastAccessedAt", Iso(node.LastAccessedAt));
        cmd.Parameters.AddWithValue("$accessCount", node.AccessCount);
        cmd.Parameters.AddWithValue("$status", (int)node.Status);
        cmd.Parameters.AddWithValue("$supersededBy", (object?)node.SupersededBy?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$retiredAt", node.RetiredAt is { } retired ? Iso(retired) : (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static MemoryNode Map(SqliteDataReader r)
    {
        var ns = new Namespace(r.GetString(2), (MemoryScope)r.GetInt32(3), r.GetString(4));
        return new MemoryNode
        {
            Id = Guid.Parse(r.GetString(0)),
            Namespace = ns,
            Statement = r.GetString(5),
            Edge = new GraphEdge(r.GetString(6), r.GetString(7), r.GetString(8)),
            Embedding = EmbeddingCodec.FromBytes((byte[])r["embedding"]),
            BlobRef = r.IsDBNull(10) ? null : r.GetString(10),
            CreatedAt = ParseIso(r.GetString(11)),
            LastAccessedAt = ParseIso(r.GetString(12)),
            AccessCount = r.GetInt32(13),
            Status = (MemoryStatus)r.GetInt32(14),
            SupersededBy = r.IsDBNull(15) ? null : Guid.Parse(r.GetString(15)),
            RetiredAt = r.IsDBNull(16) ? null : ParseIso(r.GetString(16)),
        };
    }

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
