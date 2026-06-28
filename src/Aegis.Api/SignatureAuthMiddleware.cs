using System.Security.Cryptography;
using System.Text;
using Aegis.Core.Abstractions;

namespace Aegis.Api;

/// <summary>
/// Verifies Ed25519 request signatures for protected routes (PRD REQ-1.3).
///
/// Clients sign the canonical string:
/// <code>{METHOD}\n{PATH}\n{UNIX_TS}\n{SHA256_HEX(body)}</code>
/// and send three headers:
/// <list type="bullet">
///   <item><c>X-Aegis-PublicKey</c> — hex Ed25519 public key (becomes the owner)</item>
///   <item><c>X-Aegis-Timestamp</c> — unix seconds, checked against clock skew</item>
///   <item><c>X-Aegis-Signature</c> — hex signature over the canonical string</item>
/// </list>
/// On success the verified public key is stashed in <c>HttpContext.Items</c> and
/// becomes the authoritative namespace owner.
/// </summary>
public sealed class SignatureAuthMiddleware(RequestDelegate next, AegisApiOptions options, ISignatureScheme scheme)
{
    public const string OwnerItemKey = "aegis.owner";

    private const string PublicKeyHeader = "X-Aegis-PublicKey";
    private const string TimestampHeader = "X-Aegis-Timestamp";
    private const string SignatureHeader = "X-Aegis-Signature";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.RequireSignature || !context.Request.Path.StartsWithSegments("/v1"))
        {
            await next(context);
            return;
        }

        var publicKey = context.Request.Headers[PublicKeyHeader].ToString();
        var timestamp = context.Request.Headers[TimestampHeader].ToString();
        var signature = context.Request.Headers[SignatureHeader].ToString();

        if (string.IsNullOrWhiteSpace(publicKey) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            string.IsNullOrWhiteSpace(signature))
        {
            await Reject(context, "Missing authentication headers.");
            return;
        }

        if (!long.TryParse(timestamp, out var unix) || !WithinSkew(unix))
        {
            await Reject(context, "Request timestamp is missing, malformed, or outside the allowed window.");
            return;
        }

        // Buffer the body so we can hash it and still let the endpoint read it.
        context.Request.EnableBuffering();
        var bodyHash = await HashBodyAsync(context.Request);
        context.Request.Body.Position = 0;

        var canonical = $"{context.Request.Method}\n{context.Request.Path}\n{unix}\n{bodyHash}";
        var message = Encoding.UTF8.GetBytes(canonical);

        if (!scheme.Verify(publicKey, message, signature))
        {
            await Reject(context, "Signature verification failed.");
            return;
        }

        context.Items[OwnerItemKey] = publicKey.ToLowerInvariant();
        await next(context);
    }

    private bool WithinSkew(long unix)
    {
        var delta = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix);
        return delta <= options.MaxClockSkewSeconds;
    }

    private static async Task<string> HashBodyAsync(HttpRequest request)
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        var hash = SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task Reject(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized", detail });
    }
}
