using OllMem.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OllMem.Storage.Firestore;

/// <summary>
/// Registers Google Cloud Firestore persistence for the OllMem memory layer:
/// memories live in a managed cloud database, so any machine, device or agent
/// pointed at the same project shares one memory. Call this <b>before</b>
/// <c>AddOllMemMemory()</c>: the core registrations use <c>TryAdd*</c>, so
/// whichever store is registered first wins. The hybrid store instance backs
/// both <see cref="IVectorStore"/> and <see cref="IGraphStore"/>.
/// </summary>
public static class OllMemFirestoreServiceCollectionExtensions
{
    /// <summary>Persist to Firestore in the given Google Cloud / Firebase project.</summary>
    public static IServiceCollection AddOllMemFirestoreStorage(
        this IServiceCollection services, string projectId) =>
        services.AddOllMemFirestoreStorage(o => o.ProjectId = projectId);

    public static IServiceCollection AddOllMemFirestoreStorage(
        this IServiceCollection services, Action<FirestoreStoreOptions> configure)
    {
        var options = new FirestoreStoreOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ProjectId))
            throw new ArgumentException("FirestoreStoreOptions.ProjectId is required.", nameof(configure));

        services.TryAddSingleton(options);
        services.TryAddSingleton(sp => new FirestoreStoreContext(sp.GetRequiredService<FirestoreStoreOptions>()));
        services.TryAddSingleton<FirestoreHybridStore>();
        services.TryAddSingleton<IVectorStore>(sp => sp.GetRequiredService<FirestoreHybridStore>());
        services.TryAddSingleton<IGraphStore>(sp => sp.GetRequiredService<FirestoreHybridStore>());
        services.TryAddSingleton<IBlobStore, FirestoreBlobStore>();
        services.TryAddSingleton<IAuditLog, FirestoreAuditLog>();
        services.TryAddSingleton<IOwnerLinkStore, FirestoreOwnerLinkStore>();
        return services;
    }
}
