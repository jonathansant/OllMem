using Aegis.Core.Abstractions;
using Aegis.Core.Domain;
using Aegis.Core.Embeddings;
using Aegis.Core.Engine;
using Aegis.Core.Extraction;
using Aegis.Core.Retrieval;
using Aegis.Core.Security;
using Aegis.Core.Storage;

namespace Aegis.Core.Tests;

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
    public static (AegisMemoryEngine Engine, TestClock Clock, IAuditLog Audit) Create(
        SoftDecayOptions? decay = null)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var audit = new HashChainedAuditLog();
        var engine = new AegisMemoryEngine(
            new HashingEmbeddingService(256),
            new InMemoryVectorStore(),
            new InMemoryGraphStore(),
            new InMemoryBlobStore(),
            AesGcmPayloadCipher.CreateEphemeral(),
            new SoftDecayScorer(decay),
            new RuleBasedFactExtractor(),
            audit,
            clock);

        return (engine, clock, audit);
    }

    public static Namespace UserNamespace(string owner = "owner1", string userId = "u1") =>
        new(owner, MemoryScope.User, userId);
}
