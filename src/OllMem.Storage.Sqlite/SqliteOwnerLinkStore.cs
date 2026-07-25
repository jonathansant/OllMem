using OllMem.Core.Abstractions;

namespace OllMem.Storage.Sqlite;

/// <summary>
/// Persistent owner-link store. Each link is stored twice (A→B and B→A) so a
/// lookup for either owner is a single indexed query; the pair is normalized
/// to lowercase to match <see cref="OllMem.Core.Domain.Namespace"/>'s
/// case-insensitive owner comparison.
/// </summary>
public sealed class SqliteOwnerLinkStore : IOwnerLinkStore
{
    private readonly SqliteStoreContext _ctx;

    public SqliteOwnerLinkStore(SqliteStoreContext ctx) => _ctx = ctx;

    public async ValueTask LinkAsync(string ownerA, string ownerB, CancellationToken ct = default)
    {
        var a = Normalize(ownerA);
        var b = Normalize(ownerB);
        if (a == b)
            return;

        await using var conn = _ctx.OpenConnection();
        foreach (var (x, y) in new[] { (a, b), (b, a) })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT OR IGNORE INTO owner_links (owner_a, owner_b) VALUES ($a, $b)";
            cmd.Parameters.AddWithValue("$a", x);
            cmd.Parameters.AddWithValue("$b", y);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async ValueTask UnlinkAsync(string ownerA, string ownerB, CancellationToken ct = default)
    {
        var a = Normalize(ownerA);
        var b = Normalize(ownerB);

        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM owner_links WHERE (owner_a = $a AND owner_b = $b) OR (owner_a = $b AND owner_b = $a)";
        cmd.Parameters.AddWithValue("$a", a);
        cmd.Parameters.AddWithValue("$b", b);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<IReadOnlySet<string>> GetLinkedOwnersAsync(string owner, CancellationToken ct = default)
    {
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(owner) };
        var frontier = new Queue<string>(closure);

        await using var conn = _ctx.OpenConnection();
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT owner_b FROM owner_links WHERE owner_a = $a";
            cmd.Parameters.AddWithValue("$a", current);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var next = reader.GetString(0);
                if (closure.Add(next))
                    frontier.Enqueue(next);
            }
        }

        return closure;
    }

    private static string Normalize(string owner) => owner.Trim().ToLowerInvariant();
}
