using System.Text.Json;
using System.Text.Json.Serialization;
using OllMem.Core.Abstractions;

namespace OllMem.Storage.Git;

/// <summary>
/// Git-backed owner-link store: one JSON file per linked pair, named by the
/// pair sorted lexicographically so the filename is canonical regardless of
/// argument order — like <see cref="GitBlobStore"/>, this makes the file
/// content-addressed by its own name, so two machines linking the same pair
/// concurrently can never produce a real merge conflict.
/// </summary>
public sealed class GitOwnerLinkStore : IOwnerLinkStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly GitStoreContext _ctx;

    public GitOwnerLinkStore(GitStoreContext ctx) => _ctx = ctx;

    public async ValueTask LinkAsync(string ownerA, string ownerB, CancellationToken ct = default)
    {
        var (a, b) = Normalize(ownerA, ownerB);
        if (a == b)
            return;

        await _ctx.Gate.WaitAsync(ct);
        try
        {
            var path = PathFor(a, b);
            if (!File.Exists(path))
            {
                var json = JsonSerializer.Serialize(new LinkDocument(a, b), Json) + "\n";
                await File.WriteAllTextAsync(path, json, ct);
                _ctx.NotifyChanged();
            }
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    public async ValueTask UnlinkAsync(string ownerA, string ownerB, CancellationToken ct = default)
    {
        var (a, b) = Normalize(ownerA, ownerB);

        await _ctx.Gate.WaitAsync(ct);
        try
        {
            var path = PathFor(a, b);
            if (File.Exists(path))
            {
                File.Delete(path);
                _ctx.NotifyChanged();
            }
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    public async ValueTask<IReadOnlySet<string>> GetLinkedOwnersAsync(string owner, CancellationToken ct = default)
    {
        var start = owner.Trim().ToLowerInvariant();
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };

        await _ctx.Gate.WaitAsync(ct);
        try
        {
            // Small dataset (owner links, not memories) — a full scan per
            // recall is simpler and cheap enough than a graph index on disk.
            var edges = Directory.Exists(_ctx.LinksDirectory)
                ? Directory.EnumerateFiles(_ctx.LinksDirectory, "*.json")
                    .Select(ReadLink)
                    .OfType<LinkDocument>()
                    .ToList()
                : [];

            var frontier = new Queue<string>([start]);
            while (frontier.Count > 0)
            {
                var current = frontier.Dequeue();
                foreach (var link in edges)
                {
                    var other = string.Equals(link.OwnerA, current, StringComparison.OrdinalIgnoreCase) ? link.OwnerB
                        : string.Equals(link.OwnerB, current, StringComparison.OrdinalIgnoreCase) ? link.OwnerA
                        : null;
                    if (other is not null && closure.Add(other))
                        frontier.Enqueue(other);
                }
            }
        }
        finally
        {
            _ctx.Gate.Release();
        }

        return closure;
    }

    private static (string A, string B) Normalize(string ownerA, string ownerB)
    {
        var a = ownerA.Trim().ToLowerInvariant();
        var b = ownerB.Trim().ToLowerInvariant();
        return string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
    }

    private string PathFor(string a, string b) =>
        Path.Combine(_ctx.LinksDirectory, $"{Uri.EscapeDataString(a)}__{Uri.EscapeDataString(b)}.json");

    private static LinkDocument? ReadLink(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<LinkDocument>(File.ReadAllText(file), Json);
        }
        catch (JsonException)
        {
            // A torn or foreign file must not take recall down; skip it.
            return null;
        }
    }

    private sealed record LinkDocument(string OwnerA, string OwnerB);
}
