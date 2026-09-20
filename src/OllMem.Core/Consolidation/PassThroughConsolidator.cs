using OllMem.Core.Abstractions;
using OllMem.Core.Domain;

namespace OllMem.Core.Consolidation;

/// <summary>
/// Deterministic, dependency-free consolidator: every session memory is
/// promoted as-is, with exact repeats of the same relation collapsed.
///
/// It does not attempt to judge which facts are durable and which were only
/// task state — that needs language understanding, so it is left to an
/// LLM-backed consolidator. Promoting everything is the safe default: the
/// target namespace's own conflict resolution still merges restatements, and
/// nothing is lost.
/// </summary>
public sealed class PassThroughConsolidator : IMemoryConsolidator
{
    public ValueTask<IReadOnlyList<ExtractedFact>> ConsolidateAsync(
        IReadOnlyList<MemoryNode> sessionMemories,
        CancellationToken ct = default)
    {
        var facts = new List<ExtractedFact>(sessionMemories.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in sessionMemories.OrderBy(n => n.CreatedAt))
        {
            if (node.Status != MemoryStatus.Active)
                continue;
            if (seen.Add(node.Edge.RelationKey + "=>" + node.Edge.Object.ToLowerInvariant()))
                facts.Add(new ExtractedFact(node.Statement, node.Edge));
        }

        return ValueTask.FromResult<IReadOnlyList<ExtractedFact>>(facts);
    }
}
