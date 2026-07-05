using OllMem.Core.Domain;

namespace OllMem.Core.Tests;

public class ConflictResolutionTests
{
    [Fact]
    public async Task Contradicting_fact_supersedes_old_state()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        var first = await engine.RememberAsync(ns, "User lives in NY",
            GraphEdge.Create("user", "lives in", "NY"));
        var second = await engine.RememberAsync(ns, "User lives in SF",
            GraphEdge.Create("user", "lives in", "SF"));

        Assert.Null(first.SupersededId);
        Assert.Equal(first.Node.Id, second.SupersededId);
        Assert.Equal(MemoryStatus.Active, second.Node.Status);

        // Only the latest state is returned by graph traversal.
        var edges = await engine.NeighborsAsync(ns, "user", "lives in");
        Assert.Single(edges);
        Assert.Equal("SF", edges[0].Object);
    }

    [Fact]
    public async Task Identical_fact_is_not_duplicated()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        var first = await engine.RememberAsync(ns, "User lives in SF",
            GraphEdge.Create("user", "lives in", "SF"));
        var again = await engine.RememberAsync(ns, "User lives in SF",
            GraphEdge.Create("user", "lives in", "SF"));

        Assert.Equal(first.Node.Id, again.Node.Id);
        Assert.Null(again.SupersededId);

        var edges = await engine.NeighborsAsync(ns, "user", "lives in");
        Assert.Single(edges);
    }

    [Fact]
    public async Task Different_predicates_coexist()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        await engine.RememberAsync(ns, "User lives in SF", GraphEdge.Create("user", "lives in", "SF"));
        await engine.RememberAsync(ns, "User works at Acme", GraphEdge.Create("user", "works at", "Acme"));

        var edges = await engine.NeighborsAsync(ns, "user");
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public async Task Superseded_fact_is_not_returned_by_recall()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        await engine.RememberAsync(ns, "User lives in New York",
            GraphEdge.Create("user", "lives in", "New York"));
        await engine.RememberAsync(ns, "User lives in San Francisco",
            GraphEdge.Create("user", "lives in", "San Francisco"));

        var results = await engine.RecallAsync(ns, "where does the user live", topK: 5);

        Assert.Contains(results, r => r.Node.Statement.Contains("San Francisco"));
        Assert.DoesNotContain(results, r => r.Node.Statement.Contains("New York"));
    }

    [Fact]
    public async Task Extract_then_update_resolves_state_change()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        await engine.ExtractAndStoreAsync(ns, "User: I live in NY.");
        await engine.ExtractAndStoreAsync(ns, "User: Actually I moved to SF.");

        var edges = await engine.NeighborsAsync(ns, "user", "lives in");
        Assert.Single(edges);
        Assert.Equal("SF", edges[0].Object);
    }
}
