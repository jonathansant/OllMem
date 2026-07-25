using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OllMem.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OllMem.Core.Tests;

public class ApiIntegrationTests : IClassFixture<ApiIntegrationTests.OfflineFactory>
{
    private readonly HttpClient _client;
    private readonly OfflineFactory _factory;

    public ApiIntegrationTests(OfflineFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Pins the deterministic offline components so results don't depend on
    /// whether the developer machine happens to run an Ollama server.
    /// </summary>
    public sealed class OfflineFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OllMem:Ollama:Enabled"] = "false"
            }));
            return base.CreateHost(builder);
        }
    }

    [Fact]
    public async Task Health_returns_ok()
    {
        var res = await _client.GetAsync("/health");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Remember_then_recall_roundtrip()
    {
        var owner = $"owner-{Guid.NewGuid():N}";

        var remember = await _client.PostAsJsonAsync("/v1/remember", new
        {
            owner,
            scope = "user",
            scopeId = "u1",
            statement = "User lives in San Francisco",
            subject = "user",
            predicate = "lives in",
            @object = "San Francisco"
        });
        remember.EnsureSuccessStatusCode();

        var recall = await _client.PostAsJsonAsync("/v1/recall", new
        {
            owner,
            scope = "user",
            scopeId = "u1",
            query = "where does the user live",
            topK = 3
        });
        recall.EnsureSuccessStatusCode();

        var results = await recall.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(results.GetArrayLength() >= 1);
        Assert.Contains("San Francisco",
            results[0].GetProperty("memory").GetProperty("statement").GetString());
    }

    [Fact]
    public async Task Extract_stores_facts_and_resolves_conflict()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        object Body(string t) => new { owner, scope = "user", scopeId = "u1", transcript = t };

        await _client.PostAsJsonAsync("/v1/extract", Body("User: I live in NY."));
        await _client.PostAsJsonAsync("/v1/extract", Body("User: I moved to SF."));

        var neighbors = await _client.GetAsync(
            $"/v1/neighbors?owner={owner}&scope=user&scopeId=u1&subject=user&predicate=lives%20in");
        neighbors.EnsureSuccessStatusCode();

        var edges = await neighbors.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, edges.GetArrayLength());
        Assert.Equal("SF", edges[0].GetProperty("object").GetString());
    }

    [Fact]
    public async Task Audit_chain_is_intact()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/v1/remember", new
        {
            owner, scope = "user", scopeId = "u1", statement = "User likes tea"
        });

        var audit = await _client.GetAsync("/v1/audit");
        audit.EnsureSuccessStatusCode();
        var json = await audit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("chainIntact").GetBoolean());
    }

    [Fact]
    public async Task Missing_owner_is_rejected()
    {
        var res = await _client.PostAsJsonAsync("/v1/recall", new
        {
            owner = "", scope = "user", scopeId = "u1", query = "x"
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Graph_returns_entities_and_memory_edges()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        object Fact(string statement, string predicate, string @object) => new
        {
            owner, scope = "user", scopeId = "u1",
            statement, subject = "user", predicate, @object
        };

        await _client.PostAsJsonAsync("/v1/remember", Fact("User lives in San Francisco", "lives in", "San Francisco"));
        await _client.PostAsJsonAsync("/v1/remember", Fact("User works at Acme", "works at", "Acme"));

        var res = await _client.GetAsync($"/v1/graph?owner={owner}&scope=user&scopeId=u1");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("namespaces").GetArrayLength());
        Assert.Equal(2, json.GetProperty("edges").GetArrayLength());
        // Entities: user, San Francisco, Acme.
        Assert.Equal(3, json.GetProperty("nodes").GetArrayLength());

        var user = json.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == "user");
        Assert.Equal(2, user.GetProperty("degree").GetInt32());
    }

    [Fact]
    public async Task Graph_without_parameters_spans_all_namespaces()
    {
        var ownerA = $"owner-{Guid.NewGuid():N}";
        var ownerB = $"owner-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/v1/remember", new
        {
            owner = ownerA, scope = "user", scopeId = "u1",
            statement = "A likes tea", subject = "user", predicate = "likes", @object = "tea"
        });
        await _client.PostAsJsonAsync("/v1/remember", new
        {
            owner = ownerB, scope = "agent", scopeId = "bot",
            statement = "B likes coffee", subject = "user", predicate = "likes", @object = "coffee"
        });

        var res = await _client.GetAsync("/v1/graph");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        var keys = json.GetProperty("namespaces").EnumerateArray()
            .Select(n => n.GetProperty("key").GetString()).ToList();
        Assert.Contains($"{ownerA}/user/u1", keys);
        Assert.Contains($"{ownerB}/agent/bot", keys);

        // Entities never merge across namespaces: each owner keeps its own "user".
        var userNodes = json.GetProperty("nodes").EnumerateArray()
            .Where(n => n.GetProperty("id").GetString() == "user")
            .Select(n => n.GetProperty("ns").GetString()).ToList();
        Assert.Contains($"{ownerA}/user/u1", userNodes);
        Assert.Contains($"{ownerB}/agent/bot", userNodes);

        // Every edge is tagged with its namespace so the UI can filter.
        Assert.All(json.GetProperty("edges").EnumerateArray(),
            e => Assert.False(string.IsNullOrEmpty(e.GetProperty("ns").GetString())));
    }

    [Fact]
    public async Task Graph_reports_owner_links_and_reachability_from_a_viewpoint()
    {
        var laptop = $"laptop-{Guid.NewGuid():N}";
        var desktop = $"desktop-{Guid.NewGuid():N}";
        var stranger = $"stranger-{Guid.NewGuid():N}";

        foreach (var (owner, scope, scopeId) in new[]
                 { (laptop, "user", "u1"), (laptop, "agent", "a1"), (desktop, "user", "u1"), (stranger, "user", "u1") })
        {
            await _client.PostAsJsonAsync("/v1/remember", new
            {
                owner, scope, scopeId,
                statement = "likes tea", subject = "user", predicate = "likes", @object = "tea"
            });
        }

        // Linking is engine-side only — the HTTP API deliberately exposes no
        // endpoint for it, so drive it through the host's own engine instance.
        var engine = _factory.Services.GetRequiredService<IMemoryEngine>();
        await engine.LinkOwnersAsync(laptop, desktop);

        var res = await _client.GetAsync($"/v1/graph?fromOwner={laptop}&fromScope=user&fromScopeId=u1");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        var pairs = json.GetProperty("ownerLinks").EnumerateArray()
            .Select(l => (l.GetProperty("ownerA").GetString(), l.GetProperty("ownerB").GetString()))
            .ToList();
        Assert.Contains(pairs, p => p == OrderedPair(laptop, desktop));

        var byKey = json.GetProperty("namespaces").EnumerateArray()
            .ToDictionary(n => n.GetProperty("key").GetString()!, n => n.GetProperty("affinity"));

        // The viewpoint itself, a sibling scope, a linked owner, and a stranger.
        Assert.Equal(1.0, byKey[$"{laptop}/user/u1"].GetDouble());
        Assert.Equal(0.75, byKey[$"{laptop}/agent/a1"].GetDouble());
        Assert.Equal(0.9, byKey[$"{desktop}/user/u1"].GetDouble());
        Assert.Equal(JsonValueKind.Null, byKey[$"{stranger}/user/u1"].ValueKind);
    }

    [Fact]
    public async Task Graph_without_a_viewpoint_leaves_affinity_unset()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/v1/remember", new
        {
            owner, scope = "user", scopeId = "u1",
            statement = "likes tea", subject = "user", predicate = "likes", @object = "tea"
        });

        var res = await _client.GetAsync("/v1/graph");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        // The default view is "everything, unscored" — the UI relies on this.
        Assert.All(json.GetProperty("namespaces").EnumerateArray(),
            n => Assert.Equal(JsonValueKind.Null, n.GetProperty("affinity").ValueKind));
    }

    private static (string, string) OrderedPair(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    [Fact]
    public async Task Graph_visualizer_page_is_served()
    {
        var res = await _client.GetAsync("/graph");
        res.EnsureSuccessStatusCode();
        Assert.Contains("text/html", res.Content.Headers.ContentType!.ToString());
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("/v1/graph", html);
    }

    [Fact]
    public async Task Keypair_endpoint_returns_hex_keys()
    {
        var res = await _client.PostAsync("/v1/keypair", content: null);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(64, json.GetProperty("publicKeyHex").GetString()!.Length);
    }
}
