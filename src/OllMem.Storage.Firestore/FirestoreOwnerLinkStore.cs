using Google.Cloud.Firestore;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;

namespace OllMem.Storage.Firestore;

/// <summary>
/// Firestore-backed owner-link store. Each link is one document, keyed by the
/// pair sorted lexicographically so a link between two owners is idempotent
/// and order-independent regardless of which side writes it.
/// </summary>
public sealed class FirestoreOwnerLinkStore : IOwnerLinkStore
{
    private readonly FirestoreStoreContext _ctx;

    public FirestoreOwnerLinkStore(FirestoreStoreContext ctx) => _ctx = ctx;

    public async ValueTask LinkAsync(string ownerA, string ownerB, CancellationToken ct = default)
    {
        var (a, b) = Normalize(ownerA, ownerB);
        if (a == b)
            return;

        await _ctx.OwnerLinks.Document(DocId(a, b)).SetAsync(new Dictionary<string, object>
        {
            ["ownerA"] = a,
            ["ownerB"] = b,
        }, cancellationToken: ct);
    }

    public async ValueTask UnlinkAsync(string ownerA, string ownerB, CancellationToken ct = default)
    {
        var (a, b) = Normalize(ownerA, ownerB);
        await _ctx.OwnerLinks.Document(DocId(a, b)).DeleteAsync(cancellationToken: ct);
    }

    public async ValueTask<IReadOnlySet<string>> GetLinkedOwnersAsync(string owner, CancellationToken ct = default)
    {
        var start = owner.Trim().ToLowerInvariant();
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };

        // Small dataset (owner links, not memories) — a full collection scan
        // per recall is simpler and cheap enough than a graph index.
        var snapshot = await _ctx.OwnerLinks.GetSnapshotAsync(ct);
        var edges = snapshot.Documents
            .Select(d => (A: d.GetValue<string>("ownerA"), B: d.GetValue<string>("ownerB")))
            .ToList();

        var frontier = new Queue<string>([start]);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var (a, b) in edges)
            {
                var other = string.Equals(a, current, StringComparison.OrdinalIgnoreCase) ? b
                    : string.Equals(b, current, StringComparison.OrdinalIgnoreCase) ? a
                    : null;
                if (other is not null && closure.Add(other))
                    frontier.Enqueue(other);
            }
        }

        return closure;
    }

    public async ValueTask<IReadOnlyList<OwnerLink>> GetAllLinksAsync(CancellationToken ct = default)
    {
        var snapshot = await _ctx.OwnerLinks.GetSnapshotAsync(ct);
        return snapshot.Documents
            .Select(d => OwnerLink.Create(d.GetValue<string>("ownerA"), d.GetValue<string>("ownerB")))
            .Distinct()
            .ToList();
    }

    private static (string A, string B) Normalize(string ownerA, string ownerB)
    {
        var a = ownerA.Trim().ToLowerInvariant();
        var b = ownerB.Trim().ToLowerInvariant();
        return string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
    }

    private static string DocId(string a, string b) => $"{a}__{b}";
}
