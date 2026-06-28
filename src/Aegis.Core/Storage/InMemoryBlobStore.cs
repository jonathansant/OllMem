using System.Collections.Concurrent;
using System.Security.Cryptography;
using Aegis.Core.Abstractions;

namespace Aegis.Core.Storage;

/// <summary>
/// Content-addressed in-memory blob store. The blob reference is the SHA-256 of
/// the (encrypted) bytes, giving deduplication and independent verifiability —
/// the same contract a Walrus / IPFS backend would expose (PRD REQ-3.2).
/// </summary>
public sealed class InMemoryBlobStore : IBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    public ValueTask<string> PutAsync(ReadOnlyMemory<byte> encrypted, CancellationToken ct = default)
    {
        var bytes = encrypted.ToArray();
        var blobRef = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        _blobs.TryAdd(blobRef, bytes);
        return ValueTask.FromResult(blobRef);
    }

    public ValueTask<byte[]?> GetAsync(string blobRef, CancellationToken ct = default) =>
        ValueTask.FromResult(_blobs.GetValueOrDefault(blobRef.ToLowerInvariant()));
}
