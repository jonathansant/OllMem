using System.Globalization;
using System.Runtime.InteropServices;
using Google.Cloud.Firestore;
using OllMem.Core.Domain;

namespace OllMem.Storage.Firestore;

/// <summary>
/// Firestore document shape for a <see cref="MemoryNode"/>. Timestamps are ISO
/// 8601 round-trip strings (offset-preserving, same convention as the SQLite
/// store); the embedding is a compact little-endian float blob.
/// </summary>
[FirestoreData]
internal sealed class MemoryNodeDocument
{
    [FirestoreProperty("namespaceKey")] public string NamespaceKey { get; set; } = "";
    [FirestoreProperty("owner")] public string Owner { get; set; } = "";
    [FirestoreProperty("scope")] public int Scope { get; set; }
    [FirestoreProperty("scopeId")] public string ScopeId { get; set; } = "";
    [FirestoreProperty("statement")] public string Statement { get; set; } = "";
    [FirestoreProperty("subject")] public string Subject { get; set; } = "";
    [FirestoreProperty("predicate")] public string Predicate { get; set; } = "";
    [FirestoreProperty("object")] public string Object { get; set; } = "";
    [FirestoreProperty("embedding")] public Blob Embedding { get; set; }
    [FirestoreProperty("blobRef")] public string? BlobRef { get; set; }
    [FirestoreProperty("createdAt")] public string CreatedAt { get; set; } = "";
    [FirestoreProperty("lastAccessedAt")] public string LastAccessedAt { get; set; } = "";
    [FirestoreProperty("accessCount")] public int AccessCount { get; set; }
    [FirestoreProperty("status")] public int Status { get; set; }
    [FirestoreProperty("supersededBy")] public string? SupersededBy { get; set; }
    [FirestoreProperty("retiredAt")] public string? RetiredAt { get; set; }

    public static MemoryNodeDocument From(MemoryNode node) => new()
    {
        NamespaceKey = node.Namespace.Key,
        // Normalised like namespaceKey/subject/predicate so owner-equality
        // queries (FirestoreHybridStore.SearchOwnersAsync) can match without a
        // server-side lower(); Namespace.Key is case-insensitive anyway, so
        // nothing depends on the caller's original casing being preserved.
        Owner = node.Namespace.Owner.ToLowerInvariant(),
        Scope = (int)node.Namespace.Scope,
        ScopeId = node.Namespace.ScopeId,
        Statement = node.Statement,
        Subject = node.Edge.Subject,
        Predicate = node.Edge.Predicate,
        Object = node.Edge.Object,
        Embedding = Blob.CopyFrom(EmbeddingToBytes(node.Embedding)),
        BlobRef = node.BlobRef,
        CreatedAt = Iso(node.CreatedAt),
        LastAccessedAt = Iso(node.LastAccessedAt),
        AccessCount = node.AccessCount,
        Status = (int)node.Status,
        SupersededBy = node.SupersededBy?.ToString(),
        RetiredAt = node.RetiredAt is { } retired ? Iso(retired) : null,
    };

    public MemoryNode ToNode(Guid id) => new()
    {
        Id = id,
        Namespace = new Namespace(Owner, (MemoryScope)Scope, ScopeId),
        Statement = Statement,
        Edge = new GraphEdge(Subject, Predicate, Object),
        Embedding = EmbeddingFromBytes(Embedding.ByteString.ToByteArray()),
        BlobRef = BlobRef,
        CreatedAt = ParseIso(CreatedAt),
        LastAccessedAt = ParseIso(LastAccessedAt),
        AccessCount = AccessCount,
        Status = (MemoryStatus)Status,
        SupersededBy = SupersededBy is null ? null : Guid.Parse(SupersededBy),
        RetiredAt = string.IsNullOrEmpty(RetiredAt) ? null : ParseIso(RetiredAt),
    };

    internal static byte[] EmbeddingToBytes(float[] vector) =>
        MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    internal static float[] EmbeddingFromBytes(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * sizeof(float));
        return floats;
    }

    internal static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
