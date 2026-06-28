using System.Security.Cryptography;
using System.Text;
using Aegis.Core.Abstractions;

namespace Aegis.Core.Security;

/// <summary>
/// Canonical hashing for the audit chain (PRD REQ-3.3), shared by every
/// <see cref="IAuditLog"/> implementation so an in-memory log and a persistent
/// log produce identical, mutually-verifiable hashes.
/// </summary>
public static class AuditChain
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeHash(
        long sequence,
        DateTimeOffset timestamp,
        AuditAction action,
        string namespaceKey,
        Guid nodeId,
        string detail,
        string prevHash)
    {
        var canonical = string.Join('\n',
            sequence.ToString(),
            timestamp.UtcTicks.ToString(),
            action.ToString(),
            namespaceKey,
            nodeId.ToString(),
            detail,
            prevHash);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
