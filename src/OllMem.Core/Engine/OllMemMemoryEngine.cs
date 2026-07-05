using System.Text;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;

namespace OllMem.Core.Engine;

/// <summary>
/// Default <see cref="IMemoryEngine"/> wiring the hybrid datastore, extraction,
/// encryption, audit log and time-aware reranking into the high-level
/// remember / recall surface described across the PRD.
/// </summary>
public sealed class OllMemMemoryEngine : IMemoryEngine
{
    // Pull a few extra candidates before reranking so a fresh-but-slightly-less
    // similar memory can overtake a stale-but-more-similar one (PRD REQ-4.2).
    private const int CandidateMultiplier = 4;

    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _vectors;
    private readonly IGraphStore _graph;
    private readonly IBlobStore _blobs;
    private readonly IPayloadCipher _cipher;
    private readonly IDecayScorer _decay;
    private readonly IFactExtractor _extractor;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;

    public OllMemMemoryEngine(
        IEmbeddingService embeddings,
        IVectorStore vectors,
        IGraphStore graph,
        IBlobStore blobs,
        IPayloadCipher cipher,
        IDecayScorer decay,
        IFactExtractor extractor,
        IAuditLog audit,
        TimeProvider? clock = null)
    {
        _embeddings = embeddings;
        _vectors = vectors;
        _graph = graph;
        _blobs = blobs;
        _cipher = cipher;
        _decay = decay;
        _extractor = extractor;
        _audit = audit;
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<RememberResult> RememberAsync(
        Namespace ns,
        string statement,
        GraphEdge? edge = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(statement))
            throw new ArgumentException("Statement must not be empty.", nameof(statement));

        var resolvedEdge = edge ?? GraphEdge.Create("user", "note", statement);
        return await StoreFactAsync(ns, statement.Trim(), resolvedEdge, ct);
    }

    public async ValueTask<IReadOnlyList<RememberResult>> ExtractAndStoreAsync(
        Namespace ns,
        string transcript,
        CancellationToken ct = default)
    {
        var facts = await _extractor.ExtractAsync(transcript, ct);
        var results = new List<RememberResult>(facts.Count);
        foreach (var fact in facts)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await StoreFactAsync(ns, fact.Statement, fact.Edge, ct));
        }

        return results;
    }

    private async ValueTask<RememberResult> StoreFactAsync(
        Namespace ns,
        string statement,
        GraphEdge edge,
        CancellationToken ct)
    {
        // --- Conflict resolution (PRD REQ-2.2) ---------------------------------
        Guid? supersededId = null;
        var existingId = await _graph.FindByRelationAsync(ns, edge.Subject, edge.Predicate, ct);
        if (existingId is { } prevId)
        {
            var prev = await _vectors.GetAsync(prevId, ct);
            if (prev is { Status: MemoryStatus.Active })
            {
                if (string.Equals(prev.Edge.Object, edge.Object, StringComparison.OrdinalIgnoreCase))
                {
                    // Identical assertion: refresh recency instead of duplicating.
                    Touch(prev);
                    await _vectors.UpsertAsync(prev, ct);
                    await _audit.AppendAsync(AuditAction.Access, ns, prev.Id, "duplicate fact refreshed", ct);
                    return new RememberResult(prev, null);
                }

                // Contradiction: supersede the old state, keep it for history.
                prev.Status = MemoryStatus.Superseded;
                await _vectors.UpsertAsync(prev, ct);
                await _graph.RemoveAsync(ns, prev.Id, ct);
                supersededId = prev.Id;
            }
        }

        // --- Persist the new memory -------------------------------------------
        var embedding = await _embeddings.EmbedAsync(statement, ct);

        // Encrypt the raw payload before it touches the blob layer (PRD REQ-2.3/3.2).
        var encrypted = _cipher.Encrypt(ns.Key, Encoding.UTF8.GetBytes(statement));
        var blobRef = await _blobs.PutAsync(encrypted, ct);

        var node = new MemoryNode
        {
            Id = Guid.NewGuid(),
            Namespace = ns,
            Statement = statement,
            Edge = edge,
            Embedding = embedding,
            BlobRef = blobRef,
            CreatedAt = _clock.GetUtcNow(),
            LastAccessedAt = _clock.GetUtcNow(),
        };

        await _vectors.UpsertAsync(node, ct);
        await _graph.AddAsync(ns, node, ct);
        await _audit.AppendAsync(AuditAction.Create, ns, node.Id, node.Statement, ct);

        if (supersededId is { } sid)
        {
            node.Status = MemoryStatus.Active;
            await _vectors.UpsertAsync(node, ct);
            // Link old -> new for traceability.
            var old = await _vectors.GetAsync(sid, ct);
            if (old is not null)
            {
                old.SupersededBy = node.Id;
                await _vectors.UpsertAsync(old, ct);
            }

            await _audit.AppendAsync(AuditAction.Supersede, ns, sid, $"superseded by {node.Id}", ct);
        }

        return new RememberResult(node, supersededId);
    }

    public async ValueTask<IReadOnlyList<ScoredMemory>> RecallAsync(
        Namespace ns,
        string query,
        int topK = 5,
        CancellationToken ct = default)
    {
        if (topK <= 0)
            return [];

        var queryEmbedding = await _embeddings.EmbedAsync(query, ct);
        var candidates = await _vectors.SearchAsync(ns, queryEmbedding, topK * CandidateMultiplier, ct);

        var now = _clock.GetUtcNow();
        var scored = new List<ScoredMemory>(candidates.Count);
        foreach (var (node, similarity) in candidates)
        {
            var weight = _decay.Weight(node, now);
            scored.Add(new ScoredMemory(node, similarity, weight, similarity * weight));
        }

        // Time-aware reranking: similarity × recency/usage weight (PRD REQ-4.2).
        var ranked = scored
            .OrderByDescending(s => s.FinalScore)
            .ThenByDescending(s => s.Node.LastAccessedAt)
            .Take(topK)
            .ToList();

        // Recall counts as access: update stats feeding the decay model.
        foreach (var item in ranked)
        {
            Touch(item.Node);
            await _vectors.UpsertAsync(item.Node, ct);
            await _audit.AppendAsync(AuditAction.Access, ns, item.Node.Id, "recalled", ct);
        }

        return ranked;
    }

    public ValueTask<IReadOnlyList<GraphEdge>> NeighborsAsync(
        Namespace ns,
        string subject,
        string? predicate = null,
        CancellationToken ct = default) =>
        _graph.NeighborsAsync(ns, subject, predicate, ct);

    private void Touch(MemoryNode node)
    {
        node.LastAccessedAt = _clock.GetUtcNow();
        node.AccessCount++;
    }
}
