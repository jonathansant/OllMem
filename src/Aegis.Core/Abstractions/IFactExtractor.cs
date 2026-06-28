using Aegis.Core.Domain;

namespace Aegis.Core.Abstractions;

/// <summary>A standalone fact extracted from a transcript, before embedding.</summary>
/// <param name="Statement">Canonical natural-language statement.</param>
/// <param name="Edge">The relationship triple the statement encodes.</param>
public readonly record struct ExtractedFact(string Statement, GraphEdge Edge);

/// <summary>
/// Auto-extraction pipeline (PRD REQ-2.1): intercepts raw conversation
/// transcripts and identifies standalone facts, preferences and state changes
/// without the developer manually curating memories.
/// </summary>
public interface IFactExtractor
{
    /// <summary>
    /// Extract candidate facts from a transcript. Implementations should be
    /// side-effect free; persistence and conflict resolution happen downstream.
    /// </summary>
    ValueTask<IReadOnlyList<ExtractedFact>> ExtractAsync(string transcript, CancellationToken ct = default);
}
