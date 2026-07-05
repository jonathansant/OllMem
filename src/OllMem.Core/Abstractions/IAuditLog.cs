using OllMem.Core.Domain;

namespace OllMem.Core.Abstractions;

public enum AuditAction
{
    Create,
    Access,
    Update,
    Supersede
}

/// <summary>
/// A single tamper-evident audit record. Each entry chains to the previous one
/// via <see cref="PrevHash"/> so the whole log can be verified (PRD REQ-3.3).
/// </summary>
public sealed record AuditEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    AuditAction Action,
    string NamespaceKey,
    Guid NodeId,
    string Detail,
    string PrevHash,
    string Hash);

/// <summary>Immutable, append-only, hash-chained audit log (PRD REQ-3.3).</summary>
public interface IAuditLog
{
    ValueTask<AuditEntry> AppendAsync(
        AuditAction action,
        Namespace ns,
        Guid nodeId,
        string detail,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(string? namespaceKey = null, CancellationToken ct = default);

    /// <summary>Verify the integrity of the hash chain end to end.</summary>
    ValueTask<bool> VerifyAsync(CancellationToken ct = default);
}
