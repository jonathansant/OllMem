using OllMem.Core;
using OllMem.Core.Abstractions;
using OllMem.Core.Embeddings;
using OllMem.Core.Consolidation;
using OllMem.Core.Extraction;
using OllMem.Core.Reconciliation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace OllMem.Models.Ollama;

/// <summary>
/// Registers the free, open-source model backend (local Ollama server) for the
/// OllMem memory layer. Call this <b>before</b> <c>AddOllMemMemory()</c> — the
/// core registrations use <c>TryAdd*</c>, so these take precedence.
///
/// The backend is chosen once, at first use: if an Ollama server answers the
/// startup probe, embeddings and fact extraction run on its open-source models;
/// otherwise OllMem silently keeps its deterministic offline defaults. Either
/// way there is no API key and no cost.
/// </summary>
public static class OllMemOllamaServiceCollectionExtensions
{
    public static IServiceCollection AddOllMemOllamaModels(
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
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("OllMem.Models.Ollama");

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
            var dims = sp.GetService<OllMemOptions>()?.EmbeddingDimensions ?? 256;
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

        // Reconciliation and consolidation degrade per call like extraction
        // does, so registering them is always safe: a verdict the model
        // declines to give falls back to the deterministic rule.
        if (options.UseChatReconciliation)
        {
            services.TryAddSingleton<IFactReconciler>(sp =>
            {
                var reconciliation = sp.GetService<ReconciliationOptions>() ?? new ReconciliationOptions();
                return new OllamaFactReconciler(
                    sp.GetRequiredService<OllamaClient>(),
                    new ThresholdFactReconciler(reconciliation),
                    reconciliation,
                    sp.GetService<ILoggerFactory>()?.CreateLogger<OllamaFactReconciler>());
            });
        }

        if (options.UseChatConsolidation)
        {
            services.TryAddSingleton<IMemoryConsolidator>(sp => new OllamaMemoryConsolidator(
                sp.GetRequiredService<OllamaClient>(),
                new PassThroughConsolidator(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<OllamaMemoryConsolidator>()));
        }

        return services;
    }
}
