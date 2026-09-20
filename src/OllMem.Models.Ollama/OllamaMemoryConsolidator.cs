using System.Text;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OllMem.Models.Ollama;

/// <summary>
/// LLM-backed session consolidation using an open-weight chat model served by
/// Ollama. Where the pass-through default promotes everything a session wrote,
/// this merges restatements and drops the throwaway task state that should not
/// outlive the conversation.
///
/// Every call degrades gracefully: if Ollama is down or answers with something
/// unusable, the pass-through consolidator runs instead, so consolidation
/// never silently loses a session's memories.
/// </summary>
public sealed class OllamaMemoryConsolidator : IMemoryConsolidator
{
    private static readonly string SystemPrompt =
        $$"""
        You are compressing what an AI agent learned during one work session into
        the facts worth keeping permanently.

        Keep: durable preferences, decisions, constraints, conventions, and
        hard-won lessons that will still matter in a future session.
        Drop: transient task state ("currently editing X"), progress notes,
        one-off observations, and anything already implied by another fact.
        Merge: several memories describing the same thing become one fact.

        Respond with JSON only, in exactly this shape:
        {{FactsJson.Shape}}

        Rules:
        - "predicate" is a short lower-case relation ("prefers", "lives in", "uses").
        - "statement" is one short, self-contained third-person sentence.
        - Keep the original subject of each memory; do not relabel everything "user".
        - Never invent facts that are not in the input.
        - If nothing is worth keeping, respond {"facts":[]}.
        """;

    private readonly OllamaClient _client;
    private readonly IMemoryConsolidator _fallback;
    private readonly ILogger<OllamaMemoryConsolidator> _logger;

    public OllamaMemoryConsolidator(
        OllamaClient client,
        IMemoryConsolidator fallback,
        ILogger<OllamaMemoryConsolidator>? logger = null)
    {
        _client = client;
        _fallback = fallback;
        _logger = logger ?? NullLogger<OllamaMemoryConsolidator>.Instance;
    }

    public async ValueTask<IReadOnlyList<ExtractedFact>> ConsolidateAsync(
        IReadOnlyList<MemoryNode> sessionMemories,
        CancellationToken ct = default)
    {
        if (sessionMemories.Count == 0)
            return [];

        if (!_client.IsAvailable)
            return await _fallback.ConsolidateAsync(sessionMemories, ct);

        try
        {
            var reply = await _client.ChatAsync(SystemPrompt, BuildPrompt(sessionMemories), jsonMode: true, ct);
            var facts = FactsJson.Parse(reply);

            // An empty list is a legitimate answer ("nothing durable here"),
            // but only when the model actually produced the envelope.
            if (facts is not null)
                return facts;

            _logger.LogWarning("Ollama consolidation returned unparseable output; promoting memories as-is.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ollama consolidation failed; promoting memories as-is.");
        }

        return await _fallback.ConsolidateAsync(sessionMemories, ct);
    }

    private static string BuildPrompt(IReadOnlyList<MemoryNode> sessionMemories)
    {
        var sb = new StringBuilder("SESSION MEMORIES:\n");
        foreach (var node in sessionMemories.OrderBy(n => n.CreatedAt))
        {
            sb.Append("- ").Append(node.Statement)
              .Append("  [").Append(node.Edge.Subject)
              .Append(" / ").Append(node.Edge.Predicate)
              .Append(" / ").Append(node.Edge.Object)
              .Append("]\n");
        }

        return sb.ToString();
    }
}
