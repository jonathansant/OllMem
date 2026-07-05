using System.Security.Cryptography;
using OllMem.Core.Abstractions;

namespace OllMem.Storage.Git;

/// <summary>
/// Git-backed, content-addressed blob store (PRD REQ-3.2). The reference is
/// the SHA-256 of the (already encrypted) bytes, so identical payloads dedupe
/// and — because a ref can only ever name one byte sequence — blob files can
/// never produce a meaningful merge conflict between machines.
/// </summary>
public sealed class GitBlobStore : IBlobStore
{
    private readonly GitStoreContext _ctx;

    public GitBlobStore(GitStoreContext ctx) => _ctx = ctx;

    public async ValueTask<string> PutAsync(ReadOnlyMemory<byte> encrypted, CancellationToken ct = default)
    {
        var bytes = encrypted.ToArray();
        var blobRef = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await _ctx.Gate.WaitAsync(ct);
        try
        {
            var path = PathFor(blobRef)!;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, ct);
                _ctx.NotifyChanged();
            }

            return blobRef;
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    public async ValueTask<byte[]?> GetAsync(string blobRef, CancellationToken ct = default)
    {
        var path = PathFor(blobRef.ToLowerInvariant());
        if (path is null)
            return null;

        await _ctx.Gate.WaitAsync(ct);
        try
        {
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    // Fan out over a two-hex-char prefix directory so no single directory has
    // to hold every blob. Non-hex refs can't be ours (and must never be able
    // to escape the blobs directory), so they resolve to null.
    private string? PathFor(string blobRef) =>
        blobRef.Length > 2 && blobRef.All(Uri.IsHexDigit)
            ? Path.Combine(_ctx.BlobsDirectory, blobRef[..2], blobRef)
            : null;
}
