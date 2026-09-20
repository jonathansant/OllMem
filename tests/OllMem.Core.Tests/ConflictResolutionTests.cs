using OllMem.Core.Abstractions;
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

    // ---- Untyped notes ------------------------------------------------------
    // A memory saved without a triple gets a synthetic ("user", "note") edge.
    // Matching conflicts on that predicate would make every note retire the
    // one before it, so untyped notes are reconciled semantically instead.

    [Fact]
    public async Task Unrelated_untyped_notes_coexist()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        var first = await engine.RememberAsync(ns, "The deploy pipeline needs manual approval");
        var second = await engine.RememberAsync(ns, "Invoices are reconciled every Tuesday");

        Assert.Equal(RememberOutcome.Created, first.Outcome);
        Assert.Equal(RememberOutcome.Created, second.Outcome);
        Assert.Null(second.SupersededId);
        Assert.Equal(MemoryStatus.Active, first.Node.Status);

        var edges = await engine.NeighborsAsync(ns, "user");
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public async Task Repeating_an_untyped_note_refreshes_it()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        var first = await engine.RememberAsync(ns, "The deploy pipeline needs manual approval");
        var again = await engine.RememberAsync(ns, "the deploy pipeline needs manual approval!");

        Assert.Equal(RememberOutcome.Refreshed, again.Outcome);
        Assert.Equal(first.Node.Id, again.Node.Id);
        Assert.Null(again.SupersededId);
        Assert.Single(await engine.NeighborsAsync(ns, "user"));
    }

    [Fact]
    public async Task A_near_identical_restatement_refreshes_rather_than_duplicates()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        var first = await engine.RememberAsync(
            ns, "Deployment pipeline requires manual approval before promoting into production environment");
        var again = await engine.RememberAsync(
            ns, "Deployment pipeline requires manual approval before promoting into production environment always");

        Assert.Equal(RememberOutcome.Refreshed, again.Outcome);
        Assert.Equal(first.Node.Id, again.Node.Id);
    }

    [Fact]
    public async Task A_restatement_of_an_explicit_fact_refreshes_it()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        var first = await engine.RememberAsync(ns, "User lives in SF", GraphEdge.Create("user", "lives in", "SF"));

        // Same words, different relation label: the subject+predicate check
        // misses it, so only the semantic pass can catch the duplicate.
        var again = await engine.RememberAsync(ns, "User lives in SF", GraphEdge.Create("user", "resides in", "SF"));

        Assert.Equal(RememberOutcome.Refreshed, again.Outcome);
        Assert.Equal(first.Node.Id, again.Node.Id);
    }

    [Fact]
    public async Task Facts_about_different_subjects_never_merge()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        await engine.RememberAsync(ns, "Acme ships on Fridays", GraphEdge.Create("acme", "ships on", "Fridays"));
        var other = await engine.RememberAsync(
            ns, "Acme ships on Fridays", GraphEdge.Create("globex", "ships on", "Fridays"));

        Assert.Equal(RememberOutcome.Created, other.Outcome);
        Assert.Single(await engine.NeighborsAsync(ns, "acme"));
        Assert.Single(await engine.NeighborsAsync(ns, "globex"));
    }

    [Fact]
    public async Task Outcomes_report_which_write_path_was_taken()
    {
        var (engine, _, _) = EngineFactory.Create();
        var ns = EngineFactory.UserNamespace();

        var created = await engine.RememberAsync(ns, "User lives in NY", GraphEdge.Create("user", "lives in", "NY"));
        var refreshed = await engine.RememberAsync(ns, "User lives in NY", GraphEdge.Create("user", "lives in", "NY"));
        var superseded = await engine.RememberAsync(ns, "User lives in SF", GraphEdge.Create("user", "lives in", "SF"));

        Assert.Equal(RememberOutcome.Created, created.Outcome);
        Assert.Equal(RememberOutcome.Refreshed, refreshed.Outcome);
        Assert.Equal(RememberOutcome.Superseded, superseded.Outcome);
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
