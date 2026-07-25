using System.Diagnostics;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Embeddings;
using OllMem.Core.Engine;
using OllMem.Core.Extraction;
using OllMem.Core.Retrieval;
using OllMem.Core.Security;
using OllMem.Storage.Git;

namespace OllMem.Core.Tests;

/// <summary>
/// Exercises the git-backed stores, including the multi-machine story: a bare
/// repository in a temp directory plays the shared remote, and independent
/// working-tree contexts play the machines/agents syncing through it. Debounce
/// and background sync are disabled so every sync in a test is explicit.
/// </summary>
public sealed class GitPersistenceTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"ollmem-git-test-{Guid.NewGuid():N}");

    // A shared cipher/key across "machines" so encrypted blobs remain readable.
    private readonly AesGcmPayloadCipher _cipher = AesGcmPayloadCipher.CreateEphemeral();

    private readonly List<GitStoreContext> _contexts = [];

    private string CreateBareRemote()
    {
        var remote = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(remote);
        RunGit(remote, "init", "--bare", "--initial-branch", "main");
        return remote;
    }

    private GitStoreContext NewContext(string name, string? remote = null)
    {
        var ctx = new GitStoreContext(new GitStoreOptions
        {
            Path = Path.Combine(_root, name),
            RemoteUrl = remote,
            CommitDebounceMs = 0,
            SyncIntervalSeconds = 0,
        });
        _contexts.Add(ctx);
        return ctx;
    }

    private (OllMemMemoryEngine Engine, IAuditLog Audit, GitHybridStore Hybrid) NewEngine(GitStoreContext ctx)
    {
        var hybrid = new GitHybridStore(ctx);
        var audit = new GitAuditLog(ctx);
        var engine = new OllMemMemoryEngine(
            new HashingEmbeddingService(256),
            hybrid,
            hybrid,
            new GitBlobStore(ctx),
            _cipher,
            new SoftDecayScorer(),
            new RuleBasedFactExtractor(),
            audit,
            clock: null,
            ownerLinks: new GitOwnerLinkStore(ctx));
        return (engine, audit, hybrid);
    }

    [Fact]
    public async Task Memories_survive_a_restart()
    {
        var ns = new Namespace("owner", MemoryScope.User, "u1");

        // First "process": write some facts (no remote — local repo only).
        var ctx1 = NewContext("solo");
        var (engine1, _, _) = NewEngine(ctx1);
        await engine1.RememberAsync(ns, "User lives in Lisbon",
            GraphEdge.Create("user", "lives in", "Lisbon"));
        await engine1.RememberAsync(ns, "User likes surfing",
            GraphEdge.Create("user", "likes", "surfing"));
        await ctx1.SyncAsync();

        // Second "process": brand-new context over the same repository.
        var ctx2 = NewContext("solo");
        var (engine2, _, _) = NewEngine(ctx2);
        var recalled = await engine2.RecallAsync(ns, "where does the user live", topK: 5);

        Assert.Contains(recalled, r => r.Node.Statement.Contains("Lisbon"));
        var edges = await engine2.NeighborsAsync(ns, "user");
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public async Task Memories_sync_across_machines()
    {
        var remote = CreateBareRemote();
        var ns = new Namespace("owner", MemoryScope.User, "u1");

        // Machine A writes and pushes.
        var machineA = NewContext("machine-a", remote);
        var (engineA, _, _) = NewEngine(machineA);
        await engineA.RememberAsync(ns, "User lives in Lisbon",
            GraphEdge.Create("user", "lives in", "Lisbon"));
        Assert.True(await machineA.SyncAsync());

        // Machine B clones the shared memory at startup and can recall it.
        var machineB = NewContext("machine-b", remote);
        var (engineB, _, _) = NewEngine(machineB);
        var recalled = await engineB.RecallAsync(ns, "where does the user live", topK: 3);
        Assert.Contains(recalled, r => r.Node.Statement.Contains("Lisbon"));

        // Machine B adds a fact; machine A picks it up on its next sync.
        await engineB.RememberAsync(ns, "User works at Acme",
            GraphEdge.Create("user", "works at", "Acme"));
        Assert.True(await machineB.SyncAsync());
        Assert.True(await machineA.SyncAsync());

        var edges = await engineA.NeighborsAsync(ns, "user");
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public async Task Independent_offline_writes_merge_without_losing_either()
    {
        var remote = CreateBareRemote();
        var ns = new Namespace("owner", MemoryScope.User, "u1");

        // Both machines start independently (unrelated histories) and write
        // different facts before either has synced.
        var machineA = NewContext("machine-a", remote);
        var machineB = NewContext("machine-b", remote);
        var (engineA, _, hybridA) = NewEngine(machineA);
        var (engineB, _, hybridB) = NewEngine(machineB);

        await engineA.RememberAsync(ns, "User lives in Lisbon",
            GraphEdge.Create("user", "lives in", "Lisbon"));
        await engineB.RememberAsync(ns, "User likes surfing",
            GraphEdge.Create("user", "likes", "surfing"));

        Assert.True(await machineA.SyncAsync());
        Assert.True(await machineB.SyncAsync()); // pulls A's history, merges, pushes
        Assert.True(await machineA.SyncAsync()); // pulls the merge

        Assert.Equal(2, (await hybridA.ListActiveAsync(ns)).Count);
        Assert.Equal(2, (await hybridB.ListActiveAsync(ns)).Count);
    }

    [Fact]
    public async Task Conflicting_updates_to_the_same_memory_merge_field_wise()
    {
        var remote = CreateBareRemote();
        var ns = new Namespace("owner", MemoryScope.User, "u1");

        var machineA = NewContext("machine-a", remote);
        var (engineA, _, hybridA) = NewEngine(machineA);
        var original = await engineA.RememberAsync(ns, "User lives in NY",
            GraphEdge.Create("user", "lives in", "NY"));
        Assert.True(await machineA.SyncAsync());

        var machineB = NewContext("machine-b", remote);
        var (engineB, _, hybridB) = NewEngine(machineB);

        // Both machines touch the node's usage stats (guaranteeing git sees a
        // real textual conflict, not an auto-merge) and A also supersedes it.
        await engineA.RecallAsync(ns, "where does the user live", topK: 1);
        var replacement = await engineA.RememberAsync(ns, "User lives in SF",
            GraphEdge.Create("user", "lives in", "SF"));
        Assert.NotNull(replacement.SupersededId);
        await engineB.RecallAsync(ns, "where does the user live", topK: 1);

        Assert.True(await machineA.SyncAsync());
        Assert.True(await machineB.SyncAsync()); // merges the divergent node versions
        Assert.True(await machineA.SyncAsync());

        // The supersede wins on both machines...
        foreach (var hybrid in new[] { hybridA, hybridB })
        {
            var edges = await hybrid.NeighborsAsync(ns, "user", "lives in");
            var edge = Assert.Single(edges);
            Assert.Equal("SF", edge.Object);
        }

        // ...and B's access statistics on the old node are not lost.
        var merged = await hybridB.GetAsync(original.Node.Id);
        Assert.NotNull(merged);
        Assert.Equal(MemoryStatus.Superseded, merged!.Status);
        Assert.Equal(replacement.Node.Id, merged.SupersededBy);
        Assert.Equal(1, merged.AccessCount);
    }

    [Fact]
    public async Task Audit_chains_from_all_machines_verify_after_sync()
    {
        var remote = CreateBareRemote();
        var ns = new Namespace("owner", MemoryScope.User, "u1");

        var machineA = NewContext("machine-a", remote);
        var (engineA, auditA, _) = NewEngine(machineA);
        await engineA.RememberAsync(ns, "Fact one", GraphEdge.Create("user", "note", "one"));
        Assert.True(await machineA.SyncAsync());

        var machineB = NewContext("machine-b", remote);
        var (engineB, auditB, _) = NewEngine(machineB);
        await engineB.RememberAsync(ns, "Fact two", GraphEdge.Create("user", "note", "two"));
        Assert.True(await machineB.SyncAsync());
        Assert.True(await machineA.SyncAsync());

        // Each machine sees its own chain plus the other's, and every chain
        // still verifies end to end.
        Assert.True(await auditA.VerifyAsync());
        Assert.True(await auditB.VerifyAsync());
        var entriesA = await auditA.ReadAsync();
        var entriesB = await auditB.ReadAsync();
        Assert.Equal(entriesA.Count, entriesB.Count);
        Assert.Contains(entriesA, e => e.Detail == "Fact one");
        Assert.Contains(entriesA, e => e.Detail == "Fact two");
    }

    [Fact]
    public async Task Blob_payloads_are_encrypted_recoverable_and_synced()
    {
        var remote = CreateBareRemote();
        var machineA = NewContext("machine-a", remote);
        var blobsA = new GitBlobStore(machineA);

        var plaintext = "User lives in Lisbon"u8.ToArray();
        var encrypted = _cipher.Encrypt("owner/user/u1", plaintext);
        var blobRef = await blobsA.PutAsync(encrypted);
        Assert.True(await machineA.SyncAsync());

        // Another machine reads the same content-addressed blob after syncing.
        var machineB = NewContext("machine-b", remote);
        var stored = await new GitBlobStore(machineB).GetAsync(blobRef);

        Assert.NotNull(stored);
        Assert.NotEqual(plaintext, stored);                    // stored form is ciphertext
        Assert.Equal(plaintext, _cipher.Decrypt("owner/user/u1", stored!));
    }

    [Fact]
    public async Task Owner_links_sync_across_machines()
    {
        var remote = CreateBareRemote();
        var laptop = new Namespace("laptop-key", MemoryScope.User, "u1");
        var desktop = new Namespace("desktop-key", MemoryScope.User, "u1");

        // The laptop writes under its own signing key and links its two identities.
        var machineA = NewContext("machine-a", remote);
        var (engineA, _, _) = NewEngine(machineA);
        await engineA.RememberAsync(laptop, "Deploys run through service.sh",
            GraphEdge.Create("deploy", "runs through", "service.sh"));
        await engineA.LinkOwnersAsync("laptop-key", "desktop-key");
        Assert.True(await machineA.SyncAsync());

        // The desktop clones the shared memory and recalls under its own owner,
        // reaching the laptop's namespace purely because of the synced link.
        var machineB = NewContext("machine-b", remote);
        var (engineB, _, _) = NewEngine(machineB);
        Assert.Equal(
            new[] { "desktop-key", "laptop-key" },
            (await engineB.GetLinkedOwnersAsync("desktop-key")).OrderBy(o => o));

        var recalled = await engineB.RecallAsync(desktop, "how do deploys run", topK: 5);
        Assert.Contains(recalled, r => r.Node.Statement.Contains("service.sh"));

        // Unlinking on one machine propagates back and restores isolation.
        await engineB.UnlinkOwnersAsync("desktop-key", "laptop-key");
        Assert.True(await machineB.SyncAsync());
        Assert.True(await machineA.SyncAsync());

        Assert.Empty(await engineA.RecallAsync(desktop, "how do deploys run", topK: 5));
    }

    private static void RunGit(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var ctx in _contexts)
            await ctx.DisposeAsync();

        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
