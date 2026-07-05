using System.Diagnostics;
using System.Text;

namespace Aegis.Storage.Git;

internal sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Minimal async wrapper around the <c>git</c> CLI. Shelling out (rather than
/// binding a native library) keeps the store dependency-free and means remote
/// access uses whatever credentials the machine's git already has — SSH keys,
/// credential helpers, <c>gh auth</c> — which is exactly what a store shared
/// across personal machines needs.
/// </summary>
internal sealed class GitCli
{
    private readonly string _workingDirectory;

    public GitCli(string workingDirectory) => _workingDirectory = workingDirectory;

    public async Task<GitResult> RunAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        // A store must never hang a headless agent on an interactive
        // credential prompt; fail the operation and keep memory local instead.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'git'. Is git installed and on PATH?");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new GitResult(process.ExitCode, await stdout, await stderr);
    }

    public async Task<GitResult> RunCheckedAsync(CancellationToken ct, params string[] args)
    {
        var result = await RunAsync(ct, args);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed with exit code {result.ExitCode}: {result.StdErr.Trim()}");
        }

        return result;
    }
}
