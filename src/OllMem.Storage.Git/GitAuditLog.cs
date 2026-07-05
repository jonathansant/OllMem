using System.Text.Json;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Security;

namespace OllMem.Storage.Git;

/// <summary>
/// Persistent, tamper-evident audit log (PRD REQ-3.3) designed for multiple
/// machines appending concurrently: every clone owns exactly one append-only
/// JSONL file (<c>audit/&lt;writer-id&gt;.jsonl</c>) carrying its own hash
/// chain. Appends therefore never merge-conflict across machines, and
/// <see cref="VerifyAsync"/> verifies every writer's chain end to end using
/// the shared <see cref="AuditChain"/> hashing.
/// </summary>
public sealed class GitAuditLog : IAuditLog
{
    private readonly GitStoreContext _ctx;
    private (long Sequence, string Hash)? _tip;

    public GitAuditLog(GitStoreContext ctx) => _ctx = ctx;

    private string OwnChainPath => Path.Combine(_ctx.AuditDirectory, $"{_ctx.WriterId}.jsonl");

    public async ValueTask<AuditEntry> AppendAsync(
        AuditAction action, Namespace ns, Guid nodeId, string detail, CancellationToken ct = default)
    {
        await _ctx.Gate.WaitAsync(ct);
        try
        {
            _tip ??= ReadTip(OwnChainPath);
            var sequence = _tip is { } tip ? tip.Sequence + 1 : 0;
            var prevHash = _tip?.Hash ?? AuditChain.GenesisHash;
            var timestamp = DateTimeOffset.UtcNow;
            var hash = AuditChain.ComputeHash(sequence, timestamp, action, ns.Key, nodeId, detail, prevHash);
            var entry = new AuditEntry(sequence, timestamp, action, ns.Key, nodeId, detail, prevHash, hash);

            Directory.CreateDirectory(_ctx.AuditDirectory);
            await File.AppendAllTextAsync(
                OwnChainPath, JsonSerializer.Serialize(entry, GitJson.Line) + "\n", ct);
            _tip = (sequence, hash);
            _ctx.NotifyChanged();
            return entry;
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(
        string? namespaceKey = null, CancellationToken ct = default)
    {
        var key = namespaceKey?.ToLowerInvariant();

        await _ctx.Gate.WaitAsync(ct);
        try
        {
            // Sequences are per writer, so the merged view across machines is
            // ordered by time (hash as a deterministic tie-breaker).
            return EnumerateChains()
                .SelectMany(chain => chain.Entries)
                .Where(e => key is null || e.NamespaceKey == key)
                .OrderBy(e => e.Timestamp)
                .ThenBy(e => e.Hash, StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    public async ValueTask<bool> VerifyAsync(CancellationToken ct = default)
    {
        await _ctx.Gate.WaitAsync(ct);
        try
        {
            foreach (var (_, entries) in EnumerateChains())
            {
                var prevHash = AuditChain.GenesisHash;
                foreach (var e in entries)
                {
                    if (e.PrevHash != prevHash)
                        return false;

                    var expected = AuditChain.ComputeHash(
                        e.Sequence, e.Timestamp, e.Action, e.NamespaceKey, e.NodeId, e.Detail, e.PrevHash);
                    if (expected != e.Hash)
                        return false;

                    prevHash = e.Hash;
                }
            }

            return true;
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    // ---- helpers ------------------------------------------------------------

    private IEnumerable<(string Writer, List<AuditEntry> Entries)> EnumerateChains()
    {
        if (!Directory.Exists(_ctx.AuditDirectory))
            yield break;

        foreach (var file in Directory.EnumerateFiles(_ctx.AuditDirectory, "*.jsonl"))
            yield return (Path.GetFileNameWithoutExtension(file), ReadChain(file));
    }

    private static List<AuditEntry> ReadChain(string file)
    {
        var entries = new List<AuditEntry>();
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                if (JsonSerializer.Deserialize<AuditEntry>(line, GitJson.Line) is { } entry)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                // A torn line is invisible to reads; verification of the
                // writer's chain will still expose the gap it leaves.
            }
        }

        return entries;
    }

    private static (long Sequence, string Hash)? ReadTip(string file) =>
        File.Exists(file) && ReadChain(file) is { Count: > 0 } entries
            ? (entries[^1].Sequence, entries[^1].Hash)
            : null;
}
