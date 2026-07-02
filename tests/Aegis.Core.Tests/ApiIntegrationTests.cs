using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Aegis.Core.Tests;

public class ApiIntegrationTests : IClassFixture<ApiIntegrationTests.OfflineFactory>
{
    private readonly HttpClient _client;

    public ApiIntegrationTests(OfflineFactory factory) =>
        _client = factory.CreateClient();

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
                ["Aegis:Ollama:Enabled"] = "false"
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
    public async Task Keypair_endpoint_returns_hex_keys()
    {
        var res = await _client.PostAsync("/v1/keypair", content: null);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(64, json.GetProperty("publicKeyHex").GetString()!.Length);
    }
}
