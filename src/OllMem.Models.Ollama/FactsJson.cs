using System.Text.Json;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;

namespace OllMem.Models.Ollama;

/// <summary>
/// The <c>{"facts":[{statement, subject, predicate, object}]}</c> envelope both
/// the extractor and the consolidator ask the chat model for, and the tolerant
/// parsing that turns it back into triples.
/// </summary>
internal static class FactsJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The shape instruction shared by every prompt that wants facts back.</summary>
    public const string Shape =
        """{"facts":[{"statement":"User lives in Porto","subject":"user","predicate":"lives in","object":"Porto"}]}""";

    /// <summary>
    /// Parse a model reply into facts, or null when it is not the expected
    /// envelope at all (the caller then falls back). Individual malformed
    /// entries are dropped rather than failing the whole reply.
    /// </summary>
    public static IReadOnlyList<ExtractedFact>? Parse(string reply)
    {
        FactsEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<FactsEnvelope>(reply, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope?.Facts is null)
            return null;

        var results = new List<ExtractedFact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in envelope.Facts)
        {
            if (string.IsNullOrWhiteSpace(fact.Statement) ||
                string.IsNullOrWhiteSpace(fact.Predicate) ||
                string.IsNullOrWhiteSpace(fact.Object))
                continue;

            var subject = string.IsNullOrWhiteSpace(fact.Subject) ? "user" : fact.Subject.Trim();
            var edge = GraphEdge.Create(subject, fact.Predicate.Trim(), fact.Object.Trim());
            if (seen.Add(edge.RelationKey + "=>" + edge.Object.ToLowerInvariant()))
                results.Add(new ExtractedFact(fact.Statement.Trim(), edge));
        }

        return results;
    }

    private sealed record FactsEnvelope(FactDto[]? Facts);

    private sealed record FactDto(string? Statement, string? Subject, string? Predicate, string? Object);
}
