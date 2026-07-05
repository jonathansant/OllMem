namespace OllMem.Core.Abstractions;

/// <summary>
/// Turns text into a fixed-length, L2-normalised semantic vector. The default
/// implementation is fully local and deterministic so the system runs without
/// external API keys; production deployments can swap in a real embedding model.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Dimensionality of the vectors this service produces.</summary>
    int Dimensions { get; }

    /// <summary>Embed a single text into a normalised vector.</summary>
    ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
