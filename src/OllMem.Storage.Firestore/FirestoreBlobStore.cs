using System.Security.Cryptography;
using Google.Cloud.Firestore;
using OllMem.Core.Abstractions;

namespace OllMem.Storage.Firestore;

/// <summary>
/// Firestore-backed, content-addressed blob store (PRD REQ-3.2). The reference
/// is the SHA-256 of the (encrypted) bytes, so identical payloads dedupe and
/// every reference is independently verifiable. Writes are idempotent: the
/// document id is the hash, so re-putting the same payload overwrites it with
/// identical content.
/// </summary>
public sealed class FirestoreBlobStore : IBlobStore
{
    private readonly FirestoreStoreContext _ctx;

    public FirestoreBlobStore(FirestoreStoreContext ctx) => _ctx = ctx;

    public async ValueTask<string> PutAsync(ReadOnlyMemory<byte> encrypted, CancellationToken ct = default)
    {
        var bytes = encrypted.ToArray();
        var blobRef = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await _ctx.Blobs.Document(blobRef)
            .SetAsync(new Dictionary<string, object> { ["data"] = Blob.CopyFrom(bytes) }, SetOptions.Overwrite, ct);
        return blobRef;
    }

    public async ValueTask<byte[]?> GetAsync(string blobRef, CancellationToken ct = default)
    {
        var snapshot = await _ctx.Blobs.Document(blobRef.ToLowerInvariant()).GetSnapshotAsync(ct);
        return snapshot.Exists ? snapshot.GetValue<Blob>("data").ByteString.ToByteArray() : null;
    }
}
