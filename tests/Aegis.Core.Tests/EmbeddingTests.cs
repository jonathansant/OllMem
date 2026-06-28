using Aegis.Core.Embeddings;

namespace Aegis.Core.Tests;

public class EmbeddingTests
{
    [Fact]
    public async Task Embedding_is_deterministic()
    {
        var svc = new HashingEmbeddingService(128);
        var a = await svc.EmbedAsync("the user lives in San Francisco");
        var b = await svc.EmbedAsync("the user lives in San Francisco");
        Assert.Equal(a, b);
        Assert.Equal(128, a.Length);
    }

    [Fact]
    public async Task Embedding_is_unit_normalised()
    {
        var svc = new HashingEmbeddingService();
        var v = await svc.EmbedAsync("hello world from aegis");
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        Assert.InRange(norm, 0.999, 1.001);
    }

    [Fact]
    public async Task Similar_text_scores_higher_than_unrelated_text()
    {
        var svc = new HashingEmbeddingService(512);
        var query = await svc.EmbedAsync("user favorite programming language");
        var related = await svc.EmbedAsync("the user's favorite programming language is rust");
        var unrelated = await svc.EmbedAsync("the weather in london is rainy today");

        var simRelated = HashingEmbeddingService.CosineSimilarity(query, related);
        var simUnrelated = HashingEmbeddingService.CosineSimilarity(query, unrelated);

        Assert.True(simRelated > simUnrelated,
            $"expected related ({simRelated:F3}) > unrelated ({simUnrelated:F3})");
    }

    [Fact]
    public async Task Empty_text_produces_zero_vector()
    {
        var svc = new HashingEmbeddingService(64);
        var v = await svc.EmbedAsync("   ");
        Assert.All(v, x => Assert.Equal(0f, x));
    }
}
