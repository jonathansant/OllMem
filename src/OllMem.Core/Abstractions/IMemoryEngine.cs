using OllMem.Core.Domain;

namespace OllMem.Core.Abstractions;

/// <summary>What storing a fact actually did to the namespace.</summary>
public enum RememberOutcome
{
    /// <summary>A new memory was written.</summary>
    Created,

    /// <summary>An existing memory already said this; its recency was refreshed instead.</summary>
    Refreshed,

    /// <summary>A new memory was written and an existing one it contradicted was retired.</summary>
    Superseded
}

/// <summary>Outcome of storing one fact, including whether it replaced another.</summary>
/// <param name="Node">The stored memory node — the existing one when refreshed.</param>
/// <param name="SupersededId">The prior memory it replaced, if a conflict was resolved.</param>
/// <param name="Outcome">Which of the three write paths was taken.</param>
public readonly record struct RememberResult(
    MemoryNode Node,
    Guid? SupersededId,
    RememberOutcome Outcome = RememberOutcome.Created);

/// <summary>Outcome of promoting a session namespace into a durable one.</summary>
/// <param name="Facts">The durable facts the consolidator produced.</param>
/// <param name="Promoted">
/// What storing each fact did in the target namespace. Empty for a dry run.
/// </param>
/// <param name="RetiredSessionIds">Session memories marked consolidated. Empty for a dry run.</param>
/// <param name="DryRun">Whether this was a preview that changed nothing.</param>
public readonly record struct ConsolidateResult(
    IReadOnlyList<ExtractedFact> Facts,
    IReadOnlyList<RememberResult> Promoted,
    IReadOnlyList<Guid> RetiredSessionIds,
    bool DryRun);

/// <summary>
/// High-level orchestrator that ties the hybrid datastore, extraction,
/// encryption, audit and reranking together — the surface the SDKs and API
/// sit on top of.
/// </summary>
public interface IMemoryEngine
{
    /// <summary>Store a single explicit fact, resolving conflicts (REQ-2.2).</summary>
    ValueTask<RememberResult> RememberAsync(
        Namespace ns,
        string statement,
        GraphEdge? edge = null,
        CancellationToken ct = default);

    /// <summary>
    /// Auto-extract facts from a transcript and store them (REQ-2.1). Returns
    /// the results for each fact persisted.
    /// </summary>
    ValueTask<IReadOnlyList<RememberResult>> ExtractAndStoreAsync(
        Namespace ns,
        string transcript,
        CancellationToken ct = default);

    /// <summary>
    /// Hybrid recall: semantic search + time-aware reranking (REQ-4.2).
    /// Updates access stats for returned nodes (feeding the decay model).
    /// </summary>
    /// <param name="asOf">
    /// Answer as the memory stood at this instant instead of now: facts
    /// recorded later are invisible and facts retired since are current again.
    /// A historical read leaves access stats untouched so it cannot distort
    /// the decay model.
    /// </param>
    ValueTask<IReadOnlyList<ScoredMemory>> RecallAsync(
        Namespace ns,
        string query,
        int topK = 5,
        DateTimeOffset? asOf = null,
        CancellationToken ct = default);

    /// <summary>Graph traversal: outgoing relationships for an entity (REQ-3.1).</summary>
    /// <param name="asOf">Traverse the graph as it stood at this instant instead of now.</param>
    ValueTask<IReadOnlyList<GraphEdge>> NeighborsAsync(
        Namespace ns,
        string subject,
        string? predicate = null,
        DateTimeOffset? asOf = null,
        CancellationToken ct = default);

    /// <summary>
    /// Every version of a relation over time, oldest first, each with the
    /// window during which it was the current state (REQ-4.1 keeps the
    /// superseded versions that make this possible).
    /// </summary>
    ValueTask<IReadOnlyList<MemoryHistoryEntry>> HistoryAsync(
        Namespace ns,
        string subject,
        string? predicate = null,
        CancellationToken ct = default);

    /// <summary>
    /// Promote what a session learned into a durable namespace: the session's
    /// memories are summarised into facts, stored in <paramref name="target"/>
    /// under the usual conflict resolution, and the originals are marked
    /// consolidated so they leave recall but stay in history.
    /// </summary>
    /// <param name="source">A session-scoped namespace to drain.</param>
    /// <param name="target">An agent- or user-scoped namespace owned by the same owner.</param>
    /// <param name="dryRun">Preview the facts without writing anything.</param>
    ValueTask<ConsolidateResult> ConsolidateAsync(
        Namespace source,
        Namespace target,
        bool dryRun = false,
        CancellationToken ct = default);

    /// <summary>
    /// Declare two owner identities as the same caller (e.g. the same person's
    /// two machines/signing keys), widening recall to consider both without
    /// merging or moving either owner's namespaces.
    /// </summary>
    ValueTask LinkOwnersAsync(string ownerA, string ownerB, CancellationToken ct = default);

    /// <summary>Remove a previously declared owner link.</summary>
    ValueTask UnlinkOwnersAsync(string ownerA, string ownerB, CancellationToken ct = default);

    /// <summary>The full linked-owner set for <paramref name="owner"/>, including itself.</summary>
    ValueTask<IReadOnlySet<string>> GetLinkedOwnersAsync(string owner, CancellationToken ct = default);

    /// <summary>Every declared owner link, once per pair. For visualisation/administration.</summary>
    ValueTask<IReadOnlyList<OwnerLink>> GetAllOwnerLinksAsync(CancellationToken ct = default);
}
