using OllMem.Core.Domain;

namespace OllMem.Core.Tests;

public class NamespaceIsolationTests
{
    [Fact]
    public async Task Scopes_are_isolated_within_the_same_owner()
    {
        var (engine, _, _) = EngineFactory.Create();
        var userNs = new Namespace("owner", MemoryScope.User, "u1");
        var sessionNs = new Namespace("owner", MemoryScope.Session, "s1");

        await engine.RememberAsync(userNs, "User prefers dark mode",
            GraphEdge.Create("user", "prefers", "dark mode"));
        await engine.RememberAsync(sessionNs, "Working on the invoice bug",
            GraphEdge.Create("task", "is", "invoice bug"));

        var userRecall = await engine.RecallAsync(userNs, "ui preference", topK: 5);
        Assert.All(userRecall, r => Assert.Equal(userNs.Key, r.Node.Namespace.Key));
        Assert.DoesNotContain(userRecall, r => r.Node.Statement.Contains("invoice"));
    }

    [Fact]
    public async Task Owners_are_isolated()
    {
        var (engine, _, _) = EngineFactory.Create();
        var alice = new Namespace("alice", MemoryScope.User, "u");
        var bob = new Namespace("bob", MemoryScope.User, "u");

        await engine.RememberAsync(alice, "Alice secret", GraphEdge.Create("alice", "has", "secret"));

        var bobRecall = await engine.RecallAsync(bob, "secret", topK: 5);
        Assert.Empty(bobRecall);
    }
}
