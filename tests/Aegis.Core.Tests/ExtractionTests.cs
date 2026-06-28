using Aegis.Core.Extraction;

namespace Aegis.Core.Tests;

public class ExtractionTests
{
    private readonly RuleBasedFactExtractor _extractor = new();

    [Fact]
    public async Task Extracts_location_work_and_preference()
    {
        var transcript =
            "User: Hi! I live in New York and I work at Acme Corp.\n" +
            "Assistant: Nice to meet you.\n" +
            "User: I love hiking and my favorite color is blue.";

        var facts = await _extractor.ExtractAsync(transcript);

        // Subject/predicate are normalised to lower case; the object value keeps its casing.
        Assert.Contains(facts, f => f.Edge is { Predicate: "lives in" } && f.Edge.Object == "New York");
        Assert.Contains(facts, f => f.Edge is { Predicate: "works at" } && f.Edge.Object == "Acme Corp");
        Assert.Contains(facts, f => f.Edge is { Predicate: "likes" } && f.Edge.Object == "hiking");
        Assert.Contains(facts, f => f.Edge is { Predicate: "favorite color" } && f.Edge.Object == "blue");
    }

    [Fact]
    public async Task Ignores_assistant_utterances()
    {
        var transcript =
            "Assistant: I live in the cloud and I work at OpenAI.\n" +
            "User: I live in Boston.";

        var facts = await _extractor.ExtractAsync(transcript);

        Assert.Single(facts);
        Assert.Equal("Boston", facts[0].Edge.Object);
    }

    [Fact]
    public async Task Object_capture_stops_at_clause_boundary()
    {
        var facts = await _extractor.ExtractAsync("I live in Seattle and I have a dog.");
        Assert.Contains(facts, f => f.Edge is { Predicate: "lives in", Object: "Seattle" });
        Assert.Contains(facts, f => f.Edge is { Predicate: "has", Object: "dog" });
    }

    [Fact]
    public async Task Empty_transcript_yields_no_facts()
    {
        var facts = await _extractor.ExtractAsync("");
        Assert.Empty(facts);
    }
}
