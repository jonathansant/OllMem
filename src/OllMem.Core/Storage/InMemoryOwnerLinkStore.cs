using OllMem.Core.Abstractions;
using OllMem.Core.Domain;

namespace OllMem.Core.Storage;

/// <summary>Process-local default: owner links do not survive a restart.</summary>
public sealed class InMemoryOwnerLinkStore : IOwnerLinkStore
{
    // All access is serialised through _gate, so a plain dictionary is enough.
    private readonly Dictionary<string, HashSet<string>> _edges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public ValueTask LinkAsync(string ownerA, string ownerB, CancellationToken ct = default)
    {
        var a = Normalize(ownerA);
        var b = Normalize(ownerB);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return ValueTask.CompletedTask;

        lock (_gate)
        {
            Edges(a).Add(b);
            Edges(b).Add(a);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask UnlinkAsync(string ownerA, string ownerB, CancellationToken ct = default)
    {
        var a = Normalize(ownerA);
        var b = Normalize(ownerB);
        lock (_gate)
        {
            if (_edges.TryGetValue(a, out var fromA))
                fromA.Remove(b);
            if (_edges.TryGetValue(b, out var fromB))
                fromB.Remove(a);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlySet<string>> GetLinkedOwnersAsync(string owner, CancellationToken ct = default)
    {
        var start = Normalize(owner);
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        lock (_gate)
        {
            var frontier = new Queue<string>([start]);
            while (frontier.Count > 0)
            {
                if (!_edges.TryGetValue(frontier.Dequeue(), out var neighbours))
                    continue;

                foreach (var next in neighbours)
                {
                    if (closure.Add(next))
                        frontier.Enqueue(next);
                }
            }
        }

        return ValueTask.FromResult<IReadOnlySet<string>>(closure);
    }

    public ValueTask<IReadOnlyList<OwnerLink>> GetAllLinksAsync(CancellationToken ct = default)
    {
        var links = new HashSet<OwnerLink>();
        lock (_gate)
        {
            foreach (var (owner, neighbours) in _edges)
            {
                foreach (var other in neighbours)
                    links.Add(OwnerLink.Create(owner, other));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<OwnerLink>>(links.ToList());
    }

    // Only called from the write paths, so an unknown owner is genuinely being
    // linked — reads use TryGetValue and never materialise an empty entry.
    private HashSet<string> Edges(string owner)
    {
        if (!_edges.TryGetValue(owner, out var set))
            _edges[owner] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return set;
    }

    // Lower-cased to match the persistent link stores, so the owner set handed
    // to IVectorStore.SearchOwnersAsync has the same shape on every backend.
    private static string Normalize(string owner) => owner.Trim().ToLowerInvariant();
}
