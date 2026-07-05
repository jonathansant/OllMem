using System.Collections.Concurrent;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;

namespace OllMem.Core.Storage;

/// <summary>
/// Thread-safe in-memory knowledge graph. Each namespace owns an adjacency map
/// keyed by subject; a secondary index maps (subject, predicate) to the active
/// node id so conflict detection (PRD REQ-2.2) is O(1).
/// </summary>
public sealed class InMemoryGraphStore : IGraphStore
{
    private sealed class Partition
    {
        public ConcurrentDictionary<string, ConcurrentDictionary<Guid, GraphEdge>> BySubject { get; } = new();
        public ConcurrentDictionary<string, Guid> RelationIndex { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Partition> _partitions = new();

    public ValueTask AddAsync(Namespace ns, MemoryNode node, CancellationToken ct = default)
    {
        var p = _partitions.GetOrAdd(ns.Key, _ => new Partition());
        var edges = p.BySubject.GetOrAdd(node.Edge.Subject, _ => new ConcurrentDictionary<Guid, GraphEdge>());
        edges[node.Id] = node.Edge;
        p.RelationIndex[node.Edge.RelationKey] = node.Id;
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(Namespace ns, Guid nodeId, CancellationToken ct = default)
    {
        if (!_partitions.TryGetValue(ns.Key, out var p))
            return ValueTask.CompletedTask;

        foreach (var edges in p.BySubject.Values)
        {
            if (edges.TryRemove(nodeId, out var edge) &&
                p.RelationIndex.TryGetValue(edge.RelationKey, out var current) &&
                current == nodeId)
            {
                p.RelationIndex.TryRemove(edge.RelationKey, out _);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<GraphEdge>> NeighborsAsync(
        Namespace ns,
        string subject,
        string? predicate = null,
        CancellationToken ct = default)
    {
        var key = subject.Trim().ToLowerInvariant();
        if (!_partitions.TryGetValue(ns.Key, out var p) ||
            !p.BySubject.TryGetValue(key, out var edges))
        {
            return ValueTask.FromResult<IReadOnlyList<GraphEdge>>([]);
        }

        IEnumerable<GraphEdge> result = edges.Values;
        if (!string.IsNullOrWhiteSpace(predicate))
        {
            var pred = predicate.Trim().ToLowerInvariant();
            result = result.Where(e => e.Predicate == pred);
        }

        return ValueTask.FromResult<IReadOnlyList<GraphEdge>>(result.ToList());
    }

    public ValueTask<Guid?> FindByRelationAsync(
        Namespace ns,
        string subject,
        string predicate,
        CancellationToken ct = default)
    {
        var relationKey = $"{subject.Trim().ToLowerInvariant()}|{predicate.Trim().ToLowerInvariant()}";
        if (_partitions.TryGetValue(ns.Key, out var p) &&
            p.RelationIndex.TryGetValue(relationKey, out var id))
        {
            return ValueTask.FromResult<Guid?>(id);
        }

        return ValueTask.FromResult<Guid?>(null);
    }
}
