using OllMem.Core.Domain;

namespace OllMem.Core.Tests;

public class OwnerLinkTests
{
    [Fact]
    public async Task Unlinked_owners_stay_isolated()
    {
        var (engine, _, _) = EngineFactory.Create();
        var alice = new Namespace("alice", MemoryScope.User, "u");
        var bob = new Namespace("bob", MemoryScope.User, "u");

        await engine.RememberAsync(alice, "Alice secret", GraphEdge.Create("alice", "has", "secret"));

        var bobRecall = await engine.RecallAsync(bob, "secret", topK: 5);
        Assert.Empty(bobRecall);
    }

    [Fact]
    public async Task Linking_owners_makes_recall_cross_owner()
    {
        var (engine, _, _) = EngineFactory.Create();
        var alice = new Namespace("alice", MemoryScope.User, "u1");
        var bob = new Namespace("bob", MemoryScope.User, "u2");

        await engine.RememberAsync(alice, "Working on the invoice bug",
            GraphEdge.Create("task", "is", "invoice bug"));

        await engine.LinkOwnersAsync("alice", "bob");

        var bobRecall = await engine.RecallAsync(bob, "invoice bug", topK: 5);
        Assert.Contains(bobRecall, r => r.Node.Statement.Contains("invoice"));

        // A linked-owner match still ranks below an exact-namespace match.
        var linked = Assert.Single(bobRecall);
        Assert.True(linked.NamespaceAffinity < 1.0);
    }

    [Fact]
    public async Task Unlinking_owners_restores_isolation()
    {
        var (engine, _, _) = EngineFactory.Create();
        var alice = new Namespace("alice", MemoryScope.User, "u1");
        var bob = new Namespace("bob", MemoryScope.User, "u2");

        await engine.RememberAsync(alice, "User prefers dark mode",
            GraphEdge.Create("user", "prefers", "dark mode"));
        await engine.LinkOwnersAsync("alice", "bob");
        await engine.UnlinkOwnersAsync("alice", "bob");

        var bobRecall = await engine.RecallAsync(bob, "ui preference", topK: 5);
        Assert.Empty(bobRecall);
    }

    [Fact]
    public async Task Links_are_transitive()
    {
        var (engine, _, _) = EngineFactory.Create();
        await engine.LinkOwnersAsync("alice", "bob");
        await engine.LinkOwnersAsync("bob", "carol");

        var linked = await engine.GetLinkedOwnersAsync("alice");
        Assert.Equal(new[] { "alice", "bob", "carol" }, linked.OrderBy(o => o));
    }

    [Fact]
    public async Task Linking_does_not_widen_an_unlinked_third_owner()
    {
        var (engine, _, _) = EngineFactory.Create();
        var alice = new Namespace("alice", MemoryScope.User, "u1");
        var carol = new Namespace("carol", MemoryScope.User, "u3");

        await engine.RememberAsync(alice, "User prefers dark mode",
            GraphEdge.Create("user", "prefers", "dark mode"));
        await engine.LinkOwnersAsync("alice", "bob");

        var carolRecall = await engine.RecallAsync(carol, "ui preference", topK: 5);
        Assert.Empty(carolRecall);
    }
}
