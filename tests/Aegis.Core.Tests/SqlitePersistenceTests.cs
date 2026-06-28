using Aegis.Core.Abstractions;
using Aegis.Core.Domain;
using Aegis.Core.Embeddings;
using Aegis.Core.Engine;
using Aegis.Core.Extraction;
using Aegis.Core.Retrieval;
using Aegis.Core.Security;
using Aegis.Storage.Sqlite;

namespace Aegis.Core.Tests;

/// <summary>
/// Exercises the SQLite-backed stores. Each test uses a unique database file and
/// builds a fresh set of store instances per "process lifetime" so we can prove
/// data survives a restart.
/// </summary>
public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"aegis-test-{Guid.NewGuid():N}.db");

    // A shared cipher/key across "restarts" so encrypted blobs remain readable.
    private readonly AesGcmPayloadCipher _cipher = AesGcmPayloadCipher.CreateEphemeral();

    // The context must be created once per file (it pools connections); reuse it
    // to simulate the same database being reopened by a new engine instance.
    private SqliteStoreContext? _ctx;

    private (AegisMemoryEngine Engine, IAuditLog Audit) NewEngine(TimeProvider clock)
    {
        _ctx ??= SqliteStoreContext.ForFile(_dbPath);
        var hybrid = new SqliteHybridStore(_ctx);
        var audit = new SqliteAuditLog(_ctx);
        var engine = new AegisMemoryEngine(
            new HashingEmbeddingService(256),
            hybrid,
            hybrid,
            new SqliteBlobStore(_ctx),
            _cipher,
            new SoftDecayScorer(),
            new RuleBasedFactExtractor(),
            audit,
            clock);
        return (engine, audit);
    }

    [Fact]
    public async Task Memories_survive_a_restart()
    {
        var ns = new Namespace("owner", MemoryScope.User, "u1");
        var clock = TimeProvider.System;

        // First "process": write some facts.
        var (engine1, _) = NewEngine(clock);
        await engine1.RememberAsync(ns, "User lives in Lisbon",
            GraphEdge.Create("user", "lives in", "Lisbon"));
        await engine1.RememberAsync(ns, "User likes surfing",
            GraphEdge.Create("user", "likes", "surfing"));

        // Second "process": brand-new engine, same database file.
        var (engine2, _) = NewEngine(clock);
        var recalled = await engine2.RecallAsync(ns, "where does the user live", topK: 5);

        Assert.Contains(recalled, r => r.Node.Statement.Contains("Lisbon"));
        var edges = await engine2.NeighborsAsync(ns, "user");
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public async Task Conflict_resolution_persists()
    {
        var ns = new Namespace("owner", MemoryScope.User, "u1");

        var (engine1, _) = NewEngine(TimeProvider.System);
        await engine1.RememberAsync(ns, "User lives in NY", GraphEdge.Create("user", "lives in", "NY"));

        var (engine2, _) = NewEngine(TimeProvider.System);
        var result = await engine2.RememberAsync(ns, "User lives in SF",
            GraphEdge.Create("user", "lives in", "SF"));

        Assert.NotNull(result.SupersededId);
        var edges = await engine2.NeighborsAsync(ns, "user", "lives in");
        Assert.Single(edges);
        Assert.Equal("SF", edges[0].Object);
    }

    [Fact]
    public async Task Audit_chain_persists_and_verifies_across_restart()
    {
        var ns = new Namespace("owner", MemoryScope.User, "u1");

        var (engine1, audit1) = NewEngine(TimeProvider.System);
        await engine1.RememberAsync(ns, "Fact one", GraphEdge.Create("user", "note", "one"));
        await engine1.RememberAsync(ns, "Fact two", GraphEdge.Create("user", "note", "two"));
        Assert.True(await audit1.VerifyAsync());

        // Reopen: the chain continues from the persisted tip and still verifies.
        var (engine2, audit2) = NewEngine(TimeProvider.System);
        await engine2.RememberAsync(ns, "Fact three", GraphEdge.Create("user", "note", "three"));

        Assert.True(await audit2.VerifyAsync());
        var entries = await audit2.ReadAsync();
        // Sequence numbers are contiguous across the restart.
        Assert.Equal(Enumerable.Range(0, entries.Count).Select(i => (long)i), entries.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Blob_payloads_are_encrypted_and_recoverable()
    {
        _ctx ??= SqliteStoreContext.ForFile(_dbPath);
        var blobs = new SqliteBlobStore(_ctx);

        var plaintext = "User lives in Lisbon"u8.ToArray();
        var encrypted = _cipher.Encrypt("owner/user/u1", plaintext);
        var blobRef = await blobs.PutAsync(encrypted);

        // Re-open the store and read the blob back.
        var blobs2 = new SqliteBlobStore(_ctx);
        var stored = await blobs2.GetAsync(blobRef);

        Assert.NotNull(stored);
        Assert.NotEqual(plaintext, stored);                    // stored form is ciphertext
        Assert.Equal(plaintext, _cipher.Decrypt("owner/user/u1", stored!));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
        }
    }
}
