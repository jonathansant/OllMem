using OllMem.Core.Domain;

namespace OllMem.Core.Abstractions;

/// <summary>
/// Semantic half of the hybrid datastore (PRD REQ-3.1): stores memory nodes
/// and finds the most similar ones for a query embedding.
/// </summary>
public interface IVectorStore
{
    ValueTask UpsertAsync(MemoryNode node, CancellationToken ct = default);

    ValueTask<MemoryNode?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Return the <paramref name="topK"/> active nodes in the namespace ranked
    /// by raw cosine similarity to <paramref name="queryEmbedding"/>.
    /// </summary>
    ValueTask<IReadOnlyList<(MemoryNode Node, double Similarity)>> SearchAsync(
        Namespace ns,
        float[] queryEmbedding,
        int topK,
        CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="SearchAsync"/>, but spanning every namespace belonging to
    /// any of <paramref name="owners"/> — all scopes and scopeIds — so recall can
    /// reach sibling namespaces and linked identities (see
    /// <see cref="IOwnerLinkStore"/>) without loading the whole store.
    /// Owner matching is case-insensitive, matching <see cref="Namespace.Key"/>.
    /// </summary>
    ValueTask<IReadOnlyList<(MemoryNode Node, double Similarity)>> SearchOwnersAsync(
        IReadOnlySet<string> owners,
        float[] queryEmbedding,
        int topK,
        CancellationToken ct = default);

    /// <summary>All active nodes in a namespace (used by conflict resolution).</summary>
    ValueTask<IReadOnlyList<MemoryNode>> ListActiveAsync(Namespace ns, CancellationToken ct = default);

    /// <summary>All active nodes across every namespace (used by the graph visualizer).</summary>
    ValueTask<IReadOnlyList<MemoryNode>> ListAllActiveAsync(CancellationToken ct = default);
}
