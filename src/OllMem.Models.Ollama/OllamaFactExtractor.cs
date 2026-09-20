using OllMem.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OllMem.Models.Ollama;

/// <summary>
/// LLM-backed auto-extraction (PRD REQ-2.1) using an open-weight chat model
/// served by Ollama. Unlike the rule-based baseline it handles open-domain
/// phrasing ("we relocated to Portland last spring"). Every call degrades
/// gracefully: if Ollama is down, times out, or returns unparseable output, the
/// transcript is handed to the fallback extractor instead, so extraction never
/// fails outright.
/// </summary>
public sealed class OllamaFactExtractor : IFactExtractor
{
    private const string SystemPrompt =
        """
        You extract durable facts about the USER from a conversation transcript.
        Only extract facts the user states about themselves: identity, location,
        work, preferences, possessions, relationships, state changes. Ignore
        assistant statements, questions, hypotheticals and small talk.

        Respond with JSON only, in exactly this shape:
        {"facts":[{"statement":"User lives in Porto","subject":"user","predicate":"lives in","object":"Porto"}]}

        Rules:
        - "subject" is almost always "user".
        - "predicate" is a short lower-case relation ("lives in", "works at", "likes", "has", "name").
        - "statement" is one short third-person sentence.
        - If there are no facts, respond {"facts":[]}.
        """;

    private readonly OllamaClient _client;
    private readonly IFactExtractor _fallback;
    private readonly ILogger<OllamaFactExtractor> _logger;

    public OllamaFactExtractor(
        OllamaClient client,
        IFactExtractor fallback,
        ILogger<OllamaFactExtractor>? logger = null)
    {
        _client = client;
        _fallback = fallback;
        _logger = logger ?? NullLogger<OllamaFactExtractor>.Instance;
    }

    public async ValueTask<IReadOnlyList<ExtractedFact>> ExtractAsync(
        string transcript, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return [];

        if (!_client.IsAvailable)
            return await _fallback.ExtractAsync(transcript, ct);

        try
        {
            var reply = await _client.ChatAsync(SystemPrompt, transcript, jsonMode: true, ct);
            var facts = Parse(reply);
            if (facts is not null)
                return facts;

            _logger.LogWarning("Ollama extraction returned unparseable output; using rule-based fallback.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ollama extraction failed; using rule-based fallback.");
        }

        return await _fallback.ExtractAsync(transcript, ct);
    }

    private static IReadOnlyList<ExtractedFact>? Parse(string reply) => FactsJson.Parse(reply);
}
