using System.ComponentModel;
using System.Globalization;
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
            outcome = result.Outcome.ToString(),
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
                 "reranking (recent and frequently used facts are prioritised). Returns ranked memories with scores. " +
                 "Pass asOf to answer as the memory stood at a past instant instead of now.")]
    public static async Task<string> RecallAsync(
        IMemoryEngine engine,
        [Description("Owner identity (e.g. tenant or signing key).")] string owner,
        [Description("Memory tier: user, agent, or session.")] string scope,
        [Description("Identifier within the scope.")] string scopeId,
        [Description("Natural-language query describing what to recall.")] string query,
        [Description("Maximum number of memories to return (1-100). Default 5.")] int topK = 5,
        [Description("Optional ISO-8601 instant, e.g. '2026-01-31T00:00:00Z'. Recall the memory as it stood " +
                     "then: facts recorded later are invisible and facts retired since are current again. " +
                     "A historical recall does not count as an access.")] string? asOf = null,
        CancellationToken ct = default)
    {
        var ns = ResolveNamespace(owner, scope, scopeId);
        var results = await engine.RecallAsync(ns, query, Math.Clamp(topK, 1, 100), ParseAsOf(asOf), ct);
        return Serialize(results.Select(s => new
        {
            memory = ToMemory(s.Node),
            @namespace = new { s.Node.Namespace.Owner, scope = s.Node.Namespace.Scope.ToString(), s.Node.Namespace.ScopeId },
            similarity = Math.Round(s.Similarity, 4),
            decayWeight = Math.Round(s.DecayWeight, 4),
            namespaceAffinity = Math.Round(s.NamespaceAffinity, 4),
            finalScore = Math.Round(s.FinalScore, 4),
        }));
    }

    [McpServerTool(Name = "ollmem_neighbors")]
    [Description("Traverse the knowledge graph: list the relationships (edges) for an entity, optionally filtered " +
                 "by predicate. For example subject='user' returns everything currently known about the user. " +
                 "Pass asOf to traverse the graph as it stood at a past instant.")]
    public static async Task<string> NeighborsAsync(
        IMemoryEngine engine,
        [Description("Owner identity (e.g. tenant or signing key).")] string owner,
        [Description("Memory tier: user, agent, or session.")] string scope,
        [Description("Identifier within the scope.")] string scopeId,
        [Description("Entity to inspect, e.g. 'user'.")] string subject,
        [Description("Optional predicate filter, e.g. 'lives in'.")] string? predicate = null,
        [Description("Optional ISO-8601 instant to traverse the graph as it stood then.")] string? asOf = null,
        CancellationToken ct = default)
    {
        var ns = ResolveNamespace(owner, scope, scopeId);
        var edges = await engine.NeighborsAsync(ns, subject, predicate, ParseAsOf(asOf), ct);
        return Serialize(edges.Select(e => new { e.Subject, e.Predicate, e.Object }));
    }

    [McpServerTool(Name = "ollmem_history")]
    [Description("Show how a relationship changed over time: every version of it, oldest first, each with the " +
                 "window it was true for. validTo is null for the version that is still current. Use this to " +
                 "answer 'what did we believe before' and 'when did this change'.")]
    public static async Task<string> HistoryAsync(
        IMemoryEngine engine,
        [Description("Owner identity (e.g. tenant or signing key).")] string owner,
        [Description("Memory tier: user, agent, or session.")] string scope,
        [Description("Identifier within the scope.")] string scopeId,
        [Description("Entity to inspect, e.g. 'user'.")] string subject,
        [Description("Optional predicate filter, e.g. 'lives in'.")] string? predicate = null,
        CancellationToken ct = default)
    {
        var ns = ResolveNamespace(owner, scope, scopeId);
        var entries = await engine.HistoryAsync(ns, subject, predicate, ct);
        return Serialize(new
        {
            count = entries.Count,
            entries = entries.Select(e => new
            {
                memory = ToMemory(e.Node),
                validFrom = e.ValidFrom,
                validTo = e.ValidTo,
                current = e.ValidTo is null,
            }),
        });
    }

    [McpServerTool(Name = "ollmem_consolidate")]
    [Description("Wrap up a session: summarise everything stored in a session scope into durable facts, save " +
                 "them in an agent or user scope, and mark the session memories consolidated so they leave " +
                 "recall but stay in history. Run with dryRun=true first to see what would be promoted. " +
                 "Safe to run twice — the second run finds nothing left to promote.")]
    public static async Task<string> ConsolidateAsync(
        IMemoryEngine engine,
        [Description("Owner identity (e.g. tenant or signing key).")] string owner,
        [Description("The session id to drain (the scopeId of the session-scoped memories).")] string sessionId,
        [Description("Where to promote the facts: 'agent' or 'user'. Default 'agent'.")] string targetScope = "agent",
        [Description("Identifier within the target scope. Default 'default'.")] string targetScopeId = "default",
        [Description("Preview only: report the facts without writing anything. Default false.")] bool dryRun = false,
        CancellationToken ct = default)
    {
        var source = ResolveNamespace(owner, "session", sessionId);
        var target = ResolveNamespace(owner, targetScope, targetScopeId);
        var result = await engine.ConsolidateAsync(source, target, dryRun, ct);

        return Serialize(new
        {
            dryRun = result.DryRun,
            target = target.Key,
            facts = result.Facts.Select(f => new
            {
                statement = f.Statement,
                edge = new { f.Edge.Subject, f.Edge.Predicate, f.Edge.Object },
            }),
            promoted = result.Promoted.Select(r => new
            {
                outcome = r.Outcome.ToString(),
                memory = ToMemory(r.Node),
                supersededId = r.SupersededId,
            }),
            retiredSessionMemories = result.RetiredSessionIds,
        });
    }

    [McpServerTool(Name = "ollmem_link_owners")]
    [Description("Declare two owner identities as the same real-world caller (e.g. the same person's " +
                 "two machines, each with its own signing key). Linked owners keep separate, separately " +
                 "encrypted namespaces — nothing is merged or moved — but ollmem_recall broadens across " +
                 "them so the same person's memory is visible regardless of which machine wrote it. " +
                 "Links are symmetric and transitive.")]
    public static async Task<string> LinkOwnersAsync(
        IMemoryEngine engine,
        [Description("First owner identity.")] string owner,
        [Description("Second owner identity to declare equivalent to the first.")] string otherOwner,
        CancellationToken ct = default)
    {
        await engine.LinkOwnersAsync(owner, otherOwner, ct);
        var linked = await engine.GetLinkedOwnersAsync(owner, ct);
        return Serialize(new { linkedOwners = linked.OrderBy(o => o, StringComparer.OrdinalIgnoreCase) });
    }

    [McpServerTool(Name = "ollmem_unlink_owners")]
    [Description("Remove a previously declared link between two owner identities.")]
    public static async Task<string> UnlinkOwnersAsync(
        IMemoryEngine engine,
        [Description("First owner identity.")] string owner,
        [Description("Second owner identity to unlink from the first.")] string otherOwner,
        CancellationToken ct = default)
    {
        await engine.UnlinkOwnersAsync(owner, otherOwner, ct);
        var linked = await engine.GetLinkedOwnersAsync(owner, ct);
        return Serialize(new { linkedOwners = linked.OrderBy(o => o, StringComparer.OrdinalIgnoreCase) });
    }

    [McpServerTool(Name = "ollmem_linked_owners")]
    [Description("List every owner identity currently linked (directly or transitively) to the given owner, including itself.")]
    public static async Task<string> LinkedOwnersAsync(
        IMemoryEngine engine,
        [Description("Owner identity to inspect.")] string owner,
        CancellationToken ct = default)
    {
        var linked = await engine.GetLinkedOwnersAsync(owner, ct);
        return Serialize(new { linkedOwners = linked.OrderBy(o => o, StringComparer.OrdinalIgnoreCase) });
    }

    private static DateTimeOffset? ParseAsOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;

        throw new ArgumentException($"asOf must be an ISO-8601 instant, e.g. '2026-01-31T00:00:00Z'.", nameof(value));
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
        retiredAt = n.RetiredAt,
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
