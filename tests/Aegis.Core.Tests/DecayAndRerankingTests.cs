using Aegis.Core.Domain;
using Aegis.Core.Retrieval;

namespace Aegis.Core.Tests;

public class DecayAndRerankingTests
{
    [Fact]
    public void Weight_never_drops_below_floor()
    {
        var scorer = new SoftDecayScorer(new SoftDecayOptions { Floor = 0.3, HalfLifeDays = 10 });
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ancient = new MemoryNode
        {
            Id = Guid.NewGuid(),
            Namespace = EngineFactory.UserNamespace(),
            Statement = "old",
            Edge = GraphEdge.Create("user", "note", "old"),
            Embedding = new float[4],
            LastAccessedAt = now.AddYears(-5),
        };

        var weight = scorer.Weight(ancient, now);
        Assert.True(weight >= 0.3, $"weight {weight} should be >= floor");
        Assert.True(weight <= 1.0);
    }

    [Fact]
    public void Fresh_memory_weighs_more_than_stale()
    {
        var scorer = new SoftDecayScorer(new SoftDecayOptions { Floor = 0.3, HalfLifeDays = 10 });
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        MemoryNode Node(DateTimeOffset accessed) => new()
        {
            Id = Guid.NewGuid(),
            Namespace = EngineFactory.UserNamespace(),
            Statement = "x",
            Edge = GraphEdge.Create("user", "note", "x"),
            Embedding = new float[4],
            LastAccessedAt = accessed,
        };

        var fresh = scorer.Weight(Node(now), now);
        var stale = scorer.Weight(Node(now.AddDays(-60)), now);
        Assert.True(fresh > stale);
    }

    [Fact]
    public async Task Reranking_prefers_fresh_fact_over_stale_near_duplicate()
    {
        var (engine, clock, _) = EngineFactory.Create(
            new SoftDecayOptions { Floor = 0.3, HalfLifeDays = 5 });
        var ns = EngineFactory.UserNamespace();

        // An older note about coffee...
        await engine.RememberAsync(ns, "User drinks coffee every morning",
            GraphEdge.Create("user", "drinks", "coffee"));

        // ...then a long time passes and a newer, closely related note arrives.
        clock.Advance(TimeSpan.FromDays(120));
        await engine.RememberAsync(ns, "User now drinks tea every morning",
            GraphEdge.Create("user", "drinks-beverage", "tea"));

        var results = await engine.RecallAsync(ns, "what does the user drink every morning", topK: 2);

        Assert.NotEmpty(results);
        // The fresh memory should rank first thanks to time-aware reranking.
        Assert.Contains("tea", results[0].Node.Statement);
        Assert.True(results[0].FinalScore <= results[0].Similarity + 1e-9,
            "final score must be similarity scaled by a <= 1 decay weight");
    }

    [Fact]
    public async Task Recall_increments_access_stats()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();
        await engine.RememberAsync(ns, "User likes jazz", GraphEdge.Create("user", "likes", "jazz"));

        var first = await engine.RecallAsync(ns, "music taste", topK: 1);
        var afterFirst = first[0].Node.AccessCount; // snapshot before the node is touched again
        var second = await engine.RecallAsync(ns, "music taste", topK: 1);

        Assert.Equal(1, afterFirst);
        Assert.True(second[0].Node.AccessCount > afterFirst);
    }
}
