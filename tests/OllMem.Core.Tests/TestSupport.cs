using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Embeddings;
using OllMem.Core.Engine;
using OllMem.Core.Extraction;
using OllMem.Core.Retrieval;
using OllMem.Core.Security;
using OllMem.Core.Storage;

namespace OllMem.Core.Tests;

/// <summary>A controllable clock for deterministic decay/recency tests.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
    public void Set(DateTimeOffset value) => _now = value;
}

/// <summary>Builds a fully in-memory engine for tests, exposing the clock.</summary>
public static class EngineFactory
{
    public static (OllMemMemoryEngine Engine, TestClock Clock, IAuditLog Audit) Create(
        SoftDecayOptions? decay = null, IOwnerLinkStore? ownerLinks = null)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var audit = new HashChainedAuditLog();
        var engine = new OllMemMemoryEngine(
            new HashingEmbeddingService(256),
            new InMemoryVectorStore(),
            new InMemoryGraphStore(),
            new InMemoryBlobStore(),
            AesGcmPayloadCipher.CreateEphemeral(),
            new SoftDecayScorer(decay),
            new RuleBasedFactExtractor(),
            audit,
            clock,
            ownerLinks);

        return (engine, clock, audit);
    }

    public static Namespace UserNamespace(string owner = "owner1", string userId = "u1") =>
        new(owner, MemoryScope.User, userId);
}
