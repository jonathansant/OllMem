using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aegis.Core.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Aegis.Core.Tests;

public class SignatureAuthTests : IClassFixture<SignatureAuthTests.SignedFactory>
{
    private readonly HttpClient _client;

    public SignatureAuthTests(SignedFactory factory) => _client = factory.CreateClient();

    /// <summary>A factory that turns on mandatory Ed25519 signing.</summary>
    public sealed class SignedFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aegis:RequireSignature"] = "true",
                ["Aegis:Ollama:Enabled"] = "false"
            }));
            return base.CreateHost(builder);
        }
    }

    [Fact]
    public async Task Unsigned_request_is_rejected()
    {
        var res = await _client.PostAsJsonContent("/v1/recall",
            """{"owner":"x","scope":"user","scopeId":"u1","query":"hi"}""");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Correctly_signed_request_is_accepted()
    {
        var scheme = new Ed25519SignatureScheme();
        var (pub, priv) = scheme.GenerateKeyPair();

        var body = $$"""{"owner":"{{pub}}","scope":"user","scopeId":"u1","statement":"User likes tea"}""";
        var req = Sign(scheme, priv, pub, HttpMethod.Post, "/v1/remember", body);

        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Tampered_body_after_signing_is_rejected()
    {
        var scheme = new Ed25519SignatureScheme();
        var (pub, priv) = scheme.GenerateKeyPair();

        var signedBody = $$"""{"owner":"{{pub}}","scope":"user","scopeId":"u1","statement":"original"}""";
        var req = Sign(scheme, priv, pub, HttpMethod.Post, "/v1/remember", signedBody);

        // Swap the body for a different one while keeping the signature headers.
        var tampered = signedBody.Replace("original", "tampered");
        req.Content = new StringContent(tampered, Encoding.UTF8, "application/json");

        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    private static HttpRequestMessage Sign(
        Ed25519SignatureScheme scheme, string priv, string pub,
        HttpMethod method, string path, string body)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var canonical = $"{method.Method}\n{path}\n{ts}\n{bodyHash}";
        var sig = scheme.Sign(priv, Encoding.UTF8.GetBytes(canonical));

        var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Aegis-PublicKey", pub);
        req.Headers.Add("X-Aegis-Timestamp", ts.ToString());
        req.Headers.Add("X-Aegis-Signature", sig);
        return req;
    }
}

internal static class HttpClientJsonExtensions
{
    public static Task<HttpResponseMessage> PostAsJsonContent(this HttpClient client, string url, string json) =>
        client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
}
