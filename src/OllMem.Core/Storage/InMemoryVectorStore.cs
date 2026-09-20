using System.Collections.Concurrent;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Embeddings;

namespace OllMem.Core.Storage;

/// <summary>
/// Thread-safe in-memory vector index. Performs an exact (brute-force) cosine
/// scan per namespace — well within the &lt;300ms recall budget (PRD NFR) for
/// the per-namespace working sets this layer targets. Replace with an ANN index
/// (e.g. HNSW) for very large namespaces.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<Guid, MemoryNode> _nodes = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _byNamespace = new();

    public ValueTask UpsertAsync(MemoryNode node, CancellationToken ct = default)
    {
        _nodes[node.Id] = node;
        var bucket = _byNamespace.GetOrAdd(node.Namespace.Key, _ => new ConcurrentDictionary<Guid, byte>());
        bucket[node.Id] = 0;
        return ValueTask.CompletedTask;
    }

    public ValueTask<MemoryNode?> GetAsync(Guid id, CancellationToken ct = default) =>
        ValueTask.FromResult(_nodes.GetValueOrDefault(id));

    public ValueTask<IReadOnlyList<(MemoryNode Node, double Similarity)>> SearchAsync(
        Namespace ns,
        float[] queryEmbedding,
        int topK,
        CancellationToken ct = default)
    {
        var results = new List<(MemoryNode Node, double Similarity)>();
        foreach (var node in EnumerateActive(ns))
        {
            // Vectors from a different embedding model (e.g. after switching
            // between the hashing default and an Ollama model) live in an
            // incompatible space; skip them rather than crash the recall.
            if (node.Embedding.Length != queryEmbedding.Length)
                continue;

            var sim = HashingEmbeddingService.CosineSimilarity(queryEmbedding, node.Embedding);
            results.Add((node, sim));
        }

        var top = results
            .OrderByDescending(r => r.Similarity)
            .Take(Math.Max(0, topK))
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<(MemoryNode, double)>>(top);
    }

    public ValueTask<IReadOnlyList<(MemoryNode Node, double Similarity)>> SearchOwnersAsync(
        IReadOnlySet<string> owners,
        float[] queryEmbedding,
        int topK,
        CancellationToken ct = default)
    {
        // Don't rely on the caller's set using a case-insensitive comparer.
        var match = new HashSet<string>(owners, StringComparer.OrdinalIgnoreCase);
        var top = _nodes.Values
            .Where(n => n.Status == MemoryStatus.Active
                && match.Contains(n.Namespace.Owner)
                && n.Embedding.Length == queryEmbedding.Length)
            .Select(n => (Node: n, Similarity: HashingEmbeddingService.CosineSimilarity(queryEmbedding, n.Embedding)))
            .OrderByDescending(r => r.Similarity)
            .Take(Math.Max(0, topK))
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<(MemoryNode, double)>>(top);
    }

    public ValueTask<IReadOnlyList<MemoryNode>> ListActiveAsync(Namespace ns, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<MemoryNode>>(EnumerateActive(ns).ToList());

    public ValueTask<IReadOnlyList<MemoryNode>> ListAllActiveAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<MemoryNode>>(
            _nodes.Values.Where(n => n.Status == MemoryStatus.Active).ToList());

    public ValueTask<IReadOnlyList<MemoryNode>> ListHistoryAsync(
        Namespace ns, CancellationToken ct = default)
    {
        if (!_byNamespace.TryGetValue(ns.Key, out var bucket))
            return ValueTask.FromResult<IReadOnlyList<MemoryNode>>([]);

        var all = bucket.Keys
            .Select(id => _nodes.GetValueOrDefault(id))
            .OfType<MemoryNode>()
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<MemoryNode>>(all);
    }

    public ValueTask<IReadOnlyList<MemoryNode>> ListOwnersHistoryAsync(
        IReadOnlySet<string> owners, CancellationToken ct = default)
    {
        if (owners.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<MemoryNode>>([]);

        // Don't rely on the caller's set using a case-insensitive comparer.
        var match = new HashSet<string>(owners, StringComparer.OrdinalIgnoreCase);
        var all = _nodes.Values.Where(n => match.Contains(n.Namespace.Owner)).ToList();
        return ValueTask.FromResult<IReadOnlyList<MemoryNode>>(all);
    }

    private IEnumerable<MemoryNode> EnumerateActive(Namespace ns)
    {
        if (!_byNamespace.TryGetValue(ns.Key, out var bucket))
            yield break;

        foreach (var id in bucket.Keys)
        {
            if (_nodes.TryGetValue(id, out var node) && node.Status == MemoryStatus.Active)
                yield return node;
        }
    }
}
