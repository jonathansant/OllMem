using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aegis.Models.Ollama;

/// <summary>
/// Minimal HTTP client for a local Ollama server. Ollama exposes every model it
/// hosts behind the same two endpoints (<c>/api/embed</c> and <c>/api/chat</c>),
/// so this single client works unchanged no matter which open-source model tags
/// are configured — swapping models is configuration, not code.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private readonly Lazy<bool> _available;

    public OllamaClient(OllamaOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
        _http.Timeout = options.RequestTimeout;
        _available = new Lazy<bool>(Probe, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Whether an Ollama server answered the one-time startup probe. The result
    /// is cached for the process lifetime so the chosen backend stays consistent
    /// (embeddings from different models live in incompatible vector spaces).
    /// </summary>
    public bool IsAvailable => _available.Value;

    private bool Probe()
    {
        try
        {
            using var cts = new CancellationTokenSource(_options.ProbeTimeout);
            using var response = _http.GetAsync("api/version", cts.Token)
                .GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Embed a text with the configured embedding model.</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new EmbedRequest(_options.EmbeddingModel, text);
        using var response = await _http.PostAsJsonAsync("api/embed", request, Json, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(Json, ct);
        if (body?.Embeddings is not [{ Length: > 0 } vector, ..])
            throw new InvalidOperationException(
                $"Ollama returned no embedding for model '{_options.EmbeddingModel}'.");
        return vector;
    }

    /// <summary>
    /// Ask the configured chat model a question and get its reply as raw text.
    /// <paramref name="jsonMode"/> asks Ollama to constrain output to valid JSON.
    /// </summary>
    public async Task<string> ChatAsync(
        string system, string user, bool jsonMode, CancellationToken ct = default)
    {
        var request = new ChatRequest(
            _options.ChatModel,
            [new ChatMessage("system", system), new ChatMessage("user", user)],
            Stream: false,
            Format: jsonMode ? "json" : null);

        using var response = await _http.PostAsJsonAsync("api/chat", request, Json, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, ct);
        return body?.Message?.Content
            ?? throw new InvalidOperationException(
                $"Ollama returned no message for model '{_options.ChatModel}'.");
    }

    public void Dispose() => _http.Dispose();

    private sealed record EmbedRequest(string Model, string Input);

    private sealed record EmbedResponse(float[][]? Embeddings);

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ChatRequest(
        string Model,
        ChatMessage[] Messages,
        bool Stream,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format);

    private sealed record ChatResponse(ChatMessage? Message);
}
