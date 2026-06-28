using Aegis.Core.Abstractions;
using Aegis.Core.Domain;
using Aegis.Core.Security;

namespace Aegis.Storage.Sqlite;

/// <summary>
/// Persistent, append-only, hash-chained audit log (PRD REQ-3.3). Uses the
/// shared <see cref="AuditChain"/> hashing so entries are byte-for-byte
/// compatible with the in-memory log. Appends are serialised so the chain stays
/// strictly ordered.
/// </summary>
public sealed class SqliteAuditLog : IAuditLog
{
    private readonly SqliteStoreContext _ctx;
    private readonly SemaphoreSlim _appendGate = new(1, 1);

    public SqliteAuditLog(SqliteStoreContext ctx) => _ctx = ctx;

    public async ValueTask<AuditEntry> AppendAsync(
        AuditAction action, Namespace ns, Guid nodeId, string detail, CancellationToken ct = default)
    {
        await _appendGate.WaitAsync(ct);
        try
        {
            await using var conn = _ctx.OpenConnection();

            long nextSeq;
            string prevHash;
            await using (var tip = conn.CreateCommand())
            {
                tip.CommandText = "SELECT sequence, hash FROM audit ORDER BY sequence DESC LIMIT 1";
                await using var reader = await tip.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    nextSeq = reader.GetInt64(0) + 1;
                    prevHash = reader.GetString(1);
                }
                else
                {
                    nextSeq = 0;
                    prevHash = AuditChain.GenesisHash;
                }
            }

            var timestamp = DateTimeOffset.UtcNow;
            var hash = AuditChain.ComputeHash(nextSeq, timestamp, action, ns.Key, nodeId, detail, prevHash);

            await using (var insert = conn.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT INTO audit (sequence, timestamp, action, namespace_key, node_id, detail, prev_hash, hash)
                    VALUES ($seq, $ts, $action, $ns, $node, $detail, $prev, $hash)
                    """;
                insert.Parameters.AddWithValue("$seq", nextSeq);
                insert.Parameters.AddWithValue("$ts", timestamp.ToString("O"));
                insert.Parameters.AddWithValue("$action", (int)action);
                insert.Parameters.AddWithValue("$ns", ns.Key);
                insert.Parameters.AddWithValue("$node", nodeId.ToString());
                insert.Parameters.AddWithValue("$detail", detail);
                insert.Parameters.AddWithValue("$prev", prevHash);
                insert.Parameters.AddWithValue("$hash", hash);
                await insert.ExecuteNonQueryAsync(ct);
            }

            return new AuditEntry(nextSeq, timestamp, action, ns.Key, nodeId, detail, prevHash, hash);
        }
        finally
        {
            _appendGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(
        string? namespaceKey = null, CancellationToken ct = default)
    {
        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT sequence, timestamp, action, namespace_key, node_id, detail, prev_hash, hash FROM audit" +
            (namespaceKey is null ? "" : " WHERE namespace_key = $ns") +
            " ORDER BY sequence ASC";
        if (namespaceKey is not null)
            cmd.Parameters.AddWithValue("$ns", namespaceKey.ToLowerInvariant());

        var list = new List<AuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapEntry(reader));
        return list;
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

    private static AuditEntry MapEntry(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetInt64(0),
        DateTimeOffset.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
        (AuditAction)r.GetInt32(2),
        r.GetString(3),
        Guid.Parse(r.GetString(4)),
        r.GetString(5),
        r.GetString(6),
        r.GetString(7));
}
