using OllMem.Core.Domain;

namespace OllMem.Core.Abstractions;

/// <summary>
/// Turns the memories a session accumulated into the durable facts worth
/// keeping, so short-lived task state can be promoted into an agent or user
/// namespace instead of lingering in recall forever.
/// </summary>
public interface IMemoryConsolidator
{
    /// <summary>
    /// Summarise a session's memories into the facts to promote.
    /// Implementations must be side-effect free; the engine persists the
    /// result and retires the originals.
    /// </summary>
    ValueTask<IReadOnlyList<ExtractedFact>> ConsolidateAsync(
        IReadOnlyList<MemoryNode> sessionMemories,
        CancellationToken ct = default);
}
