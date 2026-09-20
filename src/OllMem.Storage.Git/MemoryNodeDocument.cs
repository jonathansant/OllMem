using System.Runtime.InteropServices;
using OllMem.Core.Domain;

namespace OllMem.Storage.Git;

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
    Guid? SupersededBy,
    DateTimeOffset? RetiredAt = null)
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
        node.SupersededBy,
        node.RetiredAt);

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
            RetiredAt = RetiredAt,
        };
    }

    /// <summary>
    /// Deterministic, commutative resolution for two divergent versions of the
    /// same node: a retirement on either side wins (facts must not resurrect),
    /// usage stats take the maximum, and the earliest creation time is kept.
    /// </summary>
    public static MemoryNodeDocument Merge(MemoryNodeDocument ours, MemoryNodeDocument theirs)
    {
        var baseline = theirs.LastAccessedAt > ours.LastAccessedAt ? theirs : ours;
        var retired = PickRetirement(ours, theirs);

        return baseline with
        {
            CreatedAt = ours.CreatedAt <= theirs.CreatedAt ? ours.CreatedAt : theirs.CreatedAt,
            LastAccessedAt = ours.LastAccessedAt >= theirs.LastAccessedAt ? ours.LastAccessedAt : theirs.LastAccessedAt,
            AccessCount = Math.Max(ours.AccessCount, theirs.AccessCount),

            // The three retirement fields describe one event, so they are taken
            // from one side together rather than picked independently.
            Status = retired?.Status ?? baseline.Status,
            SupersededBy = retired?.SupersededBy ?? baseline.SupersededBy,
            RetiredAt = retired?.RetiredAt,
        };
    }

    /// <summary>
    /// Choose which side's retirement to keep. Both machines may have retired
    /// the node independently and differently — one superseding it while the
    /// other consolidated the session it lived in — so the choice must not
    /// depend on which side happens to be "ours", or the two machines would
    /// converge on different answers and never agree again.
    /// </summary>
    private static MemoryNodeDocument? PickRetirement(MemoryNodeDocument ours, MemoryNodeDocument theirs)
    {
        var a = ours.Status != MemoryStatus.Active ? ours : null;
        var b = theirs.Status != MemoryStatus.Active ? theirs : null;
        if (a is null || b is null)
            return a ?? b;

        // The earliest recorded retirement wins; a recorded instant beats a
        // node retired before the instant was tracked.
        if (a.RetiredAt != b.RetiredAt)
        {
            if (a.RetiredAt is null)
                return b;
            if (b.RetiredAt is null)
                return a;
            return a.RetiredAt < b.RetiredAt ? a : b;
        }

        // Same instant, or neither recorded one: break the tie on the status
        // itself so the result is still independent of merge direction.
        return a.Status <= b.Status ? a : b;
    }
}
