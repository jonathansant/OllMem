using OllMem.Core.Domain;

namespace OllMem.Core.Engine;

/// <summary>
/// Scores how "close" a candidate namespace is to the namespace a recall query
/// was issued against. Recall no longer stops at an exact (owner, scope,
/// scopeId) match — a fact stored under a sibling scopeId, a different scope
/// tier, or an owner explicitly linked as the same identity can still be
/// relevant. This weight is multiplied into the ranking score so exact matches
/// still win ties, but nearby memories are no longer invisible just because the
/// caller's scopeId convention drifted.
///
/// Only namespaces recall is allowed to reach are scored: the caller's own
/// owner and owners linked to it (see <see cref="Abstractions.IOwnerLinkStore"/>).
/// Unrelated owners are filtered out before scoring, never merely down-weighted.
/// </summary>
public static class NamespaceAffinityScorer
{
    public const double Exact = 1.0;
    public const double Sibling = 0.9;
    public const double DifferentScope = 0.75;

    /// <param name="query">The namespace recall was called against.</param>
    /// <param name="candidate">The namespace of a candidate memory.</param>
    /// <param name="linkedOwners">
    /// Owners considered equivalent to <paramref name="query"/>'s owner,
    /// including the owner itself. A candidate whose owner is in this set is
    /// scored as a sibling, even though it is a distinct, separately-encrypted
    /// namespace.
    /// </param>
    public static double Score(Namespace query, Namespace candidate, IReadOnlySet<string>? linkedOwners = null)
    {
        // Compare on Key, not struct equality: Key is case-normalised, so two
        // namespaces differing only in casing are the same namespace.
        if (string.Equals(candidate.Key, query.Key, StringComparison.Ordinal))
            return Exact;

        var sameOwner = string.Equals(candidate.Owner, query.Owner, StringComparison.OrdinalIgnoreCase)
            || (linkedOwners?.Contains(candidate.Owner) ?? false);

        // Defensive: an unrelated owner should have been filtered out upstream,
        // so score it as distant rather than rewarding it as a sibling.
        if (!sameOwner)
            return DifferentScope;

        var sameScope = candidate.Scope == query.Scope;
        var sameScopeId = string.Equals(candidate.ScopeId, query.ScopeId, StringComparison.OrdinalIgnoreCase);
        return sameScope || sameScopeId ? Sibling : DifferentScope;
    }
}
