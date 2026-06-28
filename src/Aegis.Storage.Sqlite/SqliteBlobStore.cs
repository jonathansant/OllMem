using System.Security.Cryptography;
using Aegis.Core.Abstractions;

namespace Aegis.Storage.Sqlite;

/// <summary>
/// SQLite-backed, content-addressed blob store (PRD REQ-3.2). The reference is
/// the SHA-256 of the (encrypted) bytes, so identical payloads dedupe and every
/// reference is independently verifiable.
/// </summary>
public sealed class SqliteBlobStore : IBlobStore
{
    private readonly SqliteStoreContext _ctx;

    public SqliteBlobStore(SqliteStoreContext ctx) => _ctx = ctx;

    public async ValueTask<string> PutAsync(ReadOnlyMemory<byte> encrypted, CancellationToken ct = default)
    {
        var bytes = encrypted.ToArray();
        var blobRef = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO blobs (blob_ref, data) VALUES ($ref, $data)";
        cmd.Parameters.AddWithValue("$ref", blobRef);
        cmd.Parameters.AddWithValue("$data", bytes);
        await cmd.ExecuteNonQueryAsync(ct);

        return blobRef;
    }

    public async ValueTask<byte[]?> GetAsync(string blobRef, CancellationToken ct = default)
    {
        await using var conn = _ctx.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM blobs WHERE blob_ref = $ref";
        cmd.Parameters.AddWithValue("$ref", blobRef.ToLowerInvariant());

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as byte[];
    }
}
