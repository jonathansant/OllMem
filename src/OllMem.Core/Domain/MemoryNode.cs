namespace OllMem.Core.Domain;

/// <summary>
/// The lifecycle status of a memory. Conflicting facts are never destructively
/// deleted; they are <see cref="Superseded"/> so history is preserved
/// (PRD REQ-2.2, REQ-4.1).
/// </summary>
public enum MemoryStatus
{
    Active = 0,
    Superseded = 1
}

/// <summary>
/// A single unit of memory. Every node is simultaneously a semantic embedding
/// (for similarity search) and a graph node carrying an explicit relationship
/// triple — the hybrid datastore model from PRD REQ-3.1.
/// </summary>
public sealed class MemoryNode
{
    public required Guid Id { get; init; }

    /// <summary>Owning hierarchical namespace (user / agent / session).</summary>
    public required Namespace Namespace { get; init; }

    /// <summary>Human-readable canonical statement, e.g. "User lives in SF".</summary>
    public required string Statement { get; init; }

    /// <summary>Explicit relationship triple for graph traversal.</summary>
    public required GraphEdge Edge { get; init; }

    /// <summary>Normalised semantic embedding of <see cref="Statement"/>.</summary>
    public required float[] Embedding { get; init; }

    /// <summary>
    /// Content address (hash) of the encrypted raw payload in blob storage
    /// (PRD REQ-3.2). Null until persisted to the blob layer.
    /// </summary>
    public string? BlobRef { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Updated whenever the memory is recalled (drives recency weight).</summary>
    public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Number of times this memory has been recalled (drives usage weight).</summary>
    public int AccessCount { get; set; }

    public MemoryStatus Status { get; set; } = MemoryStatus.Active;

    /// <summary>If superseded, the node that replaced this one.</summary>
    public Guid? SupersededBy { get; set; }
}
