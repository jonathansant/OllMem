using Aegis.Core.Domain;

namespace Aegis.Core.Abstractions;

/// <summary>Outcome of storing one fact, including whether it replaced another.</summary>
/// <param name="Node">The stored memory node.</param>
/// <param name="SupersededId">The prior memory it replaced, if a conflict was resolved.</param>
public readonly record struct RememberResult(MemoryNode Node, Guid? SupersededId);

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
    ValueTask<IReadOnlyList<ScoredMemory>> RecallAsync(
        Namespace ns,
        string query,
        int topK = 5,
        CancellationToken ct = default);

    /// <summary>Graph traversal: outgoing relationships for an entity (REQ-3.1).</summary>
    ValueTask<IReadOnlyList<GraphEdge>> NeighborsAsync(
        Namespace ns,
        string subject,
        string? predicate = null,
        CancellationToken ct = default);
}
