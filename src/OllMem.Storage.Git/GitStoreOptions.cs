namespace OllMem.Storage.Git;

/// <summary>
/// Configuration for the git-backed persistence store. Memory is persisted as
/// plain files inside a dedicated git repository; pointing several machines,
/// devices or agents at the same <see cref="RemoteUrl"/> gives them one shared,
/// versioned memory.
/// </summary>
public sealed class GitStoreOptions
{
    /// <summary>Local working directory of the memory repository (required).</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Git remote to sync with (any URL <c>git</c> itself accepts: SSH, HTTPS,
    /// or a local path). Authentication reuses whatever the machine's git is
    /// already configured with — SSH keys, credential helpers, etc. When null
    /// the store still versions memory locally but never talks to a network.
    /// </summary>
    public string? RemoteUrl { get; set; }

    /// <summary>Branch that carries the memory (default <c>main</c>).</summary>
    public string Branch { get; set; } = "main";

    /// <summary>Committer identity for memory commits.</summary>
    public string AuthorName { get; set; } = "OllMem Memory";

    /// <summary>Committer identity for memory commits.</summary>
    public string AuthorEmail { get; set; } = "ollmem@localhost";

    /// <summary>
    /// Quiet period after the last write before changes are committed locally,
    /// so one logical operation (which touches several files) lands as a single
    /// commit. <c>0</c> disables the debounced commit — changes are then only
    /// committed by <see cref="GitStoreContext.SyncAsync"/>, at startup
    /// recovery, or on dispose.
    /// </summary>
    public int CommitDebounceMs { get; set; } = 2000;

    /// <summary>
    /// How often to sync (commit + pull + push) with the remote in the
    /// background, so memories written by other machines become visible to
    /// long-running processes. <c>0</c> disables background sync; syncing then
    /// happens at startup, on <see cref="GitStoreContext.SyncAsync"/>, and on
    /// dispose.
    /// </summary>
    public int SyncIntervalSeconds { get; set; } = 60;
}
