using Aegis.Core.Abstractions;

namespace Aegis.Models.Ollama;

/// <summary>
/// <see cref="IEmbeddingService"/> backed by an open-source embedding model
/// served locally by Ollama — real semantic similarity ("Porto" ≈ "lives in
/// Portugal") with no API key and no per-token cost. Vectors are L2-normalised
/// so the cosine math downstream matches the deterministic default.
/// </summary>
public sealed class OllamaEmbeddingService : IEmbeddingService
{
    private readonly OllamaClient _client;
    private int _dimensions;

    public OllamaEmbeddingService(OllamaClient client) => _client = client;

    /// <summary>
    /// Dimensionality is a property of the configured model (e.g. 768 for
    /// nomic-embed-text) and is discovered from the first embedding produced.
    /// </summary>
    public int Dimensions => _dimensions;

    public async ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vector = await _client.EmbedAsync(text, ct);
        _dimensions = vector.Length;
        Normalize(vector);
        return vector;
    }

    private static void Normalize(float[] vector)
    {
        double sumSq = 0;
        foreach (var v in vector)
            sumSq += (double)v * v;

        if (sumSq <= 0)
            return;

        var inv = (float)(1.0 / Math.Sqrt(sumSq));
        for (var i = 0; i < vector.Length; i++)
            vector[i] *= inv;
    }
}
