using Aegis.Core.Abstractions;
using Aegis.Core.Domain;

namespace Aegis.Core.Retrieval;

/// <summary>
/// Options controlling the soft-decay curve (PRD REQ-4.1).
/// </summary>
public sealed class SoftDecayOptions
{
    /// <summary>Minimum weight a memory can decay to. Never deleted, just down-weighted.</summary>
    public double Floor { get; init; } = 0.3;

    /// <summary>Age in days at which the recency component reaches ~0.37 (1/e) of its range.</summary>
    public double HalfLifeDays { get; init; } = 30.0;

    /// <summary>How strongly repeated access boosts a memory back toward 1.0.</summary>
    public double UsageBoost { get; init; } = 0.15;
}

/// <summary>
/// Computes a non-destructive relevance multiplier from recency and usage.
/// Recency decays exponentially toward the floor; frequent access pulls the
/// weight back up. The result is always clamped to [Floor, 1.0] so old memories
/// remain retrievable rather than being garbage-collected.
/// </summary>
public sealed class SoftDecayScorer : IDecayScorer
{
    private readonly SoftDecayOptions _options;

    public SoftDecayScorer(SoftDecayOptions? options = null)
    {
        _options = options ?? new SoftDecayOptions();
        if (_options.Floor is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Floor must be within [0, 1].");
        if (_options.HalfLifeDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "HalfLifeDays must be positive.");
    }

    public double Floor => _options.Floor;

    public double Weight(MemoryNode node, DateTimeOffset now)
    {
        var ageDays = Math.Max(0.0, (now - node.LastAccessedAt).TotalDays);

        // Exponential recency decay across the full [0,1] range.
        var recency = Math.Exp(-ageDays / _options.HalfLifeDays);

        // Diminishing-returns usage boost: log keeps very hot items from dominating.
        var usage = _options.UsageBoost * Math.Log(1 + Math.Max(0, node.AccessCount));

        // Map recency into [Floor, 1.0], then add usage, then re-clamp.
        var weight = _options.Floor + (1.0 - _options.Floor) * recency + usage;
        return Math.Clamp(weight, _options.Floor, 1.0);
    }
}
