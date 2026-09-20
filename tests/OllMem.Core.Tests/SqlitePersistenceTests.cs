using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Embeddings;
using OllMem.Core.Engine;
using OllMem.Core.Extraction;
using OllMem.Core.Retrieval;
using OllMem.Core.Security;
using OllMem.Storage.Sqlite;

namespace OllMem.Core.Tests;

/// <summary>
/// Exercises the SQLite-backed stores. Each test uses a unique database file and
/// builds a fresh set of store instances per "process lifetime" so we can prove
/// data survives a restart.
/// </summary>
public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ollmem-test-{Guid.NewGuid():N}.db");

    // A shared cipher/key across "restarts" so encrypted blobs remain readable.
    private readonly AesGcmPayloadCipher _cipher = AesGcmPayloadCipher.CreateEphemeral();

    // The context must be created once per file (it pools connections); reuse it
    // to simulate the same database being reopened by a new engine instance.
    private SqliteStoreContext? _ctx;

    private (OllMemMemoryEngine Engine, IAuditLog Audit) NewEngine(TimeProvider clock)
    {
        _ctx ??= SqliteStoreContext.ForFile(_dbPath);
        var hybrid = new SqliteHybridStore(_ctx);
        var audit = new SqliteAuditLog(_ctx);
        var engine = new OllMemMemoryEngine(
            new HashingEmbeddingService(256),
            hybrid,
            hybrid,
            new SqliteBlobStore(_ctx),
            _cipher,
            new SoftDecayScorer(),
            new RuleBasedFactExtractor(),
            audit,
            clock,
            new SqliteOwnerLinkStore(_ctx));
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

    [Fact]
    public async Task Recall_reaches_a_sibling_scope_of_the_same_owner()
    {
        var userNs = new Namespace("owner", MemoryScope.User, "u1");
        var agentNs = new Namespace("owner", MemoryScope.Agent, "a1");

        var (engine, _) = NewEngine(TimeProvider.System);
        await engine.RememberAsync(agentNs, "Deploys run through service.sh",
            GraphEdge.Create("deploy", "runs through", "service.sh"));

        // Nothing was ever written to userNs, so this can only come from the
        // agent-scope sibling — proving SearchOwnersAsync spans scopes.
        var recalled = await engine.RecallAsync(userNs, "how do deploys run", topK: 5);
        Assert.Contains(recalled, r => r.Node.Statement.Contains("service.sh"));
        Assert.All(recalled, r => Assert.True(r.NamespaceAffinity < 1.0));
    }

    [Fact]
    public async Task Owner_links_persist_across_a_restart()
    {
        var laptop = new Namespace("laptop-key", MemoryScope.User, "u1");
        var desktop = new Namespace("desktop-key", MemoryScope.User, "u1");

        // First "process": write on the laptop identity and link the two.
        var (engine1, _) = NewEngine(TimeProvider.System);
        await engine1.RememberAsync(laptop, "Deploys run through service.sh",
            GraphEdge.Create("deploy", "runs through", "service.sh"));
        await engine1.LinkOwnersAsync("laptop-key", "desktop-key");

        // Second "process": brand-new engine, same database file.
        var (engine2, _) = NewEngine(TimeProvider.System);
        Assert.Equal(
            new[] { "desktop-key", "laptop-key" },
            (await engine2.GetLinkedOwnersAsync("desktop-key")).OrderBy(o => o));

        var recalled = await engine2.RecallAsync(desktop, "how do deploys run", topK: 5);
        Assert.Contains(recalled, r => r.Node.Statement.Contains("service.sh"));
    }

    [Fact]
    public async Task Unlinked_owners_are_not_reachable()
    {
        var alice = new Namespace("alice", MemoryScope.User, "u1");
        var mallory = new Namespace("mallory", MemoryScope.User, "u1");

        var (engine, _) = NewEngine(TimeProvider.System);
        await engine.RememberAsync(alice, "Deploys run through service.sh",
            GraphEdge.Create("deploy", "runs through", "service.sh"));

        Assert.Empty(await engine.RecallAsync(mallory, "how do deploys run", topK: 5));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Retirement_and_history_survive_a_restart()
    {
        var ns = new Namespace("owner", MemoryScope.User, "u1");
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = clock.GetUtcNow();

        var (engine1, _) = NewEngine(clock);
        await engine1.RememberAsync(ns, "User lives in Lisbon", GraphEdge.Create("user", "lives in", "Lisbon"));
        clock.Advance(TimeSpan.FromDays(3));
        await engine1.RememberAsync(ns, "User lives in Porto", GraphEdge.Create("user", "lives in", "Porto"));
        var moved = clock.GetUtcNow();

        // Second "process": the validity windows have to come back off disk.
        var (engine2, _) = NewEngine(clock);
        var history = await engine2.HistoryAsync(ns, "user", "lives in");

        Assert.Equal(2, history.Count);
        Assert.Equal(start, history[0].ValidFrom);
        Assert.Equal(moved, history[0].ValidTo);
        Assert.Null(history[1].ValidTo);

        var then = await engine2.RecallAsync(ns, "where does the user live", topK: 5, asOf: start);
        Assert.Contains("Lisbon", Assert.Single(then).Node.Statement);
    }

    [Fact]
    public async Task A_database_from_before_retirement_tracking_is_migrated_in_place()
    {
        var legacyPath = Path.Combine(Path.GetTempPath(), $"ollmem-legacy-{Guid.NewGuid():N}.db");
        var id = Guid.NewGuid();
        try
        {
            CreateLegacyDatabase(legacyPath, id);

            // Opening it with the current code adds the column and leaves the
            // existing row readable, with no retirement recorded.
            var ctx = SqliteStoreContext.ForFile(legacyPath);
            var store = new SqliteHybridStore(ctx);

            var node = await store.GetAsync(id);
            Assert.NotNull(node);
            Assert.Equal("User lives in Lisbon", node!.Statement);
            Assert.Null(node.RetiredAt);

            // And the column is now writable like any other.
            node.RetiredAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            node.Status = MemoryStatus.Superseded;
            await store.UpsertAsync(node);

            var reloaded = await store.GetAsync(id);
            Assert.Equal(node.RetiredAt, reloaded!.RetiredAt);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(legacyPath))
                File.Delete(legacyPath);
        }
    }

    /// <summary>Writes a memories table in the shape OllMem used before retirement tracking.</summary>
    private static void CreateLegacyDatabase(string path, Guid id)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();

        using var create = conn.CreateCommand();
        create.CommandText =
            """
            CREATE TABLE memories (
                id               TEXT PRIMARY KEY,
                namespace_key    TEXT NOT NULL,
                owner            TEXT NOT NULL,
                scope            INTEGER NOT NULL,
                scope_id         TEXT NOT NULL,
                statement        TEXT NOT NULL,
                subject          TEXT NOT NULL,
                predicate        TEXT NOT NULL,
                object           TEXT NOT NULL,
                embedding        BLOB NOT NULL,
                blob_ref         TEXT,
                created_at       TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                access_count     INTEGER NOT NULL,
                status           INTEGER NOT NULL,
                superseded_by    TEXT
            );
            """;
        create.ExecuteNonQuery();

        using var insert = conn.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO memories
                (id, namespace_key, owner, scope, scope_id, statement, subject, predicate, object,
                 embedding, blob_ref, created_at, last_accessed_at, access_count, status, superseded_by)
            VALUES
                ($id, 'owner/user/u1', 'owner', 1, 'u1', 'User lives in Lisbon', 'user', 'lives in', 'Lisbon',
                 $embedding, NULL, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00',
                 0, 0, NULL);
            """;
        insert.Parameters.AddWithValue("$id", id.ToString());
        insert.Parameters.AddWithValue("$embedding", new byte[16]);
        insert.ExecuteNonQuery();
    }
}
