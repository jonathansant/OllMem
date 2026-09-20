using OllMem.Core.Domain;
using OllMem.Core.Embeddings;
using OllMem.Core.Storage;

namespace OllMem.Core.Tests;

/// <summary>
/// Reading the memory as it stood in the past: as-of recall, as-of graph
/// traversal, and the full timeline of a relation.
/// </summary>
public class TemporalRecallTests
{
    /// <summary>
    /// A user who lived in New York, then moved to San Francisco ten days
    /// later. Returns the clock instant of each state.
    /// </summary>
    private static async Task<(OllMem.Core.Engine.OllMemMemoryEngine Engine, Namespace Ns,
        DateTimeOffset InNewYork, DateTimeOffset Moved)> MovingUserAsync()
    {
        var (engine, clock, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        await engine.RememberAsync(ns, "User lives in New York", GraphEdge.Create("user", "lives in", "New York"));
        var inNewYork = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromDays(10));
        await engine.RememberAsync(
            ns, "User lives in San Francisco", GraphEdge.Create("user", "lives in", "San Francisco"));
        var moved = clock.GetUtcNow();

        return (engine, ns, inNewYork, moved);
    }

    [Fact]
    public async Task Recall_without_a_viewpoint_answers_with_the_current_fact()
    {
        var (engine, ns, _, _) = await MovingUserAsync();

        var results = await engine.RecallAsync(ns, "where does the user live", topK: 5);

        Assert.Contains(results, r => r.Node.Statement.Contains("San Francisco"));
        Assert.DoesNotContain(results, r => r.Node.Statement.Contains("New York"));
    }

    [Fact]
    public async Task Recall_as_of_the_past_answers_with_the_fact_of_the_day()
    {
        var (engine, ns, inNewYork, _) = await MovingUserAsync();

        var results = await engine.RecallAsync(ns, "where does the user live", topK: 5, asOf: inNewYork);

        Assert.Contains(results, r => r.Node.Statement.Contains("New York"));
        Assert.DoesNotContain(results, r => r.Node.Statement.Contains("San Francisco"));
    }

    [Fact]
    public async Task Recall_as_of_after_the_change_answers_with_the_new_fact()
    {
        var (engine, ns, _, moved) = await MovingUserAsync();

        var results = await engine.RecallAsync(ns, "where does the user live", topK: 5, asOf: moved);

        Assert.Contains(results, r => r.Node.Statement.Contains("San Francisco"));
        Assert.DoesNotContain(results, r => r.Node.Statement.Contains("New York"));
    }

    [Fact]
    public async Task Recall_as_of_before_anything_was_known_returns_nothing()
    {
        var (engine, ns, inNewYork, _) = await MovingUserAsync();

        var results = await engine.RecallAsync(
            ns, "where does the user live", topK: 5, asOf: inNewYork.AddDays(-1));

        Assert.Empty(results);
    }

    [Fact]
    public async Task A_historical_recall_does_not_count_as_an_access()
    {
        var (engine, ns, inNewYork, _) = await MovingUserAsync();

        var results = await engine.RecallAsync(ns, "where does the user live", topK: 5, asOf: inNewYork);
        var recalled = Assert.Single(results);

        // Looking into the past must not make a retired fact look freshly
        // used, or it would outrank current facts in later live recalls.
        Assert.Equal(0, recalled.Node.AccessCount);
        Assert.Equal(inNewYork, recalled.Node.LastAccessedAt);
    }

    [Fact]
    public async Task A_live_recall_still_counts_as_an_access()
    {
        var (engine, ns, _, _) = await MovingUserAsync();

        var results = await engine.RecallAsync(ns, "where does the user live", topK: 5);

        Assert.Equal(1, Assert.Single(results).Node.AccessCount);
    }

    [Fact]
    public async Task Neighbors_as_of_the_past_traverse_the_old_graph()
    {
        var (engine, ns, inNewYork, _) = await MovingUserAsync();

        var then = await engine.NeighborsAsync(ns, "user", "lives in", asOf: inNewYork);
        var now = await engine.NeighborsAsync(ns, "user", "lives in");

        Assert.Equal("New York", Assert.Single(then).Object);
        Assert.Equal("San Francisco", Assert.Single(now).Object);
    }

    [Fact]
    public async Task History_lists_every_version_with_its_window()
    {
        var (engine, ns, inNewYork, moved) = await MovingUserAsync();

        var history = await engine.HistoryAsync(ns, "user", "lives in");

        Assert.Equal(2, history.Count);

        Assert.Equal("User lives in New York", history[0].Node.Statement);
        Assert.Equal(inNewYork, history[0].ValidFrom);
        Assert.Equal(moved, history[0].ValidTo);

        Assert.Equal("User lives in San Francisco", history[1].Node.Statement);
        Assert.Equal(moved, history[1].ValidFrom);
        Assert.Null(history[1].ValidTo);
    }

    [Fact]
    public async Task History_can_span_every_predicate_for_a_subject()
    {
        var (engine, ns, _, _) = await MovingUserAsync();
        await engine.RememberAsync(ns, "User works at Acme", GraphEdge.Create("user", "works at", "Acme"));

        var all = await engine.HistoryAsync(ns, "user");

        Assert.Equal(3, all.Count);
        Assert.Equal(2, all.Count(e => e.Node.Edge.Predicate == "lives in"));
    }

    [Fact]
    public async Task A_fact_retired_before_retirement_was_recorded_is_placed_by_its_successor()
    {
        // Memories written by an older OllMem have no RetiredAt even once
        // superseded; the successor's creation is when they stopped being true.
        var vectors = new InMemoryVectorStore();
        var (engine, clock, _) = EngineFactory.Create(vectors: vectors);
        var ns = EngineFactory.UserNamespace();
        var embeddings = new HashingEmbeddingService(256);

        var start = clock.GetUtcNow();
        var successor = new MemoryNode
        {
            Id = Guid.NewGuid(),
            Namespace = ns,
            Statement = "User lives in San Francisco",
            Edge = GraphEdge.Create("user", "lives in", "San Francisco"),
            Embedding = await embeddings.EmbedAsync("User lives in San Francisco"),
            CreatedAt = start.AddDays(10),
            LastAccessedAt = start.AddDays(10),
        };
        var legacy = new MemoryNode
        {
            Id = Guid.NewGuid(),
            Namespace = ns,
            Statement = "User lives in New York",
            Edge = GraphEdge.Create("user", "lives in", "New York"),
            Embedding = await embeddings.EmbedAsync("User lives in New York"),
            CreatedAt = start,
            LastAccessedAt = start,
            Status = MemoryStatus.Superseded,
            SupersededBy = successor.Id,
            RetiredAt = null,
        };
        await vectors.UpsertAsync(legacy);
        await vectors.UpsertAsync(successor);

        var history = await engine.HistoryAsync(ns, "user", "lives in");
        Assert.Equal(start.AddDays(10), history[0].ValidTo);

        var thenResults = await engine.RecallAsync(
            ns, "where does the user live", topK: 5, asOf: start.AddDays(5));
        Assert.Equal("User lives in New York", Assert.Single(thenResults).Node.Statement);
    }

    [Fact]
    public async Task A_retired_fact_with_no_successor_is_never_resurrected()
    {
        var vectors = new InMemoryVectorStore();
        var (engine, clock, _) = EngineFactory.Create(vectors: vectors);
        var ns = EngineFactory.UserNamespace();

        var start = clock.GetUtcNow();
        await vectors.UpsertAsync(new MemoryNode
        {
            Id = Guid.NewGuid(),
            Namespace = ns,
            Statement = "User lives in New York",
            Edge = GraphEdge.Create("user", "lives in", "New York"),
            Embedding = await new HashingEmbeddingService(256).EmbedAsync("User lives in New York"),
            CreatedAt = start,
            LastAccessedAt = start,
            Status = MemoryStatus.Superseded,
        });

        // Nothing says when it stopped being true, so it is treated as never
        // current rather than current forever.
        Assert.Empty(await engine.RecallAsync(ns, "where does the user live", topK: 5, asOf: start.AddDays(5)));
        Assert.Empty(await engine.NeighborsAsync(ns, "user", asOf: start.AddDays(5)));
    }
}
