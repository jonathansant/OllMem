using System.Text;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Embeddings;
using OllMem.Core.Reconciliation;

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

    // Minimum raw similarity for a memory from outside the caller's own
    // namespace to be considered at all.
    private const double MinBroadenedSimilarity = 0.15;

    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _vectors;
    private readonly IGraphStore _graph;
    private readonly IBlobStore _blobs;
    private readonly IPayloadCipher _cipher;
    private readonly IDecayScorer _decay;
    private readonly IFactExtractor _extractor;
    private readonly IAuditLog _audit;
    private readonly IOwnerLinkStore _ownerLinks;
    private readonly IFactReconciler _reconciler;
    private readonly IMemoryConsolidator _consolidator;
    private readonly ReconciliationOptions _reconciliation;
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
        TimeProvider? clock = null,
        IOwnerLinkStore? ownerLinks = null,
        IFactReconciler? reconciler = null,
        IMemoryConsolidator? consolidator = null,
        ReconciliationOptions? reconciliation = null)
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
        _ownerLinks = ownerLinks ?? new Storage.InMemoryOwnerLinkStore();
        _reconciliation = reconciliation ?? new ReconciliationOptions();
        _reconciler = reconciler ?? new ThresholdFactReconciler(_reconciliation);
        _consolidator = consolidator ?? new Consolidation.PassThroughConsolidator();
    }

    // ---- Write path ---------------------------------------------------------

    public async ValueTask<RememberResult> RememberAsync(
        Namespace ns,
        string statement,
        GraphEdge? edge = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(statement))
            throw new ArgumentException("Statement must not be empty.", nameof(statement));

        var trimmed = statement.Trim();

        // Without a caller-supplied triple there is no relation to reason
        // about, so the edge is synthetic and conflict detection has to be
        // semantic (see StoreFactAsync).
        var resolvedEdge = edge ?? GraphEdge.Create("user", "note", trimmed);
        return await StoreFactAsync(ns, trimmed, resolvedEdge, edge is not null, ct);
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
            results.Add(await StoreFactAsync(ns, fact.Statement, fact.Edge, true, ct));
        }

        return results;
    }

    /// <param name="explicitRelation">
    /// Whether <paramref name="edge"/> is a relation the caller actually
    /// asserted. A synthetic note edge must not take part in
    /// subject+predicate conflict resolution: every untyped memory in a
    /// namespace shares the predicate "note", so matching on it would make
    /// each new note retire the one before it.
    /// </param>
    private async ValueTask<RememberResult> StoreFactAsync(
        Namespace ns,
        string statement,
        GraphEdge edge,
        bool explicitRelation,
        CancellationToken ct)
    {
        // Needed both to persist the node and to find semantically close
        // memories below, so it is computed once up front.
        var embedding = await _embeddings.EmbedAsync(statement, ct);

        MemoryNode? superseded = null;
        var supersedeReason = "same subject and predicate, new value";

        // --- Conflict resolution 1: exact relation (PRD REQ-2.2) --------------
        if (explicitRelation)
        {
            var existingId = await _graph.FindByRelationAsync(ns, edge.Subject, edge.Predicate, ct);
            if (existingId is { } prevId)
            {
                var prev = await _vectors.GetAsync(prevId, ct);
                if (prev is { Status: MemoryStatus.Active })
                {
                    if (string.Equals(prev.Edge.Object, edge.Object, StringComparison.OrdinalIgnoreCase))
                    {
                        // Identical assertion: refresh recency instead of duplicating.
                        return await RefreshAsync(ns, prev, "duplicate fact refreshed", ct);
                    }

                    superseded = prev;
                }
            }
        }

        // --- Conflict resolution 2: semantic ----------------------------------
        // Catches what the relation check cannot: a paraphrase of an existing
        // fact, and a contradiction expressed through a different predicate.
        if (superseded is null)
        {
            var candidates = await _vectors.SearchAsync(ns, embedding, _reconciliation.MaxCandidates, ct);
            if (candidates.Count > 0)
            {
                var verdict = await _reconciler.ReconcileAsync(statement, edge, candidates, ct);
                var target = verdict.TargetId is { } targetId
                    ? candidates.Select(c => c.Node).FirstOrDefault(n => n.Id == targetId)
                    : null;

                if (target is { Status: MemoryStatus.Active })
                {
                    switch (verdict.Kind)
                    {
                        case ReconcileKind.Duplicate:
                            return await RefreshAsync(
                                ns, target, $"semantic duplicate refreshed ({verdict.Reason})", ct);

                        case ReconcileKind.Supersedes:
                            superseded = target;
                            supersedeReason = $"semantic: {verdict.Reason}";
                            break;
                    }
                }
            }
        }

        if (superseded is not null)
        {
            // Retire the old state now so it leaves graph and vector results;
            // the link to its replacement is added once that node has an id.
            Retire(superseded, MemoryStatus.Superseded, successor: null);
            await _vectors.UpsertAsync(superseded, ct);
            await _graph.RemoveAsync(ns, superseded.Id, ct);
        }

        // --- Persist the new memory -------------------------------------------
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

        if (superseded is null)
            return new RememberResult(node, null, RememberOutcome.Created);

        // Link old -> new for traceability.
        superseded.SupersededBy = node.Id;
        await _vectors.UpsertAsync(superseded, ct);
        await _audit.AppendAsync(
            AuditAction.Supersede, ns, superseded.Id, $"superseded by {node.Id} ({supersedeReason})", ct);

        return new RememberResult(node, superseded.Id, RememberOutcome.Superseded);
    }

    private async ValueTask<RememberResult> RefreshAsync(
        Namespace ns, MemoryNode existing, string detail, CancellationToken ct)
    {
        Touch(existing);
        await _vectors.UpsertAsync(existing, ct);
        await _audit.AppendAsync(AuditAction.Access, ns, existing.Id, detail, ct);
        return new RememberResult(existing, null, RememberOutcome.Refreshed);
    }

    // ---- Read path ----------------------------------------------------------

    public async ValueTask<IReadOnlyList<ScoredMemory>> RecallAsync(
        Namespace ns,
        string query,
        int topK = 5,
        DateTimeOffset? asOf = null,
        CancellationToken ct = default)
    {
        if (topK <= 0)
            return [];

        var queryEmbedding = await _embeddings.EmbedAsync(query, ct);
        var candidateCount = topK * CandidateMultiplier;

        // Broaden beyond the exact namespace: a fact stored under a sibling
        // scopeId, a different scope tier, or an owner explicitly linked as
        // the same identity (IOwnerLinkStore — e.g. the same person's other
        // machine) can still be the right answer. Owners are never broadened
        // into implicitly: namespaces are cryptographically compartmentalized
        // per owner (PRD REQ-1.3), so crossing that boundary requires an
        // explicit link the caller created.
        var linkedOwners = await _ownerLinks.GetLinkedOwnersAsync(ns.Owner, ct);

        IReadOnlyList<(MemoryNode Node, double Similarity)> exact;
        IReadOnlyList<(MemoryNode Node, double Similarity)> broadened;
        if (asOf is { } at)
        {
            // Historical reads have to see retired facts, which the stores'
            // search methods filter out, so the ranking happens here over the
            // full history instead.
            exact = await SearchHistoryAsync(
                await _vectors.ListHistoryAsync(ns, ct), queryEmbedding, candidateCount, at, ct);
            broadened = await SearchHistoryAsync(
                await _vectors.ListOwnersHistoryAsync(linkedOwners, ct), queryEmbedding, candidateCount, at, ct);
        }
        else
        {
            // The caller's own namespace is always fully considered,
            // independently of how it competes with the broadened pool.
            exact = await _vectors.SearchAsync(ns, queryEmbedding, candidateCount, ct);
            broadened = await _vectors.SearchOwnersAsync(linkedOwners, queryEmbedding, candidateCount, ct);
        }

        var seen = new HashSet<Guid>(exact.Count + broadened.Count);

        // Decay is evaluated at the viewpoint: judging a historical answer by
        // today's clock would fade every fact by the elapsed time.
        var now = asOf ?? _clock.GetUtcNow();
        var scored = new List<ScoredMemory>(exact.Count + broadened.Count);
        foreach (var (node, similarity) in exact.Concat(broadened))
        {
            // The broadened pool overlaps the exact one; keep the first (exact)
            // reading of each node so a memory is never scored twice.
            if (!seen.Add(node.Id))
                continue;

            var affinity = NamespaceAffinityScorer.Score(ns, node.Namespace, linkedOwners);

            // Outside the caller's own namespace, a weak similarity is noise
            // rather than a plausibly-related fact; don't let namespace breadth
            // drag in irrelevant results.
            if (affinity < NamespaceAffinityScorer.Exact && similarity < MinBroadenedSimilarity)
                continue;

            var weight = _decay.Weight(node, now);
            scored.Add(new ScoredMemory(node, similarity, weight, affinity, similarity * weight * affinity));
        }

        // Time-aware, namespace-aware reranking: similarity × recency/usage
        // weight × namespace affinity (PRD REQ-4.2).
        var ranked = scored
            .OrderByDescending(s => s.FinalScore)
            .ThenByDescending(s => s.Node.LastAccessedAt)
            .Take(topK)
            .ToList();

        foreach (var item in ranked)
        {
            if (asOf is { } viewpoint)
            {
                // A look into the past must not make a fact look freshly used,
                // so the decay inputs are left exactly as they were.
                await _audit.AppendAsync(
                    AuditAction.Access, item.Node.Namespace, item.Node.Id,
                    $"recalled as of {viewpoint:O}", ct);
                continue;
            }

            // Recall counts as access: update stats feeding the decay model.
            Touch(item.Node);
            await _vectors.UpsertAsync(item.Node, ct);
            await _audit.AppendAsync(AuditAction.Access, item.Node.Namespace, item.Node.Id, "recalled", ct);
        }

        return ranked;
    }

    public async ValueTask<IReadOnlyList<GraphEdge>> NeighborsAsync(
        Namespace ns,
        string subject,
        string? predicate = null,
        DateTimeOffset? asOf = null,
        CancellationToken ct = default)
    {
        if (asOf is not { } at)
            return await _graph.NeighborsAsync(ns, subject, predicate, ct);

        var edges = new List<GraphEdge>();
        foreach (var node in await MatchingHistoryAsync(ns, subject, predicate, ct))
        {
            if (await WasValidAtAsync(node, at, ct))
                edges.Add(node.Edge);
        }

        return edges;
    }

    public async ValueTask<IReadOnlyList<MemoryHistoryEntry>> HistoryAsync(
        Namespace ns,
        string subject,
        string? predicate = null,
        CancellationToken ct = default)
    {
        var entries = new List<MemoryHistoryEntry>();
        foreach (var node in (await MatchingHistoryAsync(ns, subject, predicate, ct)).OrderBy(n => n.CreatedAt))
            entries.Add(new MemoryHistoryEntry(node, node.CreatedAt, await ResolveRetiredAtAsync(node, ct)));

        return entries;
    }

    // ---- Consolidation ------------------------------------------------------

    public async ValueTask<ConsolidateResult> ConsolidateAsync(
        Namespace source,
        Namespace target,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        if (source.Scope != MemoryScope.Session)
            throw new ArgumentException("Consolidation drains a session namespace.", nameof(source));
        if (target.Scope == MemoryScope.Session)
            throw new ArgumentException(
                "Consolidation promotes into an agent or user namespace.", nameof(target));
        if (!string.Equals(source.Owner, target.Owner, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source and target must belong to the same owner.", nameof(target));

        var sessionMemories = await _vectors.ListActiveAsync(source, ct);
        if (sessionMemories.Count == 0)
            return new ConsolidateResult([], [], [], dryRun);

        var facts = await _consolidator.ConsolidateAsync(sessionMemories, ct);
        if (dryRun || facts.Count == 0)
            return new ConsolidateResult(facts, [], [], dryRun);

        var promoted = new List<RememberResult>(facts.Count);
        foreach (var fact in facts)
        {
            ct.ThrowIfCancellationRequested();

            // Facts carry a real relation, and the target namespace's own
            // conflict resolution applies — so consolidating twice promotes
            // nothing new.
            promoted.Add(await StoreFactAsync(target, fact.Statement, fact.Edge, true, ct));
        }

        // A session memory only has a single successor to point at when the
        // consolidator passed the facts through one for one; a summary that
        // merged several of them has no such mapping.
        var oneToOne = promoted.Count == sessionMemories.Count;

        var retired = new List<Guid>(sessionMemories.Count);
        for (var i = 0; i < sessionMemories.Count; i++)
        {
            var node = sessionMemories[i];
            Retire(node, MemoryStatus.Consolidated, oneToOne ? promoted[i].Node.Id : null);
            await _vectors.UpsertAsync(node, ct);
            await _graph.RemoveAsync(source, node.Id, ct);
            await _audit.AppendAsync(
                AuditAction.Consolidate, source, node.Id, $"consolidated into {target.Key}", ct);
            retired.Add(node.Id);
        }

        return new ConsolidateResult(facts, promoted, retired, false);
    }

    // ---- Owner links --------------------------------------------------------

    public ValueTask LinkOwnersAsync(string ownerA, string ownerB, CancellationToken ct = default) =>
        _ownerLinks.LinkAsync(ownerA, ownerB, ct);

    public ValueTask UnlinkOwnersAsync(string ownerA, string ownerB, CancellationToken ct = default) =>
        _ownerLinks.UnlinkAsync(ownerA, ownerB, ct);

    public ValueTask<IReadOnlySet<string>> GetLinkedOwnersAsync(string owner, CancellationToken ct = default) =>
        _ownerLinks.GetLinkedOwnersAsync(owner, ct);

    public ValueTask<IReadOnlyList<OwnerLink>> GetAllOwnerLinksAsync(CancellationToken ct = default) =>
        _ownerLinks.GetAllLinksAsync(ct);

    // ---- Helpers ------------------------------------------------------------

    private void Touch(MemoryNode node)
    {
        node.LastAccessedAt = _clock.GetUtcNow();
        node.AccessCount++;
    }

    /// <summary>
    /// Take a memory out of the current state without deleting it, stamping
    /// when it stopped being true so as-of recall and history can place it.
    /// </summary>
    private void Retire(MemoryNode node, MemoryStatus status, Guid? successor)
    {
        node.Status = status;
        node.RetiredAt = _clock.GetUtcNow();
        if (successor is not null)
            node.SupersededBy = successor;
    }

    private async ValueTask<IReadOnlyList<MemoryNode>> MatchingHistoryAsync(
        Namespace ns, string subject, string? predicate, CancellationToken ct)
    {
        // Subjects and predicates are stored normalised by GraphEdge.Create.
        var subjectKey = subject.Trim().ToLowerInvariant();
        var predicateKey = string.IsNullOrWhiteSpace(predicate) ? null : predicate.Trim().ToLowerInvariant();

        return (await _vectors.ListHistoryAsync(ns, ct))
            .Where(n => n.Edge.Subject == subjectKey)
            .Where(n => predicateKey is null || n.Edge.Predicate == predicateKey)
            .ToList();
    }

    private async ValueTask<IReadOnlyList<(MemoryNode Node, double Similarity)>> SearchHistoryAsync(
        IReadOnlyList<MemoryNode> candidates,
        float[] queryEmbedding,
        int topK,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        var results = new List<(MemoryNode Node, double Similarity)>();
        foreach (var node in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Vectors from a different embedding model live in an incompatible
            // space; skip them rather than return nonsense similarities.
            if (node.Embedding.Length != queryEmbedding.Length)
                continue;
            if (!await WasValidAtAsync(node, asOf, ct))
                continue;

            results.Add((node, HashingEmbeddingService.CosineSimilarity(queryEmbedding, node.Embedding)));
        }

        return results
            .OrderByDescending(r => r.Similarity)
            .Take(Math.Max(0, topK))
            .ToList();
    }

    private async ValueTask<bool> WasValidAtAsync(MemoryNode node, DateTimeOffset asOf, CancellationToken ct)
    {
        if (node.CreatedAt > asOf)
            return false;

        var retiredAt = await ResolveRetiredAtAsync(node, ct);
        return retiredAt is null || retiredAt > asOf;
    }

    /// <summary>
    /// When a memory stopped being current. Memories retired before
    /// <see cref="MemoryNode.RetiredAt"/> existed have to be placed from their
    /// successor instead; one with no successor at all is treated as never
    /// current rather than current forever, so a historical read cannot
    /// resurrect it.
    /// </summary>
    private async ValueTask<DateTimeOffset?> ResolveRetiredAtAsync(MemoryNode node, CancellationToken ct)
    {
        if (node.RetiredAt is { } retiredAt)
            return retiredAt;
        if (node.Status == MemoryStatus.Active)
            return null;

        if (node.SupersededBy is { } successorId)
        {
            var successor = await _vectors.GetAsync(successorId, ct);
            if (successor is not null)
                return successor.CreatedAt;
        }

        return node.CreatedAt;
    }
}
