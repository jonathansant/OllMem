using Google.Cloud.Firestore;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Embeddings;

namespace OllMem.Storage.Firestore;

/// <summary>
/// Firestore-backed hybrid datastore (PRD REQ-3.1). A single
/// <c>{prefix}_memories</c> collection is both the vector index and the
/// knowledge graph: vector search scans the embeddings, while graph queries
/// filter on the subject/predicate/object fields. Because both views share one
/// document per memory, superseding a fact (flipping its status) removes it
/// from graph and vector results atomically. All queries use equality filters
/// only, so no composite Firestore indexes have to be provisioned.
/// </summary>
public sealed class FirestoreHybridStore : IVectorStore, IGraphStore
{
    private readonly FirestoreStoreContext _ctx;

    public FirestoreHybridStore(FirestoreStoreContext ctx) => _ctx = ctx;

    // ---- IVectorStore -------------------------------------------------------

    public async ValueTask UpsertAsync(MemoryNode node, CancellationToken ct = default) =>
        await _ctx.Memories.Document(node.Id.ToString())
            .SetAsync(MemoryNodeDocument.From(node), SetOptions.Overwrite, ct);

    public async ValueTask<MemoryNode?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var snapshot = await _ctx.Memories.Document(id.ToString()).GetSnapshotAsync(ct);
        return snapshot.Exists ? snapshot.ConvertTo<MemoryNodeDocument>().ToNode(id) : null;
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

    // Firestore caps the value list of an "in" filter; owner sets are tiny in
    // practice (one identity's machines), so chunking is a safety net.
    private const int InFilterChunkSize = 30;

    public async ValueTask<IReadOnlyList<(MemoryNode Node, double Similarity)>> SearchOwnersAsync(
        IReadOnlySet<string> owners, float[] queryEmbedding, int topK, CancellationToken ct = default)
    {
        if (owners.Count == 0)
            return [];

        // Owners are stored lower-cased (MemoryNodeDocument.From). Documents
        // written before that normalisation kept the caller's casing, so query
        // both spellings rather than silently missing pre-existing memories.
        var candidates = owners
            .SelectMany(o => new[] { o.Trim(), o.Trim().ToLowerInvariant() })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var results = new List<(MemoryNode Node, double Similarity)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i += InFilterChunkSize)
        {
            var chunk = candidates.Skip(i).Take(InFilterChunkSize).ToList();
            var snapshot = await _ctx.Memories
                .WhereIn("owner", chunk)
                .WhereEqualTo("status", (int)MemoryStatus.Active)
                .GetSnapshotAsync(ct);

            foreach (var doc in snapshot.Documents)
            {
                // Chunks may overlap on casing variants of the same owner.
                if (!seen.Add(doc.Id))
                    continue;
                var node = ToNode(doc);
                if (node.Embedding.Length != queryEmbedding.Length)
                    continue;
                results.Add((node, HashingEmbeddingService.CosineSimilarity(queryEmbedding, node.Embedding)));
            }
        }

        return results
            .OrderByDescending(x => x.Similarity)
            .Take(Math.Max(0, topK))
            .ToList();
    }

    public async ValueTask<IReadOnlyList<MemoryNode>> ListActiveAsync(
        Namespace ns, CancellationToken ct = default)
    {
        var snapshot = await _ctx.Memories
            .WhereEqualTo("namespaceKey", ns.Key)
            .WhereEqualTo("status", (int)MemoryStatus.Active)
            .GetSnapshotAsync(ct);
        return snapshot.Documents.Select(ToNode).ToList();
    }

    public async ValueTask<IReadOnlyList<MemoryNode>> ListAllActiveAsync(CancellationToken ct = default)
    {
        var snapshot = await _ctx.Memories
            .WhereEqualTo("status", (int)MemoryStatus.Active)
            .GetSnapshotAsync(ct);
        return snapshot.Documents.Select(ToNode).ToList();
    }

    // ---- IGraphStore --------------------------------------------------------

    // The document is written by the vector upsert; the graph view is the same
    // collection, so AddAsync is an idempotent upsert keeping both views consistent.
    public ValueTask AddAsync(Namespace ns, MemoryNode node, CancellationToken ct = default) =>
        UpsertAsync(node, ct);

    // Superseding flips status to 1, which graph queries already exclude, so the
    // edge "disappears" without losing the document (history is preserved, REQ-4.1).
    public ValueTask RemoveAsync(Namespace ns, Guid nodeId, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public async ValueTask<IReadOnlyList<GraphEdge>> NeighborsAsync(
        Namespace ns, string subject, string? predicate = null, CancellationToken ct = default)
    {
        Query query = _ctx.Memories
            .WhereEqualTo("namespaceKey", ns.Key)
            .WhereEqualTo("subject", subject.Trim().ToLowerInvariant())
            .WhereEqualTo("status", (int)MemoryStatus.Active);
        if (predicate is not null)
            query = query.WhereEqualTo("predicate", predicate.Trim().ToLowerInvariant());

        var snapshot = await query.GetSnapshotAsync(ct);
        return snapshot.Documents
            .Select(d => new GraphEdge(
                d.GetValue<string>("subject"), d.GetValue<string>("predicate"), d.GetValue<string>("object")))
            .ToList();
    }

    public async ValueTask<Guid?> FindByRelationAsync(
        Namespace ns, string subject, string predicate, CancellationToken ct = default)
    {
        var snapshot = await _ctx.Memories
            .WhereEqualTo("namespaceKey", ns.Key)
            .WhereEqualTo("subject", subject.Trim().ToLowerInvariant())
            .WhereEqualTo("predicate", predicate.Trim().ToLowerInvariant())
            .WhereEqualTo("status", (int)MemoryStatus.Active)
            .Limit(1)
            .GetSnapshotAsync(ct);
        return snapshot.Count == 0 ? null : Guid.Parse(snapshot.Documents[0].Id);
    }

    private static MemoryNode ToNode(DocumentSnapshot doc) =>
        doc.ConvertTo<MemoryNodeDocument>().ToNode(Guid.Parse(doc.Id));
}
