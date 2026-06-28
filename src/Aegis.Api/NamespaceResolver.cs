using System.Diagnostics.CodeAnalysis;
using Aegis.Core.Domain;

namespace Aegis.Api;

/// <summary>
/// Builds a <see cref="Namespace"/> from a request, enforcing that — when
/// signature auth is active — the namespace owner equals the verified signing
/// key. This is what compartmentalises namespaces per PRD REQ-1.3.
/// </summary>
public static class NamespaceResolver
{
    public static bool TryResolve(
        ScopedRequest req,
        HttpContext ctx,
        out Namespace ns,
        [NotNullWhen(false)] out string? error)
    {
        ns = default;
        error = null;

        var verifiedOwner = ctx.Items.TryGetValue(SignatureAuthMiddleware.OwnerItemKey, out var v)
            ? v as string
            : null;

        // When a signature was verified the owner is authoritative; a mismatching
        // body owner is rejected to prevent cross-namespace writes.
        var owner = verifiedOwner ?? req.Owner;
        if (string.IsNullOrWhiteSpace(owner))
        {
            error = "owner is required";
            return false;
        }

        if (verifiedOwner is not null &&
            !string.IsNullOrWhiteSpace(req.Owner) &&
            !string.Equals(req.Owner, verifiedOwner, StringComparison.OrdinalIgnoreCase))
        {
            error = "owner does not match the signing key";
            return false;
        }

        if (!TryParseScope(req.Scope, out var scope))
        {
            error = "scope must be one of: user, agent, session";
            return false;
        }

        var scopeId = string.IsNullOrWhiteSpace(req.ScopeId) ? "default" : req.ScopeId;
        ns = new Namespace(owner, scope, scopeId);
        return true;
    }

    public static bool TryParseScope(string? value, out MemoryScope scope)
    {
        scope = MemoryScope.User;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return Enum.TryParse(value, ignoreCase: true, out scope) && Enum.IsDefined(scope);
    }
}
