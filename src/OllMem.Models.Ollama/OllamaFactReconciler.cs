using System.Text;
using System.Text.Json;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Reconciliation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OllMem.Models.Ollama;

/// <summary>
/// LLM-backed semantic conflict detection (PRD REQ-2.2) using an open-weight
/// chat model served by Ollama. It sees what neither an exact triple match nor
/// a cosine threshold can: that "user prefers bun" retires "user uses npm",
/// and that two differently-worded sentences assert the same thing.
///
/// The deterministic threshold rule runs first and wins when it is already
/// sure, so the model is only consulted for the genuinely ambiguous middle
/// band. Every call degrades gracefully: if Ollama is down, times out, or
/// answers with something unusable, the threshold verdict stands — which can
/// only ever mean "store both", never a wrongly discarded fact.
/// </summary>
public sealed class OllamaFactReconciler : IFactReconciler
{
    private const string SystemPrompt =
        """
        You decide how a NEW fact about an entity relates to FACTS already on record.

        Answer with JSON only, in exactly this shape:
        {"verdict":"duplicate","candidate":1,"reason":"same fact, different wording"}

        Verdicts:
        - "duplicate": the new fact asserts the same thing as candidate N. Wording may differ.
        - "supersedes": the new fact contradicts or replaces candidate N, so the old one is
          no longer true (e.g. a changed preference, a move, a new job).
        - "unrelated": the new fact can coexist with all candidates. Use this whenever you
          are unsure, and whenever the facts are merely on a similar topic.

        Rules:
        - "candidate" is the number of the single fact you are ruling on. Omit it for "unrelated".
        - Two facts about different attributes are "unrelated", even when both are about the
          same entity (e.g. where someone lives vs where they work).
        - A more specific restatement of the same attribute is "duplicate", not "supersedes".
        - "reason" is one short phrase.
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly OllamaClient _client;
    private readonly IFactReconciler _fallback;
    private readonly ReconciliationOptions _options;
    private readonly ILogger<OllamaFactReconciler> _logger;

    public OllamaFactReconciler(
        OllamaClient client,
        IFactReconciler fallback,
        ReconciliationOptions? options = null,
        ILogger<OllamaFactReconciler>? logger = null)
    {
        _client = client;
        _fallback = fallback;
        _options = options ?? new ReconciliationOptions();
        _logger = logger ?? NullLogger<OllamaFactReconciler>.Instance;
    }

    public async ValueTask<ReconcileVerdict> ReconcileAsync(
        string statement,
        GraphEdge edge,
        IReadOnlyList<(MemoryNode Node, double Similarity)> candidates,
        CancellationToken ct = default)
    {
        var baseline = await _fallback.ReconcileAsync(statement, edge, candidates, ct);

        // The cheap rule already recognised a restatement; no need to ask.
        if (baseline.Kind != ReconcileKind.Unrelated)
            return baseline;

        if (!_client.IsAvailable)
            return baseline;

        // Only the ambiguous band is worth a model round-trip: anything above
        // the duplicate threshold was just handled, and anything below the
        // candidate threshold is not plausibly about the same thing.
        var band = candidates
            .Where(c => c.Node.Status == MemoryStatus.Active)
            .Where(c => string.Equals(c.Node.Edge.Subject, edge.Subject, StringComparison.OrdinalIgnoreCase))
            .Where(c => c.Similarity >= _options.CandidateSimilarity
                     && c.Similarity < _options.DuplicateSimilarity)
            .OrderByDescending(c => c.Similarity)
            .Take(_options.MaxCandidates)
            .ToList();

        if (band.Count == 0)
            return baseline;

        try
        {
            var reply = await _client.ChatAsync(SystemPrompt, BuildPrompt(statement, band), jsonMode: true, ct);
            if (Parse(reply, band) is { } verdict)
                return verdict;

            _logger.LogWarning("Ollama reconciliation returned unusable output; keeping the threshold verdict.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ollama reconciliation failed; keeping the threshold verdict.");
        }

        return baseline;
    }

    private static string BuildPrompt(string statement, IReadOnlyList<(MemoryNode Node, double Similarity)> band)
    {
        var sb = new StringBuilder();
        sb.Append("NEW FACT:\n").Append(statement).Append("\n\nFACTS ON RECORD:\n");
        for (var i = 0; i < band.Count; i++)
            sb.Append(i + 1).Append(". ").Append(band[i].Node.Statement).Append('\n');

        return sb.ToString();
    }

    /// <summary>
    /// Map a reply back onto a real memory. Candidates are numbered rather
    /// than identified by guid: small models copy a one-digit index reliably
    /// and hallucinate uuids readily. Anything out of range is treated as an
    /// unusable answer rather than guessed at.
    /// </summary>
    private static ReconcileVerdict? Parse(
        string reply, IReadOnlyList<(MemoryNode Node, double Similarity)> band)
    {
        VerdictDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<VerdictDto>(reply, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dto?.Verdict))
            return null;

        var reason = string.IsNullOrWhiteSpace(dto.Reason) ? "model judgement" : dto.Reason.Trim();

        var kind = dto.Verdict.Trim().ToLowerInvariant() switch
        {
            "duplicate" => ReconcileKind.Duplicate,
            "supersedes" => ReconcileKind.Supersedes,
            "unrelated" => ReconcileKind.Unrelated,
            _ => (ReconcileKind?)null,
        };

        if (kind is null)
            return null;
        if (kind == ReconcileKind.Unrelated)
            return ReconcileVerdict.Unrelated;

        if (dto.Candidate is not { } index || index < 1 || index > band.Count)
            return null;

        return new ReconcileVerdict(kind.Value, band[index - 1].Node.Id, reason);
    }

    private sealed record VerdictDto(string? Verdict, int? Candidate, string? Reason);
}
