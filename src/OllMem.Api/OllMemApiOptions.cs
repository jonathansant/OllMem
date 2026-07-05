namespace OllMem.Api;

/// <summary>API-layer settings, bound from the "OllMem" configuration section.</summary>
public sealed class OllMemApiOptions
{
    /// <summary>
    /// When true, every write/read under /v1 must carry a valid Ed25519
    /// signature (PRD REQ-1.3) and the namespace owner is forced to the signing
    /// key. Defaults to false for friction-free local development.
    /// </summary>
    public bool RequireSignature { get; set; }

    /// <summary>Maximum accepted difference between request timestamp and server clock.</summary>
    public int MaxClockSkewSeconds { get; set; } = 300;
}
