using Aegis.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aegis.Storage.Sqlite;

/// <summary>
/// Registers SQLite-backed persistence for the Aegis memory layer. Call this
/// <b>before</b> <c>AddAegisMemory()</c>: the core registrations use
/// <c>TryAdd*</c>, so whichever store is registered first wins. The hybrid store
/// instance backs both <see cref="IVectorStore"/> and <see cref="IGraphStore"/>.
/// </summary>
public static class AegisSqliteServiceCollectionExtensions
{
    /// <summary>Persist to a SQLite database file at <paramref name="path"/>.</summary>
    public static IServiceCollection AddAegisSqliteStorage(this IServiceCollection services, string path) =>
        services.AddAegisSqliteStorage(SqliteStoreContext.ForFile(path));

    public static IServiceCollection AddAegisSqliteStorage(
        this IServiceCollection services, SqliteStoreContext context)
    {
        services.TryAddSingleton(context);
        services.TryAddSingleton<SqliteHybridStore>();
        services.TryAddSingleton<IVectorStore>(sp => sp.GetRequiredService<SqliteHybridStore>());
        services.TryAddSingleton<IGraphStore>(sp => sp.GetRequiredService<SqliteHybridStore>());
        services.TryAddSingleton<IBlobStore, SqliteBlobStore>();
        services.TryAddSingleton<IAuditLog, SqliteAuditLog>();
        return services;
    }
}
