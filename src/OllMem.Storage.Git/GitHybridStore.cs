using System.Text.Json;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Embeddings;

namespace OllMem.Storage.Git;

/// <summary>
/// Git-backed hybrid datastore (PRD REQ-3.1): one JSON document per memory
/// node, partitioned into a directory per namespace. Vector search scans the
/// namespace's documents, graph queries filter the same documents by their
/// triple — a node file is both views, so superseding a fact removes it from
/// vector and graph results atomically (and in the same commit).
/// </summary>
public sealed class GitHybridStore : IVectorStore, IGraphStore
{
    private readonly GitStoreContext _ctx;

    public GitHybridStore(GitStoreContext ctx) => _ctx = ctx;

    // ---- IVectorStore -------------------------------------------------------

    public async ValueTask UpsertAsync(MemoryNode node, CancellationToken ct = default)
    {
        await _ctx.Gate.WaitAsync(ct);
        try
        {
            var dir = DirectoryFor(node.Namespace);
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(MemoryNodeDocument.From(node), GitJson.Document) + "\n";
            await File.WriteAllTextAsync(Path.Combine(dir, $"{node.Id}.json"), json, ct);
            _ctx.NotifyChanged();
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    public async ValueTask<MemoryNode?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await _ctx.Gate.WaitAsync(ct);
        try
        {
            var file = Directory
                .EnumerateFiles(_ctx.NodesDirectory, $"{id}.json", SearchOption.AllDirectories)
                .FirstOrDefault();
            return file is null ? null : ReadNode(file);
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<(MemoryNode Node, double Similarity)>> SearchAsync(
        Namespace ns, float[] queryEmbedding, int topK, CancellationToken ct = default)
    {
        var active = await ListActiveAsync(ns, ct);
        return active
            // Documents embedded by a different model (different dimensionality)
            // are in an incompatible vector space; skip them rather than crash.
            .Where(n => n.Embedding.Length == queryEmbedding.Length)
            .Select(n => (Node: n, Similarity: HashingEmbeddingService.CosineSimilarity(queryEmbedding, n.Embedding)))
            .OrderByDescending(x => x.Similarity)
            .Take(Math.Max(0, topK))
            .ToList();
    }

    public async ValueTask<IReadOnlyList<(MemoryNode Node, double Similarity)>> SearchOwnersAsync(
        IReadOnlySet<string> owners, float[] queryEmbedding, int topK, CancellationToken ct = default)
    {
        if (owners.Count == 0)
            return [];

        await _ctx.Gate.WaitAsync(ct);
        try
        {
            // The top level of nodes/ is one directory per owner, named from the
            // lower-cased owner, so this reads only the requested owners' files
            // instead of walking the whole tree.
            var files = owners
                .Select(o => Path.Combine(_ctx.NodesDirectory, EscapeSegment(o)))
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories));

            return files
                .Select(ReadNode)
                .OfType<MemoryNode>()
                .Where(n => n.Status == MemoryStatus.Active && n.Embedding.Length == queryEmbedding.Length)
                .Select(n => (Node: n, Similarity: HashingEmbeddingService.CosineSimilarity(queryEmbedding, n.Embedding)))
                .OrderByDescending(x => x.Similarity)
                .Take(Math.Max(0, topK))
                .ToList();
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<MemoryNode>> ListActiveAsync(
        Namespace ns, CancellationToken ct = default)
    {
        await _ctx.Gate.WaitAsync(ct);
        try
        {
            return ReadNamespace(ns).Where(n => n.Status == MemoryStatus.Active).ToList();
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<MemoryNode>> ListAllActiveAsync(CancellationToken ct = default)
    {
        await _ctx.Gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(_ctx.NodesDirectory))
                return [];
            return Directory
                .EnumerateFiles(_ctx.NodesDirectory, "*.json", SearchOption.AllDirectories)
                .Select(ReadNode)
                .OfType<MemoryNode>()
                .Where(n => n.Status == MemoryStatus.Active)
                .ToList();
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    // ---- IGraphStore --------------------------------------------------------

    // The node document is written by the vector upsert; the graph view reads
    // the same documents, so AddAsync is an idempotent upsert.
    public ValueTask AddAsync(Namespace ns, MemoryNode node, CancellationToken ct = default) =>
        UpsertAsync(node, ct);

    // Superseding flips status, which graph queries already exclude, so the
    // edge "disappears" without losing the document (history is preserved).
    public ValueTask RemoveAsync(Namespace ns, Guid nodeId, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public async ValueTask<IReadOnlyList<GraphEdge>> NeighborsAsync(
        Namespace ns, string subject, string? predicate = null, CancellationToken ct = default)
    {
        var subjectKey = subject.Trim().ToLowerInvariant();
        var predicateKey = predicate?.Trim().ToLowerInvariant();

        await _ctx.Gate.WaitAsync(ct);
        try
        {
            return ReadNamespace(ns)
                .Where(n => n.Status == MemoryStatus.Active && n.Edge.Subject == subjectKey)
                .Where(n => predicateKey is null || n.Edge.Predicate == predicateKey)
                .Select(n => n.Edge)
                .ToList();
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    public async ValueTask<Guid?> FindByRelationAsync(
        Namespace ns, string subject, string predicate, CancellationToken ct = default)
    {
        var relationKey = $"{subject.Trim().ToLowerInvariant()}|{predicate.Trim().ToLowerInvariant()}";

        await _ctx.Gate.WaitAsync(ct);
        try
        {
            return ReadNamespace(ns)
                .Where(n => n.Status == MemoryStatus.Active && n.Edge.RelationKey == relationKey)
                .OrderByDescending(n => n.CreatedAt)
                .Select(n => (Guid?)n.Id)
                .FirstOrDefault();
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    // ---- helpers ------------------------------------------------------------

    private string DirectoryFor(Namespace ns) => Path.Combine(
        _ctx.NodesDirectory,
        EscapeSegment(ns.Owner),
        ns.Scope.ToString().ToLowerInvariant(),
        EscapeSegment(ns.ScopeId));

    // Namespace identity is case-insensitive (Namespace.Key lower-cases), so
    // the directory name is derived from the lowered value; escaping keeps
    // arbitrary owner/scope ids filesystem- and git-safe.
    private static string EscapeSegment(string value) =>
        Uri.EscapeDataString(value.ToLowerInvariant());

    private IEnumerable<MemoryNode> ReadNamespace(Namespace ns)
    {
        var dir = DirectoryFor(ns);
        if (!Directory.Exists(dir))
            yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            if (ReadNode(file) is { } node)
                yield return node;
        }
    }

    private static MemoryNode? ReadNode(string file)
    {
        try
        {
            return JsonSerializer
                .Deserialize<MemoryNodeDocument>(File.ReadAllText(file), GitJson.Document)?
                .ToNode();
        }
        catch (JsonException)
        {
            // A torn or foreign file must not take recall down; skip it.
            return null;
        }
    }
}
