using Aegis.Core.Domain;

namespace Aegis.Api;

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

public sealed record ScoredMemoryDto(
    MemoryDto Memory,
    double Similarity,
    double DecayWeight,
    double FinalScore)
{
    public static ScoredMemoryDto From(ScoredMemory s) => new(
        MemoryDto.From(s.Node),
        Math.Round(s.Similarity, 4),
        Math.Round(s.DecayWeight, 4),
        Math.Round(s.FinalScore, 4));
}

public sealed record KeyPairResponse(string PublicKeyHex, string PrivateKeyHex);
