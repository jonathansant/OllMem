using System.Security.Cryptography;
using System.Text;
using Aegis.Core.Abstractions;
using Aegis.Core.Domain;

namespace Aegis.Core.Security;

/// <summary>
/// Append-only, hash-chained audit log (PRD REQ-3.3). Each entry's hash covers
/// its own fields plus the previous entry's hash, so any retroactive edit or
/// deletion breaks the chain and is detectable via <see cref="VerifyAsync"/>.
/// </summary>
public sealed class HashChainedAuditLog : IAuditLog
{
    private const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly List<AuditEntry> _entries = [];
    private readonly object _gate = new();

    public ValueTask<AuditEntry> AppendAsync(
        AuditAction action,
        Namespace ns,
        Guid nodeId,
        string detail,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var seq = _entries.Count;
            var prevHash = _entries.Count == 0 ? GenesisHash : _entries[^1].Hash;
            var timestamp = DateTimeOffset.UtcNow;
            var hash = ComputeHash(seq, timestamp, action, ns.Key, nodeId, detail, prevHash);

            var entry = new AuditEntry(seq, timestamp, action, ns.Key, nodeId, detail, prevHash, hash);
            _entries.Add(entry);
            return ValueTask.FromResult(entry);
        }
    }

    public ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(
        string? namespaceKey = null,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<AuditEntry> query = _entries;
            if (!string.IsNullOrWhiteSpace(namespaceKey))
            {
                var key = namespaceKey.ToLowerInvariant();
                query = query.Where(e => e.NamespaceKey == key);
            }

            return ValueTask.FromResult<IReadOnlyList<AuditEntry>>(query.ToList());
        }
    }

    public ValueTask<bool> VerifyAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var prevHash = GenesisHash;
            foreach (var e in _entries)
            {
                if (e.PrevHash != prevHash)
                    return ValueTask.FromResult(false);

                var expected = ComputeHash(
                    e.Sequence, e.Timestamp, e.Action, e.NamespaceKey, e.NodeId, e.Detail, e.PrevHash);
                if (expected != e.Hash)
                    return ValueTask.FromResult(false);

                prevHash = e.Hash;
            }

            return ValueTask.FromResult(true);
        }
    }

    private static string ComputeHash(
        long seq,
        DateTimeOffset timestamp,
        AuditAction action,
        string namespaceKey,
        Guid nodeId,
        string detail,
        string prevHash)
    {
        var canonical = string.Join('\n',
            seq.ToString(),
            timestamp.UtcTicks.ToString(),
            action.ToString(),
            namespaceKey,
            nodeId.ToString(),
            detail,
            prevHash);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
