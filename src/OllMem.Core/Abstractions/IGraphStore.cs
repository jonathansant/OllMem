using OllMem.Core.Domain;

namespace OllMem.Core.Abstractions;

/// <summary>
/// Relationship half of the hybrid datastore (PRD REQ-3.1). Models memories as
/// an explicit directed graph so callers can traverse entity relationships
/// (e.g. all facts where subject = "user").
/// </summary>
public interface IGraphStore
{
    ValueTask AddAsync(Namespace ns, MemoryNode node, CancellationToken ct = default);

    /// <summary>Remove an edge for a superseded memory.</summary>
    ValueTask RemoveAsync(Namespace ns, Guid nodeId, CancellationToken ct = default);

    /// <summary>Outgoing edges for an entity, optionally filtered by predicate.</summary>
    ValueTask<IReadOnlyList<GraphEdge>> NeighborsAsync(
        Namespace ns,
        string subject,
        string? predicate = null,
        CancellationToken ct = default);

    /// <summary>
    /// Find the active memory expressing the (subject, predicate) relation, if
    /// any. Used to detect conflicting facts (PRD REQ-2.2).
    /// </summary>
    ValueTask<Guid?> FindByRelationAsync(
        Namespace ns,
        string subject,
        string predicate,
        CancellationToken ct = default);
}
