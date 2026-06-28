namespace Aegis.Core.Domain;

/// <summary>
/// A memory returned from recall together with the scores that ranked it.
/// </summary>
/// <param name="Node">The memory node.</param>
/// <param name="Similarity">Raw cosine similarity to the query (0..1).</param>
/// <param name="DecayWeight">Recency/usage multiplier with a 0.3 floor (PRD REQ-4.1).</param>
/// <param name="FinalScore">similarity × decayWeight, used for ranking (PRD REQ-4.2).</param>
public readonly record struct ScoredMemory(
    MemoryNode Node,
    double Similarity,
    double DecayWeight,
    double FinalScore);
