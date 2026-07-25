using OllMem.Core.Abstractions;
using OllMem.Core.Embeddings;
using OllMem.Core.Engine;
using OllMem.Core.Extraction;
using OllMem.Core.Retrieval;
using OllMem.Core.Security;
using OllMem.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OllMem.Core;

/// <summary>
/// Registers the OllMem memory layer with the dependency-injection container.
/// All registrations use <c>TryAdd*</c> so callers can override any component
/// (e.g. a real embedding model or a Walrus blob store) before this is called.
/// </summary>
public static class OllMemServiceCollectionExtensions
{
    public static IServiceCollection AddOllMemMemory(
        this IServiceCollection services,
        Action<OllMemOptions>? configure = null)
    {
        var options = new OllMemOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.TryAddSingleton(TimeProvider.System);

        // Embeddings (local, deterministic by default).
        services.TryAddSingleton<IEmbeddingService>(
            _ => new HashingEmbeddingService(options.EmbeddingDimensions));

        // Hybrid datastore: vector + graph + content-addressed blobs.
        services.TryAddSingleton<IVectorStore, InMemoryVectorStore>();
        services.TryAddSingleton<IGraphStore, InMemoryGraphStore>();
        services.TryAddSingleton<IBlobStore, InMemoryBlobStore>();
        services.TryAddSingleton<IOwnerLinkStore, InMemoryOwnerLinkStore>();

        // Security: Ed25519 signing, AES-GCM payload encryption, audit log.
        services.TryAddSingleton<ISignatureScheme, Ed25519SignatureScheme>();
        services.TryAddSingleton<IPayloadCipher>(_ => CreateCipher(options));
        services.TryAddSingleton<IAuditLog, HashChainedAuditLog>();

        // Retrieval lifecycle.
        services.TryAddSingleton<IDecayScorer>(_ => new SoftDecayScorer(options.Decay));

        // Auto-extraction (rule-based default).
        services.TryAddSingleton<IFactExtractor, RuleBasedFactExtractor>();

        // Orchestrator.
        services.TryAddSingleton<IMemoryEngine, OllMemMemoryEngine>();

        return services;
    }

    private static AesGcmPayloadCipher CreateCipher(OllMemOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.MasterKeyBase64))
            return AesGcmPayloadCipher.CreateEphemeral();

        var key = Convert.FromBase64String(options.MasterKeyBase64);
        return new AesGcmPayloadCipher(key);
    }
}
