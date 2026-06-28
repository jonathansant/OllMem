using Aegis.Core.Abstractions;
using Aegis.Core.Embeddings;
using Aegis.Core.Engine;
using Aegis.Core.Extraction;
using Aegis.Core.Retrieval;
using Aegis.Core.Security;
using Aegis.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aegis.Core;

/// <summary>
/// Registers the Aegis memory layer with the dependency-injection container.
/// All registrations use <c>TryAdd*</c> so callers can override any component
/// (e.g. a real embedding model or a Walrus blob store) before this is called.
/// </summary>
public static class AegisServiceCollectionExtensions
{
    public static IServiceCollection AddAegisMemory(
        this IServiceCollection services,
        Action<AegisOptions>? configure = null)
    {
        var options = new AegisOptions();
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

        // Security: Ed25519 signing, AES-GCM payload encryption, audit log.
        services.TryAddSingleton<ISignatureScheme, Ed25519SignatureScheme>();
        services.TryAddSingleton<IPayloadCipher>(_ => CreateCipher(options));
        services.TryAddSingleton<IAuditLog, HashChainedAuditLog>();

        // Retrieval lifecycle.
        services.TryAddSingleton<IDecayScorer>(_ => new SoftDecayScorer(options.Decay));

        // Auto-extraction (rule-based default).
        services.TryAddSingleton<IFactExtractor, RuleBasedFactExtractor>();

        // Orchestrator.
        services.TryAddSingleton<IMemoryEngine, AegisMemoryEngine>();

        return services;
    }

    private static AesGcmPayloadCipher CreateCipher(AegisOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.MasterKeyBase64))
            return AesGcmPayloadCipher.CreateEphemeral();

        var key = Convert.FromBase64String(options.MasterKeyBase64);
        return new AesGcmPayloadCipher(key);
    }
}
