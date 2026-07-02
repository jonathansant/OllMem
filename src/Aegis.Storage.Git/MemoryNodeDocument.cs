using System.Runtime.InteropServices;
using Aegis.Core.Domain;

namespace Aegis.Storage.Git;

/// <summary>
/// The on-disk (and in-git) form of a <see cref="MemoryNode"/>: one JSON
/// document per node. Keeping each node in its own file means two machines
/// only ever produce a git conflict when they both modified the <i>same</i>
/// memory — and those conflicts are resolved field-wise by <see cref="Merge"/>.
/// </summary>
internal sealed record MemoryNodeDocument(
    Guid Id,
    string Owner,
    MemoryScope Scope,
    string ScopeId,
    string Statement,
    string Subject,
    string Predicate,
    string Object,
    string Embedding,
    string? BlobRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    int AccessCount,
    MemoryStatus Status,
    Guid? SupersededBy)
{
    public static MemoryNodeDocument From(MemoryNode node) => new(
        node.Id,
        node.Namespace.Owner,
        node.Namespace.Scope,
        node.Namespace.ScopeId,
        node.Statement,
        node.Edge.Subject,
        node.Edge.Predicate,
        node.Edge.Object,
        Convert.ToBase64String(MemoryMarshal.AsBytes(node.Embedding.AsSpan())),
        node.BlobRef,
        node.CreatedAt,
        node.LastAccessedAt,
        node.AccessCount,
        node.Status,
        node.SupersededBy);

    public MemoryNode ToNode()
    {
        var bytes = Convert.FromBase64String(Embedding);
        var embedding = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, embedding, 0, embedding.Length * sizeof(float));

        return new MemoryNode
        {
            Id = Id,
            Namespace = new Namespace(Owner, Scope, ScopeId),
            Statement = Statement,
            Edge = new GraphEdge(Subject, Predicate, Object),
            Embedding = embedding,
            BlobRef = BlobRef,
            CreatedAt = CreatedAt,
            LastAccessedAt = LastAccessedAt,
            AccessCount = AccessCount,
            Status = Status,
            SupersededBy = SupersededBy,
        };
    }

    /// <summary>
    /// Deterministic, commutative resolution for two divergent versions of the
    /// same node: a supersede on either side wins (facts must not resurrect),
    /// usage stats take the maximum, and the earliest creation time is kept.
    /// </summary>
    public static MemoryNodeDocument Merge(MemoryNodeDocument ours, MemoryNodeDocument theirs)
    {
        var baseline = theirs.LastAccessedAt > ours.LastAccessedAt ? theirs : ours;
        var superseded = ours.Status == MemoryStatus.Superseded ? ours
            : theirs.Status == MemoryStatus.Superseded ? theirs
            : null;

        return baseline with
        {
            CreatedAt = ours.CreatedAt <= theirs.CreatedAt ? ours.CreatedAt : theirs.CreatedAt,
            LastAccessedAt = ours.LastAccessedAt >= theirs.LastAccessedAt ? ours.LastAccessedAt : theirs.LastAccessedAt,
            AccessCount = Math.Max(ours.AccessCount, theirs.AccessCount),
            Status = superseded?.Status ?? baseline.Status,
            SupersededBy = superseded?.SupersededBy ?? baseline.SupersededBy,
        };
    }
}
