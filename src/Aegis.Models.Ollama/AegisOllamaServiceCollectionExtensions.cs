using Aegis.Core;
using Aegis.Core.Abstractions;
using Aegis.Core.Embeddings;
using Aegis.Core.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aegis.Models.Ollama;

/// <summary>
/// Registers the free, open-source model backend (local Ollama server) for the
/// Aegis memory layer. Call this <b>before</b> <c>AddAegisMemory()</c> — the
/// core registrations use <c>TryAdd*</c>, so these take precedence.
///
/// The backend is chosen once, at first use: if an Ollama server answers the
/// startup probe, embeddings and fact extraction run on its open-source models;
/// otherwise Aegis silently keeps its deterministic offline defaults. Either
/// way there is no API key and no cost.
/// </summary>
public static class AegisOllamaServiceCollectionExtensions
{
    public static IServiceCollection AddAegisOllamaModels(
        this IServiceCollection services, Action<OllamaOptions>? configure = null)
    {
        var options = new OllamaOptions();
        configure?.Invoke(options);
        if (!options.Enabled)
            return services;

        services.TryAddSingleton(options);
        services.TryAddSingleton(sp => new OllamaClient(sp.GetRequiredService<OllamaOptions>()));

        // Picked once per process: mixing vectors from different embedding
        // models would corrupt similarity search, so there is no per-call
        // fallback here — Ollama either owns the vector space or the
        // deterministic hashing model does.
        services.TryAddSingleton<IEmbeddingService>(sp =>
        {
            var client = sp.GetRequiredService<OllamaClient>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Aegis.Models.Ollama");

            if (client.IsAvailable)
            {
                var opts = sp.GetRequiredService<OllamaOptions>();
                logger?.LogInformation(
                    "Ollama detected at {Endpoint}; embeddings use open-source model '{Model}'.",
                    opts.Endpoint, opts.EmbeddingModel);
                return new OllamaEmbeddingService(client);
            }

            logger?.LogWarning(
                "No Ollama server reachable at {Endpoint}; falling back to the offline hashing embedder. " +
                "Install Ollama (https://ollama.com) and `ollama pull {Model}` for semantic recall.",
                sp.GetRequiredService<OllamaOptions>().Endpoint,
                sp.GetRequiredService<OllamaOptions>().EmbeddingModel);
            var dims = sp.GetService<AegisOptions>()?.EmbeddingDimensions ?? 256;
            return new HashingEmbeddingService(dims);
        });

        // Extraction degrades per call, so it is always safe to register: a
        // fact extracted by rules is compatible with one extracted by the LLM.
        if (options.UseChatExtraction)
        {
            services.TryAddSingleton<IFactExtractor>(sp => new OllamaFactExtractor(
                sp.GetRequiredService<OllamaClient>(),
                new RuleBasedFactExtractor(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<OllamaFactExtractor>()));
        }

        return services;
    }
}
