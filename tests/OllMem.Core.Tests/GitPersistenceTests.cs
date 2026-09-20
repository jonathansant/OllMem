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

        // ...including when it stopped being true, so as-of recall agrees on
        // both machines rather than depending on who merged.
        Assert.NotNull(merged.RetiredAt);
        var onA = await hybridA.GetAsync(original.Node.Id);
        Assert.Equal(merged.RetiredAt, onA!.RetiredAt);
    }

    [Fact]
    public async Task History_and_as_of_recall_survive_a_restart()
    {
        var ns = new Namespace("owner", MemoryScope.User, "u1");
        var ctx = NewContext("machine1");

        var before = DateTimeOffset.UtcNow;
        var (engine1, _, _) = NewEngine(ctx);
        await engine1.RememberAsync(ns, "User lives in Lisbon", GraphEdge.Create("user", "lives in", "Lisbon"));
        await engine1.RememberAsync(ns, "User lives in Porto", GraphEdge.Create("user", "lives in", "Porto"));
        await ctx.SyncAsync();

        // Second "process": the validity windows have to come back out of the
        // node documents on disk.
        var (engine2, _, _) = NewEngine(ctx);

        var history = await engine2.HistoryAsync(ns, "user", "lives in");
        Assert.Equal(2, history.Count);
        Assert.Contains("Lisbon", history[0].Node.Statement);
        Assert.NotNull(history[0].ValidTo);
        Assert.Null(history[1].ValidTo);

        // Nothing was known before the first write.
        Assert.Empty(await engine2.RecallAsync(ns, "where does the user live", topK: 5, asOf: before));

        // Between the two writes, Lisbon was the current answer.
        var then = await engine2.RecallAsync(
            ns, "where does the user live", topK: 5, asOf: history[0].ValidTo!.Value.AddTicks(-1));
        Assert.Contains("Lisbon", Assert.Single(then).Node.Statement);
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

    // ---- The merge rule itself ----------------------------------------------
    // Two machines that diverge must land on the same node whichever of them
    // performs the merge, or they never agree again. Some divergences are hard
    // to stage through real git, so the rule is also exercised directly.

    private static MemoryNodeDocument Doc(
        Guid id, MemoryStatus status, DateTimeOffset? retiredAt, Guid? supersededBy = null, int accessCount = 0)
    {
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new MemoryNodeDocument(
            id, "owner", MemoryScope.User, "u1", "User lives in Lisbon",
            "user", "lives in", "Lisbon", Convert.ToBase64String(new byte[16]), null,
            epoch, epoch, accessCount, status, supersededBy, retiredAt);
    }

    [Fact]
    public void Merging_two_retirements_is_independent_of_direction()
    {
        var id = Guid.NewGuid();
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // One machine superseded the fact; the other consolidated the session
        // it lived in, an hour later.
        var superseded = Doc(id, MemoryStatus.Superseded, epoch.AddHours(1), Guid.NewGuid());
        var consolidated = Doc(id, MemoryStatus.Consolidated, epoch.AddHours(2), Guid.NewGuid());

        var fromA = MemoryNodeDocument.Merge(superseded, consolidated);
        var fromB = MemoryNodeDocument.Merge(consolidated, superseded);

        Assert.Equal(fromA, fromB);

        // The earlier retirement is the one that stuck, with its own successor.
        Assert.Equal(MemoryStatus.Superseded, fromA.Status);
        Assert.Equal(epoch.AddHours(1), fromA.RetiredAt);
        Assert.Equal(superseded.SupersededBy, fromA.SupersededBy);
    }

    [Fact]
    public void Merging_retirements_recorded_at_the_same_instant_still_converges()
    {
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

        var superseded = Doc(id, MemoryStatus.Superseded, at, Guid.NewGuid());
        var consolidated = Doc(id, MemoryStatus.Consolidated, at, Guid.NewGuid());

        Assert.Equal(
            MemoryNodeDocument.Merge(superseded, consolidated),
            MemoryNodeDocument.Merge(consolidated, superseded));
    }

    [Fact]
    public void A_retirement_beats_an_active_copy_from_either_side()
    {
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        var successor = Guid.NewGuid();

        var active = Doc(id, MemoryStatus.Active, null, accessCount: 3);
        var retired = Doc(id, MemoryStatus.Superseded, at, successor);

        foreach (var merged in new[]
                 {
                     MemoryNodeDocument.Merge(active, retired),
                     MemoryNodeDocument.Merge(retired, active),
                 })
        {
            Assert.Equal(MemoryStatus.Superseded, merged.Status);
            Assert.Equal(at, merged.RetiredAt);
            Assert.Equal(successor, merged.SupersededBy);

            // The active side's usage statistics are not lost.
            Assert.Equal(3, merged.AccessCount);
        }
    }

    [Fact]
    public void A_node_active_on_both_sides_has_no_retirement()
    {
        var id = Guid.NewGuid();

        var merged = MemoryNodeDocument.Merge(
            Doc(id, MemoryStatus.Active, null), Doc(id, MemoryStatus.Active, null, accessCount: 2));

        Assert.Equal(MemoryStatus.Active, merged.Status);
        Assert.Null(merged.RetiredAt);
    }

    [Fact]
    public void A_retirement_with_a_recorded_instant_beats_one_without()
    {
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

        // A node retired by an older OllMem has no instant recorded.
        var legacy = Doc(id, MemoryStatus.Superseded, null, Guid.NewGuid());
        var current = Doc(id, MemoryStatus.Superseded, at, Guid.NewGuid());

        Assert.Equal(at, MemoryNodeDocument.Merge(legacy, current).RetiredAt);
        Assert.Equal(at, MemoryNodeDocument.Merge(current, legacy).RetiredAt);
    }
}
