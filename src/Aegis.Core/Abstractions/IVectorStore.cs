using Aegis.Core.Domain;

namespace Aegis.Core.Abstractions;

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

    /// <summary>All active nodes in a namespace (used by conflict resolution).</summary>
    ValueTask<IReadOnlyList<MemoryNode>> ListActiveAsync(Namespace ns, CancellationToken ct = default);
}
