using Aegis.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aegis.Storage.Git;

/// <summary>
/// Registers git-backed persistence for the Aegis memory layer: memory is
/// stored as plain files in a git repository and synced with a remote, so any
/// machine, device or agent pointed at the same remote shares one memory.
/// Call this <b>before</b> <c>AddAegisMemory()</c>: the core registrations use
/// <c>TryAdd*</c>, so whichever store is registered first wins. The hybrid
/// store instance backs both <see cref="IVectorStore"/> and
/// <see cref="IGraphStore"/>.
/// </summary>
public static class AegisGitServiceCollectionExtensions
{
    /// <summary>
    /// Persist to a git repository at <paramref name="path"/>, optionally
    /// syncing with <paramref name="remoteUrl"/> (SSH, HTTPS, or a local path;
    /// authentication reuses the machine's existing git credentials).
    /// </summary>
    public static IServiceCollection AddAegisGitStorage(
        this IServiceCollection services, string path, string? remoteUrl = null) =>
        services.AddAegisGitStorage(o =>
        {
            o.Path = path;
            o.RemoteUrl = remoteUrl;
        });

    public static IServiceCollection AddAegisGitStorage(
        this IServiceCollection services, Action<GitStoreOptions> configure)
    {
        var options = new GitStoreOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.Path))
            throw new ArgumentException("GitStoreOptions.Path is required.", nameof(configure));

        services.TryAddSingleton(options);
        services.TryAddSingleton(sp => new GitStoreContext(
            sp.GetRequiredService<GitStoreOptions>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<GitStoreContext>()));
        services.TryAddSingleton<GitHybridStore>();
        services.TryAddSingleton<IVectorStore>(sp => sp.GetRequiredService<GitHybridStore>());
        services.TryAddSingleton<IGraphStore>(sp => sp.GetRequiredService<GitHybridStore>());
        services.TryAddSingleton<IBlobStore, GitBlobStore>();
        services.TryAddSingleton<IAuditLog, GitAuditLog>();
        return services;
    }
}
