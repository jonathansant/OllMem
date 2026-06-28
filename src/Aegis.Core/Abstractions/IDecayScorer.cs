using Aegis.Core.Domain;

namespace Aegis.Core.Abstractions;

/// <summary>
/// Computes the soft-decay weight for a memory (PRD REQ-4.1). The weight is a
/// non-destructive multiplier in the range [floor, 1.0]: older / rarely used
/// memories are down-weighted but never deleted.
/// </summary>
public interface IDecayScorer
{
    /// <summary>The minimum weight a memory can decay to (default 0.3).</summary>
    double Floor { get; }

    /// <summary>Weight in [Floor, 1.0] for <paramref name="node"/> evaluated at <paramref name="now"/>.</summary>
    double Weight(MemoryNode node, DateTimeOffset now);
}
