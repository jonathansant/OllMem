using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using ModelContextProtocol.Server;

namespace OllMem.Mcp;

/// <summary>
/// Exposes the OllMem memory layer to MCP clients (PRD REQ-1.2 — zero-config
/// integration via the Model Context Protocol). Each method becomes an MCP tool;
/// the <see cref="IMemoryEngine"/> parameter is injected from DI while the
/// remaining parameters form the tool's input schema.
/// </summary>
[McpServerToolType]
public static class OllMemMemoryTools
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    [McpServerTool(Name = "ollmem_remember")]
    [Description("Store a single fact in the agent's permanent memory. Scope is one of: user, agent, session. " +
                 "Optionally provide an explicit subject/predicate/object relationship triple; contradicting " +
                 "facts for the same subject+predicate automatically supersede the old value.")]
    public static async Task<string> RememberAsync(
        IMemoryEngine engine,
        [Description("Owner identity (e.g. tenant or signing key).")] string owner,
        [Description("Memory tier: user, agent, or session.")] string scope,
        [Description("Identifier within the scope (user id, agent id, or session id).")] string scopeId,
        [Description("The fact to remember, as a natural-language statement.")] string statement,
        [Description("Optional relationship subject, e.g. 'user'.")] string? subject = null,
        [Description("Optional relationship predicate, e.g. 'lives in'.")] string? predicate = null,
        [Description("Optional relationship object, e.g. 'San Francisco'.")] string? @object = null,
        CancellationToken ct = default)
    {
        var ns = ResolveNamespace(owner, scope, scopeId);

        GraphEdge? edge = null;
        if (!string.IsNullOrWhiteSpace(subject) &&
            !string.IsNullOrWhiteSpace(predicate) &&
            !string.IsNullOrWhiteSpace(@object))
        {
            edge = GraphEdge.Create(subject!, predicate!, @object!);
        }

        var result = await engine.RememberAsync(ns, statement, edge, ct);
        return Serialize(new
        {
            stored = ToMemory(result.Node),
            supersededId = result.SupersededId,
        });
    }

    [McpServerTool(Name = "ollmem_extract")]
    [Description("Automatically extract standalone facts, preferences and state changes from a raw conversation " +
                 "transcript and store them. Returns the facts that were persisted. Use lines like 'User: ...' " +
                 "so the agent's own utterances are ignored.")]
    public static async Task<string> ExtractAsync(
        IMemoryEngine engine,
        [Description("Owner identity (e.g. tenant or signing key).")] string owner,
        [Description("Memory tier: user, agent, or session.")] string scope,
        [Description("Identifier within the scope.")] string scopeId,
        [Description("Raw conversation transcript to mine for facts.")] string transcript,
        CancellationToken ct = default)
    {
        var ns = ResolveNamespace(owner, scope, scopeId);
        var results = await engine.ExtractAndStoreAsync(ns, transcript, ct);
        return Serialize(new
        {
            count = results.Count,
            stored = results.Select(r => new { memory = ToMemory(r.Node), supersededId = r.SupersededId }),
        });
    }

    [McpServerTool(Name = "ollmem_recall")]
    [Description("Recall the most relevant memories for a query using hybrid semantic search with time-aware " +
                 "reranking (recent and frequently used facts are prioritised). Returns ranked memories with scores.")]
    public static async Task<string> RecallAsync(
        IMemoryEngine engine,
        [Description("Owner identity (e.g. tenant or signing key).")] string owner,
        [Description("Memory tier: user, agent, or session.")] string scope,
        [Description("Identifier within the scope.")] string scopeId,
        [Description("Natural-language query describing what to recall.")] string query,
        [Description("Maximum number of memories to return (1-100). Default 5.")] int topK = 5,
        CancellationToken ct = default)
    {
        var ns = ResolveNamespace(owner, scope, scopeId);
        var results = await engine.RecallAsync(ns, query, Math.Clamp(topK, 1, 100), ct);
        return Serialize(results.Select(s => new
        {
            memory = ToMemory(s.Node),
            similarity = Math.Round(s.Similarity, 4),
            decayWeight = Math.Round(s.DecayWeight, 4),
            finalScore = Math.Round(s.FinalScore, 4),
        }));
    }

    [McpServerTool(Name = "ollmem_neighbors")]
    [Description("Traverse the knowledge graph: list the relationships (edges) for an entity, optionally filtered " +
                 "by predicate. For example subject='user' returns everything currently known about the user.")]
    public static async Task<string> NeighborsAsync(
        IMemoryEngine engine,
        [Description("Owner identity (e.g. tenant or signing key).")] string owner,
        [Description("Memory tier: user, agent, or session.")] string scope,
        [Description("Identifier within the scope.")] string scopeId,
        [Description("Entity to inspect, e.g. 'user'.")] string subject,
        [Description("Optional predicate filter, e.g. 'lives in'.")] string? predicate = null,
        CancellationToken ct = default)
    {
        var ns = ResolveNamespace(owner, scope, scopeId);
        var edges = await engine.NeighborsAsync(ns, subject, predicate, ct);
        return Serialize(edges.Select(e => new { e.Subject, e.Predicate, e.Object }));
    }

    private static Namespace ResolveNamespace(string owner, string scope, string scopeId)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("owner is required.", nameof(owner));
        if (!Enum.TryParse<MemoryScope>(scope, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException("scope must be one of: user, agent, session.", nameof(scope));

        return new Namespace(owner, parsed, string.IsNullOrWhiteSpace(scopeId) ? "default" : scopeId);
    }

    private static object ToMemory(MemoryNode n) => new
    {
        id = n.Id,
        statement = n.Statement,
        edge = new { n.Edge.Subject, n.Edge.Predicate, n.Edge.Object },
        status = n.Status.ToString(),
        accessCount = n.AccessCount,
        createdAt = n.CreatedAt,
        lastAccessedAt = n.LastAccessedAt,
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
