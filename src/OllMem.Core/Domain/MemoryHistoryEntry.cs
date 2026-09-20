namespace OllMem.Core.Domain;

/// <summary>
/// One version of a fact in the timeline of a (subject, predicate) relation:
/// the memory plus the window during which it was the current state.
/// </summary>
/// <param name="Node">The memory node.</param>
/// <param name="ValidFrom">When the fact was recorded.</param>
/// <param name="ValidTo">
/// When it stopped being current, or <c>null</c> while it is still the
/// current state.
/// </param>
public readonly record struct MemoryHistoryEntry(
    MemoryNode Node,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo);
