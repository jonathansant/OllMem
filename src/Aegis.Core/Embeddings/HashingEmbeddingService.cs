using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aegis.Core.Abstractions;

namespace Aegis.Core.Embeddings;

/// <summary>
/// A deterministic, dependency-free embedding model based on the hashing trick:
/// tokens are hashed into a fixed-dimensional bag, sub-linearly weighted, and
/// L2-normalised. It captures lexical overlap well enough for similarity search
/// in tests and offline use, and requires no model weights or API keys.
/// Swap in a transformer-based <see cref="IEmbeddingService"/> for production
/// semantic quality.
/// </summary>
public sealed class HashingEmbeddingService : IEmbeddingService
{
    private static readonly char[] Separators =
        " \t\r\n.,;:!?\"'()[]{}<>/\\|`~@#$%^&*-_=+".ToCharArray();

    public int Dimensions { get; }

    public HashingEmbeddingService(int dimensions = 256)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Dimensions must be positive.");
        Dimensions = dimensions;
    }

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vector = new float[Dimensions];
        if (string.IsNullOrWhiteSpace(text))
            return ValueTask.FromResult(vector);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.ToLowerInvariant();
            counts[token] = counts.TryGetValue(token, out var c) ? c + 1 : 1;
        }

        foreach (var (token, count) in counts)
        {
            var (bucket, sign) = Bucket(token);
            // Sub-linear term weighting dampens the effect of very frequent tokens.
            vector[bucket] += sign * (float)(1.0 + Math.Log(count));
        }

        Normalize(vector);
        return ValueTask.FromResult(vector);
    }

    private (int Bucket, int Sign) Bucket(string token)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), hash);
        // First 4 bytes pick the dimension; next byte chooses a sign so that
        // unrelated tokens are less likely to constructively collide.
        var raw = BitConverter.ToUInt32(hash[..4]);
        var bucket = (int)(raw % (uint)Dimensions);
        var sign = (hash[4] & 1) == 0 ? 1 : -1;
        return (bucket, sign);
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

    /// <summary>Cosine similarity for already-normalised vectors, clamped to [0,1].</summary>
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException(
                $"Vector length mismatch: {a.Length} vs {b.Length}.", nameof(b));

        double dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += (double)a[i] * b[i];

        // Inputs are unit vectors, so the dot product is already the cosine.
        return Math.Clamp(dot, 0.0, 1.0);
    }

    internal static string FormatScore(double value) =>
        value.ToString("0.000", CultureInfo.InvariantCulture);
}
