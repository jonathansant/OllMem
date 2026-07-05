namespace OllMem.Core.Abstractions;

/// <summary>
/// Persistent, content-addressed storage for encrypted raw payloads
/// (PRD REQ-3.2). The abstraction stands in for verifiable networks such as
/// Walrus or IPFS; the default implementation keeps blobs in memory / on disk.
/// </summary>
public interface IBlobStore
{
    /// <summary>
    /// Store an (already encrypted) payload and return its content address —
    /// a hash of the bytes, so identical payloads dedupe and references are
    /// independently verifiable.
    /// </summary>
    ValueTask<string> PutAsync(ReadOnlyMemory<byte> encrypted, CancellationToken ct = default);

    ValueTask<byte[]?> GetAsync(string blobRef, CancellationToken ct = default);
}
