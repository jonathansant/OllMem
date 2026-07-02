using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aegis.Storage.Git;

/// <summary>
/// Owns the memory repository: a plain git working tree holding one JSON file
/// per memory node, content-addressed blob files, and per-writer audit chains.
/// Writes land on disk immediately (durability never depends on the network),
/// are committed after a debounce, and are synced — commit, pull with
/// automatic merge, push — with the configured remote so every machine or
/// agent pointed at the same remote shares the same memory.
///
/// All file and git access is serialised through <see cref="Gate"/> so a pull
/// can never rewrite files mid-read or mid-write.
/// </summary>
public sealed class GitStoreContext : IAsyncDisposable
{
    private readonly GitStoreOptions _options;
    private readonly ILogger? _logger;
    private readonly GitCli _git;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string[] _identityArgs;
    private readonly Timer? _commitTimer;
    private readonly Timer? _syncTimer;
    private int _syncing;
    private bool _disposed;

    /// <summary>Absolute path of the repository working tree.</summary>
    public string Root { get; }

    internal SemaphoreSlim Gate => _gate;
    internal string NodesDirectory { get; }
    internal string BlobsDirectory { get; }
    internal string AuditDirectory { get; }

    /// <summary>
    /// Stable id of this clone, used to name its private audit chain file.
    /// Kept under <c>.git/</c> (never synced) so two machines can't collide.
    /// </summary>
    internal string WriterId { get; }

    public GitStoreContext(GitStoreOptions options, ILogger<GitStoreContext>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(options.Path))
            throw new ArgumentException("GitStoreOptions.Path is required.", nameof(options));

        _options = options;
        _logger = logger;
        Root = Path.GetFullPath(options.Path);
        NodesDirectory = Path.Combine(Root, "nodes");
        BlobsDirectory = Path.Combine(Root, "blobs");
        AuditDirectory = Path.Combine(Root, "audit");
        _git = new GitCli(Root);
        _identityArgs =
        [
            "-c", $"user.name={options.AuthorName}",
            "-c", $"user.email={options.AuthorEmail}",
            "-c", "commit.gpgsign=false",
        ];

        EnsureRepositoryAsync().GetAwaiter().GetResult();
        WriterId = LoadOrCreateWriterId();

        if (options.CommitDebounceMs > 0)
            _commitTimer = new Timer(_ => FireAndForget(CommitOnTimerAsync), null, Timeout.Infinite, Timeout.Infinite);

        if (options.SyncIntervalSeconds > 0 && HasRemote)
        {
            var period = TimeSpan.FromSeconds(options.SyncIntervalSeconds);
            _syncTimer = new Timer(_ => FireAndForget(BackgroundSyncAsync), null, period, period);
        }
    }

    private bool HasRemote => !string.IsNullOrWhiteSpace(_options.RemoteUrl);

    /// <summary>
    /// Commit any pending memory changes and, when a remote is configured,
    /// pull (merging concurrent writes from other machines) and push. Returns
    /// false when the remote is unreachable; local memory keeps working and
    /// the next sync retries.
    /// </summary>
    public async ValueTask<bool> SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await CommitPendingLockedAsync("aegis: memory update", ct);
            return !HasRemote || await FetchMergePushLockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Schedules the debounced local commit after a write.</summary>
    internal void NotifyChanged() =>
        _commitTimer?.Change(_options.CommitDebounceMs, Timeout.Infinite);

    // ---- repository lifecycle ------------------------------------------------

    private async Task EnsureRepositoryAsync()
    {
        Directory.CreateDirectory(Root);

        if (!Directory.Exists(Path.Combine(Root, ".git")))
            await _git.RunCheckedAsync(default, "init", "--initial-branch", _options.Branch);

        if (HasRemote)
        {
            var remotes = await _git.RunCheckedAsync(default, "remote");
            var hasOrigin = remotes.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains("origin");
            await _git.RunCheckedAsync(default,
                "remote", hasOrigin ? "set-url" : "add", "origin", _options.RemoteUrl!);
        }

        Directory.CreateDirectory(NodesDirectory);
        Directory.CreateDirectory(BlobsDirectory);
        Directory.CreateDirectory(AuditDirectory);

        // Writes that never got committed (process killed before the debounce
        // fired) are still on disk; fold them into a commit before syncing.
        await CommitPendingLockedAsync("aegis: recover unsynced memory", default);

        if (HasRemote)
            await FetchMergePushLockedAsync(default);
    }

    private string LoadOrCreateWriterId()
    {
        var path = Path.Combine(Root, ".git", "aegis-writer-id");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0)
                return existing;
        }

        var machine = new string(Environment.MachineName.ToLowerInvariant()
            .Where(char.IsAsciiLetterOrDigit).ToArray());
        if (machine.Length == 0)
            machine = "writer";
        var id = $"{machine[..Math.Min(machine.Length, 24)]}-" +
                 Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        File.WriteAllText(path, id);
        return id;
    }

    // ---- commit / sync (callers must hold the gate) --------------------------

    private async Task<bool> CommitPendingLockedAsync(string message, CancellationToken ct)
    {
        var status = await _git.RunCheckedAsync(ct, "status", "--porcelain");
        if (status.StdOut.Length == 0)
            return false;

        await _git.RunCheckedAsync(ct, "add", "-A");
        await _git.RunCheckedAsync(ct, [.. _identityArgs, "commit", "-m", message]);
        return true;
    }

    private async Task<bool> FetchMergePushLockedAsync(CancellationToken ct)
    {
        // Fetch → merge → push, retrying when another machine pushes in
        // between (git rejects the push, we merge their commit and try again).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var fetch = await _git.RunAsync(ct, "fetch", "origin", _options.Branch);
            if (fetch.Success)
            {
                if (!await IntegrateFetchedLockedAsync(ct))
                    return false;
            }
            else if (!fetch.StdErr.Contains("couldn't find remote ref"))
            {
                // A brand-new/empty remote is fine (nothing to merge yet);
                // anything else means the remote is unreachable right now.
                _logger?.LogWarning(
                    "git fetch failed; memory stays local until the next sync: {Error}", fetch.StdErr.Trim());
                return false;
            }

            var head = await _git.RunAsync(ct, "rev-parse", "--verify", "HEAD");
            if (!head.Success)
                return true; // no local commits and an empty remote: nothing to push

            var push = await _git.RunAsync(ct, "push", "-u", "origin", _options.Branch);
            if (push.Success)
                return true;

            if (!push.StdErr.Contains("[rejected]"))
            {
                _logger?.LogWarning(
                    "git push failed; changes remain committed locally: {Error}", push.StdErr.Trim());
                return false;
            }
        }

        _logger?.LogWarning("git sync lost the push race repeatedly; will retry on the next sync.");
        return false;
    }

    private async Task<bool> IntegrateFetchedLockedAsync(CancellationToken ct)
    {
        var head = await _git.RunAsync(ct, "rev-parse", "--verify", "HEAD");
        if (!head.Success)
        {
            // Fresh local repo with no commits: adopt the remote history.
            await _git.RunCheckedAsync(ct, "checkout", "-B", _options.Branch, "FETCH_HEAD");
            return true;
        }

        // Unrelated histories are expected: each machine may have initialised
        // its local memory repo independently before first syncing.
        var merge = await _git.RunAsync(ct,
            [.. _identityArgs, "merge", "--no-edit", "--allow-unrelated-histories", "FETCH_HEAD"]);
        return merge.Success || await ResolveMergeConflictsLockedAsync(ct);
    }

    private async Task<bool> ResolveMergeConflictsLockedAsync(CancellationToken ct)
    {
        var conflicted = await _git.RunCheckedAsync(ct, "diff", "--name-only", "--diff-filter=U");
        foreach (var path in conflicted.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var ours = await ReadStageAsync(2, path, ct);
            var theirs = await ReadStageAsync(3, path, ct);
            var merged = TryMergeConflict(path, ours, theirs);
            if (merged is null)
            {
                _logger?.LogWarning("Unresolvable merge conflict in {Path}; keeping local memory only.", path);
                await _git.RunAsync(ct, "merge", "--abort");
                return false;
            }

            await File.WriteAllTextAsync(Path.Combine(Root, path), merged, ct);
        }

        await _git.RunCheckedAsync(ct, "add", "-A");
        await _git.RunCheckedAsync(ct, [.. _identityArgs, "commit", "--no-edit"]);
        return true;
    }

    private async Task<string?> ReadStageAsync(int stage, string path, CancellationToken ct)
    {
        var result = await _git.RunAsync(ct, "show", $":{stage}:{path}");
        return result.Success ? result.StdOut : null;
    }

    private static string? TryMergeConflict(string path, string? ours, string? theirs)
    {
        if (ours is null || theirs is null)
            return ours ?? theirs; // nothing is ever deleted, so one side must exist

        var relative = path.Replace('\\', '/');
        if (relative.StartsWith("nodes/", StringComparison.Ordinal) &&
            relative.EndsWith(".json", StringComparison.Ordinal))
        {
            try
            {
                var ourDoc = JsonSerializer.Deserialize<MemoryNodeDocument>(ours, GitJson.Document);
                var theirDoc = JsonSerializer.Deserialize<MemoryNodeDocument>(theirs, GitJson.Document);
                if (ourDoc is null || theirDoc is null)
                    return null;

                return JsonSerializer.Serialize(
                    MemoryNodeDocument.Merge(ourDoc, theirDoc), GitJson.Document) + "\n";
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (relative.StartsWith("audit/", StringComparison.Ordinal))
        {
            // Audit chains are per-writer append-only files, so a conflict can
            // only be two appends racing; the union preserves both.
            var ourLines = ours.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var theirLines = theirs.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var union = ourLines.Concat(theirLines.Except(ourLines));
            return string.Join('\n', union) + "\n";
        }

        // Blobs are content-addressed: the same ref is the same bytes, so
        // either side is correct.
        return theirs;
    }

    // ---- background timers ----------------------------------------------------

    private void FireAndForget(Func<Task> action) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Background git operation failed; will retry later.");
            }
        });

    private async Task CommitOnTimerAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await CommitPendingLockedAsync("aegis: memory update", default);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task BackgroundSyncAsync()
    {
        if (Interlocked.Exchange(ref _syncing, 1) == 1)
            return; // a slow sync is still running; skip this tick

        try
        {
            await SyncAsync();
        }
        finally
        {
            Volatile.Write(ref _syncing, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_commitTimer is not null)
            await _commitTimer.DisposeAsync();
        if (_syncTimer is not null)
            await _syncTimer.DisposeAsync();

        try
        {
            await SyncAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Final git sync on dispose failed; commits are preserved locally.");
        }

        _gate.Dispose();
    }
}
