namespace Aegis.Core.Domain;

/// <summary>
/// Identifies an isolated memory partition. A namespace is the unit of
/// ownership and cryptographic compartmentalisation (PRD REQ-1.3): every
/// memory belongs to exactly one (owner, scope, scopeId) tuple.
/// </summary>
/// <param name="Owner">
/// Stable owner identity. In production this is the public key (hex) that
/// signs requests; locally it can be any opaque tenant id.
/// </param>
/// <param name="Scope">The hierarchical tier this namespace addresses.</param>
/// <param name="ScopeId">
/// Identifier within the scope, e.g. the user id, agent id, or session id.
/// </param>
public readonly record struct Namespace(string Owner, MemoryScope Scope, string ScopeId)
{
    /// <summary>Canonical, stable string key used for indexing and audit.</summary>
    public string Key => $"{Owner}/{Scope}/{ScopeId}".ToLowerInvariant();

    public override string ToString() => Key;
}
