using OllMem.Core.Abstractions;
using OllMem.Core.Domain;

namespace OllMem.Core.Reconciliation;

/// <summary>
/// Options controlling semantic conflict detection (PRD REQ-2.2).
/// </summary>
public sealed class ReconciliationOptions
{
    /// <summary>
    /// Cosine similarity at or above which a new statement is treated as
    /// saying the same thing as an existing memory, so the existing one is
    /// refreshed rather than duplicated.
    /// </summary>
    public double DuplicateSimilarity { get; init; } = 0.92;

    /// <summary>
    /// Lower edge of the "related but not identical" band. Memories scoring
    /// between this and <see cref="DuplicateSimilarity"/> are the ones an
    /// LLM-backed reconciler is asked to judge; below it they are unrelated.
    /// </summary>
    public double CandidateSimilarity { get; init; } = 0.75;

    /// <summary>How many nearest memories to consider when reconciling.</summary>
    public int MaxCandidates { get; init; } = 5;
}

/// <summary>
/// Deterministic, dependency-free reconciler: a new statement duplicates an
/// existing memory when it is about the same subject and their embeddings are
/// at least <see cref="ReconciliationOptions.DuplicateSimilarity"/> alike.
///
/// It deliberately never reports <see cref="ReconcileKind.Supersedes"/>:
/// deciding that two differently-worded facts contradict each other needs
/// language understanding, so retiring a memory is left to an LLM-backed
/// reconciler. Being conservative here means the offline default can only ever
/// merge restatements, never silently discard a fact.
/// </summary>
public sealed class ThresholdFactReconciler : IFactReconciler
{
    private readonly ReconciliationOptions _options;

    public ThresholdFactReconciler(ReconciliationOptions? options = null)
    {
        _options = options ?? new ReconciliationOptions();
        if (_options.DuplicateSimilarity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "DuplicateSimilarity must be within [0, 1].");
        if (_options.CandidateSimilarity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "CandidateSimilarity must be within [0, 1].");
        if (_options.CandidateSimilarity > _options.DuplicateSimilarity)
            throw new ArgumentOutOfRangeException(
                nameof(options), "CandidateSimilarity must not exceed DuplicateSimilarity.");
        if (_options.MaxCandidates <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxCandidates must be positive.");
    }

    public ValueTask<ReconcileVerdict> ReconcileAsync(
        string statement,
        GraphEdge edge,
        IReadOnlyList<(MemoryNode Node, double Similarity)> candidates,
        CancellationToken ct = default)
    {
        (MemoryNode Node, double Similarity)? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Node.Status != MemoryStatus.Active)
                continue;

            // Subjects are normalised lower-case by GraphEdge.Create, so this
            // keeps facts about different entities from merging just because
            // they are worded alike.
            if (!string.Equals(candidate.Node.Edge.Subject, edge.Subject, StringComparison.OrdinalIgnoreCase))
                continue;

            if (candidate.Similarity < _options.DuplicateSimilarity)
                continue;

            if (best is null || candidate.Similarity > best.Value.Similarity)
                best = candidate;
        }

        var verdict = best is { } match
            ? new ReconcileVerdict(
                ReconcileKind.Duplicate,
                match.Node.Id,
                $"similarity {match.Similarity:0.###} >= {_options.DuplicateSimilarity:0.###}")
            : ReconcileVerdict.Unrelated;

        return ValueTask.FromResult(verdict);
    }
}
