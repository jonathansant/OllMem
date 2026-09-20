using OllMem.Core.Abstractions;
using OllMem.Core.Consolidation;
using OllMem.Core.Domain;
using OllMem.Models.Ollama;

namespace OllMem.Core.Tests;

/// <summary>
/// Wrapping up a session: promoting what it learned into a durable namespace
/// and retiring the originals without losing them.
/// </summary>
public class ConsolidationTests
{
    private static async Task<(OllMem.Core.Engine.OllMemMemoryEngine Engine, TestClock Clock, IAuditLog Audit,
        Namespace Session, Namespace Agent)> SessionAsync(IMemoryConsolidator? consolidator = null)
    {
        var (engine, clock, audit) = EngineFactory.Create(consolidator: consolidator);
        var session = EngineFactory.SessionNamespace();
        var agent = EngineFactory.AgentNamespace();

        await engine.RememberAsync(session, "User prefers bun", GraphEdge.Create("user", "prefers", "bun"));
        await engine.RememberAsync(session, "Tests run offline", GraphEdge.Create("tests", "run", "offline"));
        clock.Advance(TimeSpan.FromHours(1));

        return (engine, clock, audit, session, agent);
    }

    [Fact]
    public async Task Consolidation_promotes_session_facts_into_the_durable_scope()
    {
        var (engine, _, _, session, agent) = await SessionAsync();

        var result = await engine.ConsolidateAsync(session, agent);

        Assert.False(result.DryRun);
        Assert.Equal(2, result.Promoted.Count);
        Assert.All(result.Promoted, r => Assert.Equal(RememberOutcome.Created, r.Outcome));

        var edges = await engine.NeighborsAsync(agent, "user");
        Assert.Equal("bun", Assert.Single(edges).Object);
        Assert.Single(await engine.NeighborsAsync(agent, "tests"));
    }

    [Fact]
    public async Task Consolidated_session_memories_leave_recall_but_stay_in_history()
    {
        var (engine, clock, _, session, agent) = await SessionAsync();
        var beforeConsolidation = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(5));

        var result = await engine.ConsolidateAsync(session, agent);
        Assert.Equal(2, result.RetiredSessionIds.Count);

        // Gone from the session's live view...
        Assert.Empty(await engine.NeighborsAsync(session, "user"));

        // ...but still there as of before the wrap-up.
        var then = await engine.NeighborsAsync(session, "user", asOf: beforeConsolidation);
        Assert.Equal("bun", Assert.Single(then).Object);

        var history = await engine.HistoryAsync(session, "user", "prefers");
        var entry = Assert.Single(history);
        Assert.Equal(MemoryStatus.Consolidated, entry.Node.Status);
        Assert.Equal(clock.GetUtcNow(), entry.ValidTo);
    }

    [Fact]
    public async Task A_consolidated_memory_points_at_what_replaced_it()
    {
        var (engine, _, _, session, agent) = await SessionAsync();

        var result = await engine.ConsolidateAsync(session, agent);
        var history = await engine.HistoryAsync(session, "user", "prefers");
        var promotedIds = result.Promoted.Select(p => p.Node.Id).ToList();

        Assert.Contains(Assert.Single(history).Node.SupersededBy!.Value, promotedIds);
    }

    [Fact]
    public async Task Consolidating_twice_promotes_nothing_new()
    {
        var (engine, _, _, session, agent) = await SessionAsync();

        await engine.ConsolidateAsync(session, agent);
        var second = await engine.ConsolidateAsync(session, agent);

        Assert.Empty(second.Facts);
        Assert.Empty(second.Promoted);
        Assert.Empty(second.RetiredSessionIds);
        Assert.Equal(2, (await engine.NeighborsAsync(agent, "user")).Count
                      + (await engine.NeighborsAsync(agent, "tests")).Count);
    }

    [Fact]
    public async Task A_dry_run_reports_the_facts_and_changes_nothing()
    {
        var (engine, _, audit, session, agent) = await SessionAsync();
        var auditBefore = (await audit.ReadAsync()).Count;

        var result = await engine.ConsolidateAsync(session, agent, dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(2, result.Facts.Count);
        Assert.Contains(result.Facts, f => f.Statement == "User prefers bun");
        Assert.Empty(result.Promoted);
        Assert.Empty(result.RetiredSessionIds);

        // Neither namespace moved, and nothing was written to the log.
        Assert.Single(await engine.NeighborsAsync(session, "user"));
        Assert.Empty(await engine.NeighborsAsync(agent, "user"));
        Assert.Equal(auditBefore, (await audit.ReadAsync()).Count);
    }

    [Fact]
    public async Task Consolidation_is_recorded_in_the_audit_log()
    {
        var (engine, _, audit, session, agent) = await SessionAsync();

        await engine.ConsolidateAsync(session, agent);

        var entries = await audit.ReadAsync(session.Key);
        Assert.Equal(2, entries.Count(e => e.Action == AuditAction.Consolidate));
        Assert.True(await audit.VerifyAsync());
    }

    [Fact]
    public async Task An_empty_session_consolidates_to_nothing()
    {
        var (engine, _, _, _, agent) = await SessionAsync();
        var empty = EngineFactory.SessionNamespace(sessionId: "never-used");

        var result = await engine.ConsolidateAsync(empty, agent);

        Assert.Empty(result.Facts);
        Assert.Empty(result.RetiredSessionIds);
    }

    [Theory]
    [InlineData(MemoryScope.User)]
    [InlineData(MemoryScope.Agent)]
    public async Task Only_a_session_namespace_can_be_drained(MemoryScope sourceScope)
    {
        var (engine, _, _, _, agent) = await SessionAsync();
        var source = new Namespace("owner1", sourceScope, "x");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.ConsolidateAsync(source, agent));
    }

    [Fact]
    public async Task A_session_cannot_be_promoted_into_another_session()
    {
        var (engine, _, _, session, _) = await SessionAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.ConsolidateAsync(session, EngineFactory.SessionNamespace(sessionId: "s2")));
    }

    [Fact]
    public async Task A_session_cannot_be_promoted_into_another_owners_namespace()
    {
        var (engine, _, _, session, _) = await SessionAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.ConsolidateAsync(session, EngineFactory.AgentNamespace(owner: "someone-else")));
    }

    // ---- Consolidators -------------------------------------------------------

    [Fact]
    public async Task The_offline_consolidator_collapses_repeated_relations()
    {
        var (engine, _, _) = EngineFactory.Create();
        var session = EngineFactory.SessionNamespace();
        var agent = EngineFactory.AgentNamespace();

        await engine.RememberAsync(session, "User prefers bun", GraphEdge.Create("user", "prefers", "bun"));
        await engine.RememberAsync(session, "The user prefers bun", GraphEdge.Create("user", "likes", "bun"));

        var memories = new List<MemoryNode>();
        var history = await engine.HistoryAsync(session, "user");
        memories.AddRange(history.Select(h => h.Node));

        var facts = await new PassThroughConsolidator().ConsolidateAsync(memories);

        // Different relations, so both survive; only exact repeats collapse.
        Assert.Equal(2, facts.Count);
        Assert.Single(await new PassThroughConsolidator().ConsolidateAsync([memories[0], memories[0]]));
    }

    [Fact]
    public async Task The_llm_consolidator_merges_a_session_into_fewer_facts()
    {
        using var client = new OllamaClient(
            new OllamaOptions
            {
                Endpoint = "http://fake-ollama",
                ProbeTimeout = TimeSpan.FromMilliseconds(500),
                RequestTimeout = TimeSpan.FromSeconds(5),
            },
            new FakeOllamaHandler
            {
                ChatContent =
                    """
                    {"facts":[{"statement":"User prefers bun for package management",
                               "subject":"user","predicate":"prefers","object":"bun"}]}
                    """,
            });

        var (engine, _, _, session, agent) = await SessionAsync(
            new OllamaMemoryConsolidator(client, new PassThroughConsolidator()));

        var result = await engine.ConsolidateAsync(session, agent);

        var fact = Assert.Single(result.Facts);
        Assert.Equal("User prefers bun for package management", fact.Statement);

        // Both session memories are retired even though they merged into one,
        // and with no one-to-one mapping none of them claims a successor.
        Assert.Equal(2, result.RetiredSessionIds.Count);
        Assert.Null(Assert.Single(await engine.HistoryAsync(session, "user", "prefers")).Node.SupersededBy);
    }

    [Fact]
    public async Task The_llm_consolidator_promotes_everything_when_the_reply_is_unusable()
    {
        using var client = new OllamaClient(
            new OllamaOptions
            {
                Endpoint = "http://fake-ollama",
                ProbeTimeout = TimeSpan.FromMilliseconds(500),
                RequestTimeout = TimeSpan.FromSeconds(5),
            },
            new FakeOllamaHandler { ChatContent = "I had a look and, honestly, it's hard to say." });

        var (engine, _, _, session, agent) = await SessionAsync(
            new OllamaMemoryConsolidator(client, new PassThroughConsolidator()));

        var result = await engine.ConsolidateAsync(session, agent);

        Assert.Equal(2, result.Facts.Count);
    }
}
