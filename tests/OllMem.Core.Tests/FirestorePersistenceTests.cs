using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Embeddings;
using OllMem.Core.Engine;
using OllMem.Core.Extraction;
using OllMem.Core.Retrieval;
using OllMem.Core.Security;
using OllMem.Storage.Firestore;

namespace OllMem.Core.Tests;

/// <summary>
/// Exercises the Firestore-backed stores against the local Firestore emulator.
/// The tests are skipped (they return immediately) unless
/// <c>FIRESTORE_EMULATOR_HOST</c> is set — run them with:
/// <code>
///   gcloud emulators firestore start --host-port=localhost:8791 &amp;
///   FIRESTORE_EMULATOR_HOST=localhost:8791 dotnet test
/// </code>
/// Each test class instance uses a unique collection prefix, so runs are
/// isolated without wiping the emulator. Store instances are rebuilt per
/// "process lifetime" to prove data survives a restart.
/// </summary>
public sealed class FirestorePersistenceTests
{
    private static bool EmulatorAvailable =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST"));

    // A shared cipher/key across "restarts" so encrypted blobs remain readable.
    private readonly AesGcmPayloadCipher _cipher = AesGcmPayloadCipher.CreateEphemeral();
    private readonly string _prefix = $"ollmemtest_{Guid.NewGuid():N}";

    private FirestoreStoreContext NewContext() => new(new FirestoreStoreOptions
    {
        ProjectId = "ollmem-tests",
        CollectionPrefix = _prefix,
    });

    private (OllMemMemoryEngine Engine, IAuditLog Audit) NewEngine(TimeProvider clock)
    {
        var ctx = NewContext();
        var hybrid = new FirestoreHybridStore(ctx);
        var audit = new FirestoreAuditLog(ctx);
        var engine = new OllMemMemoryEngine(
            new HashingEmbeddingService(256),
            hybrid,
            hybrid,
            new FirestoreBlobStore(ctx),
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
        if (!EmulatorAvailable) return;

        var ns = new Namespace("owner", MemoryScope.User, "u1");
        var clock = TimeProvider.System;

        // First "process": write some facts.
        var (engine1, _) = NewEngine(clock);
        await engine1.RememberAsync(ns, "User lives in Lisbon",
            GraphEdge.Create("user", "lives in", "Lisbon"));
        await engine1.RememberAsync(ns, "User likes surfing",
            GraphEdge.Create("user", "likes", "surfing"));

        // Second "process": brand-new stores, same Firestore collections.
        var (engine2, _) = NewEngine(clock);
        var recalled = await engine2.RecallAsync(ns, "where does the user live", topK: 5);

        Assert.Contains(recalled, r => r.Node.Statement.Contains("Lisbon"));
        var edges = await engine2.NeighborsAsync(ns, "user");
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public async Task Conflict_resolution_persists()
    {
        if (!EmulatorAvailable) return;

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
        if (!EmulatorAvailable) return;

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
        if (!EmulatorAvailable) return;

        var blobs = new FirestoreBlobStore(NewContext());

        var plaintext = "User lives in Lisbon"u8.ToArray();
        var encrypted = _cipher.Encrypt("owner/user/u1", plaintext);
        var blobRef = await blobs.PutAsync(encrypted);

        // Re-open the store and read the blob back.
        var blobs2 = new FirestoreBlobStore(NewContext());
        var stored = await blobs2.GetAsync(blobRef);

        Assert.NotNull(stored);
        Assert.NotEqual(plaintext, stored);                    // stored form is ciphertext
        Assert.Equal(plaintext, _cipher.Decrypt("owner/user/u1", stored!));
    }
}
