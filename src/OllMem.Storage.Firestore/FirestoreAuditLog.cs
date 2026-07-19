using Google.Cloud.Firestore;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Security;

namespace OllMem.Storage.Firestore;

/// <summary>
/// Persistent, append-only, hash-chained audit log (PRD REQ-3.3) on Firestore.
/// Uses the shared <see cref="AuditChain"/> hashing so entries are byte-for-byte
/// compatible with the other stores. The chain tip (last sequence + hash) lives
/// in a meta document; appends run in a Firestore transaction that reads the
/// tip, creates the next entry and advances the tip, so the chain stays
/// strictly ordered even with several writers.
/// </summary>
public sealed class FirestoreAuditLog : IAuditLog
{
    private readonly FirestoreStoreContext _ctx;

    public FirestoreAuditLog(FirestoreStoreContext ctx) => _ctx = ctx;

    public async ValueTask<AuditEntry> AppendAsync(
        AuditAction action, Namespace ns, Guid nodeId, string detail, CancellationToken ct = default)
    {
        return await _ctx.Db.RunTransactionAsync(async tx =>
        {
            var tip = await tx.GetSnapshotAsync(_ctx.AuditTip, ct);
            long nextSeq = 0;
            var prevHash = AuditChain.GenesisHash;
            if (tip.Exists)
            {
                nextSeq = tip.GetValue<long>("sequence") + 1;
                prevHash = tip.GetValue<string>("hash");
            }

            var timestamp = DateTimeOffset.UtcNow;
            var hash = AuditChain.ComputeHash(nextSeq, timestamp, action, ns.Key, nodeId, detail, prevHash);
            var entry = new AuditEntry(nextSeq, timestamp, action, ns.Key, nodeId, detail, prevHash, hash);

            // D19 keeps document ids lexicographically ordered by sequence.
            tx.Create(_ctx.Audit.Document(nextSeq.ToString("D19")), ToFields(entry));
            tx.Set(_ctx.AuditTip, new Dictionary<string, object>
            {
                ["sequence"] = nextSeq,
                ["hash"] = hash,
            });
            return entry;
        }, cancellationToken: ct);
    }

    public async ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(
        string? namespaceKey = null, CancellationToken ct = default)
    {
        // The filtered query stays equality-only (no composite index needed);
        // ordering by sequence happens client-side.
        Query query = namespaceKey is null
            ? _ctx.Audit.OrderBy("sequence")
            : _ctx.Audit.WhereEqualTo("namespaceKey", namespaceKey.ToLowerInvariant());

        var snapshot = await query.GetSnapshotAsync(ct);
        return snapshot.Documents
            .Select(MapEntry)
            .OrderBy(e => e.Sequence)
            .ToList();
    }

    public async ValueTask<bool> VerifyAsync(CancellationToken ct = default)
    {
        var entries = await ReadAsync(null, ct);
        var prevHash = AuditChain.GenesisHash;
        foreach (var e in entries)
        {
            if (e.PrevHash != prevHash)
                return false;

            var expected = AuditChain.ComputeHash(
                e.Sequence, e.Timestamp, e.Action, e.NamespaceKey, e.NodeId, e.Detail, e.PrevHash);
            if (expected != e.Hash)
                return false;

            prevHash = e.Hash;
        }

        return true;
    }

    private static Dictionary<string, object> ToFields(AuditEntry entry) => new()
    {
        ["sequence"] = entry.Sequence,
        ["timestamp"] = entry.Timestamp.ToString("O"),
        ["action"] = (int)entry.Action,
        ["namespaceKey"] = entry.NamespaceKey,
        ["nodeId"] = entry.NodeId.ToString(),
        ["detail"] = entry.Detail,
        ["prevHash"] = entry.PrevHash,
        ["hash"] = entry.Hash,
    };

    private static AuditEntry MapEntry(DocumentSnapshot d) => new(
        d.GetValue<long>("sequence"),
        DateTimeOffset.Parse(d.GetValue<string>("timestamp"), null, System.Globalization.DateTimeStyles.RoundtripKind),
        (AuditAction)d.GetValue<int>("action"),
        d.GetValue<string>("namespaceKey"),
        Guid.Parse(d.GetValue<string>("nodeId")),
        d.GetValue<string>("detail"),
        d.GetValue<string>("prevHash"),
        d.GetValue<string>("hash"));
}
