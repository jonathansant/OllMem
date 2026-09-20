using OllMem.Core.Domain;

namespace OllMem.Core.Abstractions;

/// <summary>How a new fact relates to the memories already in a namespace.</summary>
public enum ReconcileKind
{
    /// <summary>Nothing existing covers this fact; store it alongside them.</summary>
    Unrelated,

    /// <summary>An existing memory already says this; refresh it instead of storing a copy.</summary>
    Duplicate,

    /// <summary>This fact contradicts an existing memory, which should be retired.</summary>
    Supersedes
}

/// <summary>
/// The reconciler's decision about a new fact.
/// </summary>
/// <param name="Kind">What relationship was found.</param>
/// <param name="TargetId">
/// The existing memory the verdict refers to. Always set for
/// <see cref="ReconcileKind.Duplicate"/> and <see cref="ReconcileKind.Supersedes"/>,
/// null for <see cref="ReconcileKind.Unrelated"/>.
/// </param>
/// <param name="Reason">Short human-readable justification, recorded in the audit log.</param>
public readonly record struct ReconcileVerdict(ReconcileKind Kind, Guid? TargetId, string Reason)
{
    public static ReconcileVerdict Unrelated { get; } = new(ReconcileKind.Unrelated, null, "");
}

/// <summary>
/// Decides whether a new fact duplicates or contradicts memories that are
/// semantically close to it (PRD REQ-2.2, beyond the exact subject+predicate
/// match). Sits behind an interface so the deterministic threshold rule can be
/// swapped for an LLM-backed implementation.
/// </summary>
public interface IFactReconciler
{
    /// <summary>
    /// Judge <paramref name="statement"/> against the nearest existing
    /// memories. Implementations must be side-effect free; the engine applies
    /// the verdict.
    /// </summary>
    /// <param name="candidates">
    /// Nearest active memories in the target namespace with their cosine
    /// similarity, most similar first.
    /// </param>
    ValueTask<ReconcileVerdict> ReconcileAsync(
        string statement,
        GraphEdge edge,
        IReadOnlyList<(MemoryNode Node, double Similarity)> candidates,
        CancellationToken ct = default);
}
