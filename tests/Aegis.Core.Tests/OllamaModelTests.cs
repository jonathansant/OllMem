using System.Net;
using System.Text;
using System.Text.Json;
using Aegis.Core.Abstractions;
using Aegis.Core.Domain;
using Aegis.Core.Embeddings;
using Aegis.Core.Extraction;
using Aegis.Core.Storage;
using Aegis.Models.Ollama;
using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Core.Tests;

/// <summary>
/// Exercises the Ollama model integration fully offline: a fake HTTP handler
/// plays the Ollama server, and an unroutable endpoint proves the graceful
/// fallback to the deterministic components. No real model is ever contacted.
/// </summary>
public sealed class OllamaModelTests
{
    // Nothing listens here; connections are refused immediately.
    private const string DeadEndpoint = "http://127.0.0.1:1";

    private static OllamaOptions Options(string endpoint = "http://fake-ollama") => new()
    {
        Endpoint = endpoint,
        ProbeTimeout = TimeSpan.FromMilliseconds(500),
        RequestTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task Embeddings_are_parsed_and_normalised()
    {
        using var client = new OllamaClient(Options(), new FakeOllamaHandler
        {
            EmbedVector = [3f, 4f], // length 5 pre-normalisation
        });
        var service = new OllamaEmbeddingService(client);

        var vector = await service.EmbedAsync("hello");

        Assert.Equal(2, vector.Length);
        Assert.Equal(2, service.Dimensions);
        Assert.Equal(0.6f, vector[0], 3);
        Assert.Equal(0.8f, vector[1], 3);
    }

    [Fact]
    public async Task Extractor_parses_llm_json_facts()
    {
        using var client = new OllamaClient(Options(), new FakeOllamaHandler
        {
            ChatContent = """
                {"facts":[
                  {"statement":"User lives in Porto","subject":"user","predicate":"lives in","object":"Porto"},
                  {"statement":"User lives in Porto","subject":"user","predicate":"lives in","object":"porto"},
                  {"statement":"","subject":"user","predicate":"likes","object":"ignored"}
                ]}
                """,
        });
        var extractor = new OllamaFactExtractor(client, new RuleBasedFactExtractor());

        var facts = await extractor.ExtractAsync("User: we relocated to Porto last spring");

        var fact = Assert.Single(facts); // duplicate + blank statement dropped
        Assert.Equal("User lives in Porto", fact.Statement);
        Assert.Equal("lives in", fact.Edge.Predicate);
        Assert.Equal("Porto", fact.Edge.Object);
    }

    [Fact]
    public async Task Extractor_falls_back_to_rules_on_unparseable_output()
    {
        using var client = new OllamaClient(Options(), new FakeOllamaHandler
        {
            ChatContent = "Sure! Here are the facts I found: none really.",
        });
        var extractor = new OllamaFactExtractor(client, new RuleBasedFactExtractor());

        var facts = await extractor.ExtractAsync("User: I live in Berlin");

        Assert.Contains(facts, f => f.Edge.Object == "Berlin");
    }

    [Fact]
    public async Task Extractor_falls_back_to_rules_when_server_is_unreachable()
    {
        using var client = new OllamaClient(Options(DeadEndpoint));
        var extractor = new OllamaFactExtractor(client, new RuleBasedFactExtractor());

        var facts = await extractor.ExtractAsync("User: I live in Berlin");

        Assert.Contains(facts, f => f.Edge.Object == "Berlin");
    }

    [Fact]
    public void Di_uses_ollama_backend_when_server_responds()
    {
        var services = new ServiceCollection();
        services.AddAegisOllamaModels(o =>
        {
            o.Endpoint = Options().Endpoint;
            o.ProbeTimeout = TimeSpan.FromSeconds(5);
        });
        // The DI factory builds its own client, so substitute one wired to the fake.
        services.AddSingleton(sp =>
            new OllamaClient(sp.GetRequiredService<OllamaOptions>(), new FakeOllamaHandler()));
        services.AddAegisMemory();

        using var sp = services.BuildServiceProvider();

        Assert.IsType<OllamaEmbeddingService>(sp.GetRequiredService<IEmbeddingService>());
        Assert.IsType<OllamaFactExtractor>(sp.GetRequiredService<IFactExtractor>());
    }

    [Fact]
    public void Di_falls_back_to_hashing_embedder_when_server_is_unreachable()
    {
        var services = new ServiceCollection();
        services.AddAegisOllamaModels(o =>
        {
            o.Endpoint = DeadEndpoint;
            o.ProbeTimeout = TimeSpan.FromMilliseconds(500);
        });
        services.AddAegisMemory();

        using var sp = services.BuildServiceProvider();

        Assert.IsType<HashingEmbeddingService>(sp.GetRequiredService<IEmbeddingService>());
    }

    [Fact]
    public void Di_keeps_offline_defaults_when_disabled()
    {
        var services = new ServiceCollection();
        services.AddAegisOllamaModels(o => o.Enabled = false);
        services.AddAegisMemory();

        using var sp = services.BuildServiceProvider();

        Assert.IsType<HashingEmbeddingService>(sp.GetRequiredService<IEmbeddingService>());
        Assert.IsType<RuleBasedFactExtractor>(sp.GetRequiredService<IFactExtractor>());
    }

    [Fact]
    public async Task Recall_skips_vectors_from_a_different_embedding_space()
    {
        // Simulates switching embedding models over a persisted store: old
        // 4-dim vectors must be ignored, not crash an 8-dim query.
        var store = new InMemoryVectorStore();
        var ns = new Namespace("o", MemoryScope.User, "u1");

        var old = TestMemory(ns, "old fact", dimensions: 4);
        var current = TestMemory(ns, "current fact", dimensions: 8);
        await store.UpsertAsync(old);
        await store.UpsertAsync(current);

        var results = await store.SearchAsync(ns, new float[8], topK: 10);

        var hit = Assert.Single(results);
        Assert.Equal("current fact", hit.Node.Statement);
    }

    private static MemoryNode TestMemory(
        Namespace ns, string statement, int dimensions) => new()
    {
        Id = Guid.NewGuid(),
        Namespace = ns,
        Statement = statement,
        Embedding = new float[dimensions],
        Edge = GraphEdge.Create("user", "note", statement),
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Plays an Ollama server: /api/version, /api/embed, /api/chat.</summary>
    private sealed class FakeOllamaHandler : HttpMessageHandler
    {
        public float[] EmbedVector { get; init; } = [1f, 0f];
        public string ChatContent { get; init; } = """{"facts":[]}""";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var payload = path switch
            {
                "/api/version" => """{"version":"0.0.0-fake"}""",
                "/api/embed" => JsonSerializer.Serialize(new { embeddings = new[] { EmbedVector } }),
                "/api/chat" => JsonSerializer.Serialize(new { message = new { role = "assistant", content = ChatContent } }),
                _ => throw new InvalidOperationException($"Unexpected path {path}"),
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
