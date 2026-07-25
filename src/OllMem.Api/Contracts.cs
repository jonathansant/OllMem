using OllMem.Core.Domain;

namespace OllMem.Api;

/// <summary>Shared namespace coordinates carried by every request.</summary>
public abstract record ScopedRequest
{
    /// <summary>Owner identity. With signature auth on, must equal the signing public key.</summary>
    public string Owner { get; init; } = "";

    /// <summary>One of: user, agent, session.</summary>
    public string Scope { get; init; } = "user";

    /// <summary>Identifier within the scope (user id, agent id, session id).</summary>
    public string ScopeId { get; init; } = "default";
}

public sealed record RememberRequest : ScopedRequest
{
    public string Statement { get; init; } = "";

    // Optional explicit triple; if omitted a default ("user note") edge is used.
    public string? Subject { get; init; }
    public string? Predicate { get; init; }
    public string? Object { get; init; }
}

public sealed record ExtractRequest : ScopedRequest
{
    public string Transcript { get; init; } = "";
}

public sealed record RecallRequest : ScopedRequest
{
    public string Query { get; init; } = "";
    public int TopK { get; init; } = 5;
}

public sealed record EdgeDto(string Subject, string Predicate, string Object)
{
    public static EdgeDto From(GraphEdge e) => new(e.Subject, e.Predicate, e.Object);
}

public sealed record MemoryDto(
    Guid Id,
    string Statement,
    EdgeDto Edge,
    string Status,
    string? BlobRef,
    int AccessCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt)
{
    public static MemoryDto From(MemoryNode n) => new(
        n.Id, n.Statement, EdgeDto.From(n.Edge), n.Status.ToString(),
        n.BlobRef, n.AccessCount, n.CreatedAt, n.LastAccessedAt);
}

public sealed record RememberResponse(MemoryDto Memory, Guid? SupersededId);

public sealed record NamespaceDto(string Owner, string Scope, string ScopeId)
{
    public static NamespaceDto From(Namespace ns) => new(ns.Owner, ns.Scope.ToString(), ns.ScopeId);
}

public sealed record ScoredMemoryDto(
    MemoryDto Memory,
    NamespaceDto Namespace,
    double Similarity,
    double DecayWeight,
    double NamespaceAffinity,
    double FinalScore)
{
    public static ScoredMemoryDto From(ScoredMemory s) => new(
        MemoryDto.From(s.Node),
        NamespaceDto.From(s.Node.Namespace),
        Math.Round(s.Similarity, 4),
        Math.Round(s.DecayWeight, 4),
        Math.Round(s.NamespaceAffinity, 4),
        Math.Round(s.FinalScore, 4));
}

public sealed record KeyPairResponse(string PublicKeyHex, string PrivateKeyHex);

/// <summary>A namespace present in the graph dump served to the browser visualizer.</summary>
public sealed record GraphNamespaceDto(string Key, string Owner, string Scope, string ScopeId, int MemoryCount);

/// <summary>An entity in the graph dump; entities are scoped to their namespace.</summary>
public sealed record GraphNodeDto(string Id, string Ns, int Degree);

/// <summary>A memory-backed relationship in the graph dump, with metadata for display.</summary>
public sealed record GraphEdgeDto(
    Guid MemoryId,
    string Ns,
    string Subject,
    string Predicate,
    string Object,
    string Statement,
    int AccessCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt)
{
    public static GraphEdgeDto From(MemoryNode n) => new(
        n.Id, n.Namespace.Key, n.Edge.Subject, n.Edge.Predicate, n.Edge.Object,
        n.Statement, n.AccessCount, n.CreatedAt, n.LastAccessedAt);
}

public sealed record GraphResponse(
    IReadOnlyList<GraphNamespaceDto> Namespaces,
    IReadOnlyList<GraphNodeDto> Nodes,
    IReadOnlyList<GraphEdgeDto> Edges)
{
    public static GraphResponse From(IReadOnlyList<MemoryNode> memories)
    {
        var edges = memories.Select(GraphEdgeDto.From).ToList();

        // Entities never merge across namespaces (one owner's "user" is not
        // another's); within a namespace they merge case-insensitively because
        // subjects/predicates are stored lower-cased but objects keep their
        // original casing (GraphEdge.Create).
        var nodes = edges
            .SelectMany(e => new[] { (e.Ns, Name: e.Subject), (e.Ns, Name: e.Object) })
            .GroupBy(x => (x.Ns, Key: x.Name.ToLowerInvariant()))
            .Select(g => new GraphNodeDto(g.First().Name, g.Key.Ns, g.Count()))
            .ToList();

        var namespaces = memories
            .GroupBy(m => m.Namespace.Key)
            .Select(g =>
            {
                var ns = g.First().Namespace;
                return new GraphNamespaceDto(
                    ns.Key, ns.Owner, ns.Scope.ToString().ToLowerInvariant(), ns.ScopeId, g.Count());
            })
            .OrderBy(n => n.Key)
            .ToList();

        return new GraphResponse(namespaces, nodes, edges);
    }
}
