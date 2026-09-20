using System.Net;
using System.Text;
using System.Text.Json;
using OllMem.Core.Abstractions;
using OllMem.Core.Consolidation;
using OllMem.Core.Domain;
using OllMem.Core.Embeddings;
using OllMem.Core.Engine;
using OllMem.Core.Extraction;
using OllMem.Core.Reconciliation;
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
        SoftDecayOptions? decay = null,
        IOwnerLinkStore? ownerLinks = null,
        IVectorStore? vectors = null,
        IFactReconciler? reconciler = null,
        IMemoryConsolidator? consolidator = null,
        ReconciliationOptions? reconciliation = null)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var audit = new HashChainedAuditLog();
        var engine = new OllMemMemoryEngine(
            new HashingEmbeddingService(256),
            vectors ?? new InMemoryVectorStore(),
            new InMemoryGraphStore(),
            new InMemoryBlobStore(),
            AesGcmPayloadCipher.CreateEphemeral(),
            new SoftDecayScorer(decay),
            new RuleBasedFactExtractor(),
            audit,
            clock,
            ownerLinks,
            reconciler,
            consolidator,
            reconciliation);

        return (engine, clock, audit);
    }

    public static Namespace UserNamespace(string owner = "owner1", string userId = "u1") =>
        new(owner, MemoryScope.User, userId);

    public static Namespace SessionNamespace(string owner = "owner1", string sessionId = "s1") =>
        new(owner, MemoryScope.Session, sessionId);

    public static Namespace AgentNamespace(string owner = "owner1", string agentId = "a1") =>
        new(owner, MemoryScope.Agent, agentId);
}

/// <summary>
/// Plays an Ollama server over a fake transport so the model-backed components
/// can be exercised without a real model. <see cref="ChatResponder"/> lets a
/// test answer differently per request; otherwise every chat returns
/// <see cref="ChatContent"/>.
/// </summary>
public sealed class FakeOllamaHandler : HttpMessageHandler
{
    public float[] EmbedVector { get; init; } = [1f, 0f];

    public string ChatContent { get; init; } = """{"facts":[]}""";

    public Func<string, string>? ChatResponder { get; init; }

    /// <summary>Bodies of the chat requests received, in order.</summary>
    public List<string> ChatRequests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        string payload;
        switch (path)
        {
            case "/api/version":
                payload = """{"version":"0.0.0-fake"}""";
                break;

            case "/api/embed":
                payload = JsonSerializer.Serialize(new { embeddings = new[] { EmbedVector } });
                break;

            case "/api/chat":
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                ChatRequests.Add(body);
                var content = ChatResponder?.Invoke(body) ?? ChatContent;
                payload = JsonSerializer.Serialize(new { message = new { role = "assistant", content } });
                break;

            default:
                throw new InvalidOperationException($"Unexpected path {path}");
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
    }
}
