using OllMem.Core.Retrieval;

namespace OllMem.Core;

/// <summary>
/// Configuration for the OllMem memory layer.
/// </summary>
public sealed class OllMemOptions
{
    /// <summary>Dimensionality of the local embedding model.</summary>
    public int EmbeddingDimensions { get; set; } = 256;

    /// <summary>
    /// Base64-encoded 32-byte master key used to derive per-namespace
    /// encryption keys (PRD REQ-2.3). If null, an ephemeral key is generated at
    /// startup (suitable for dev/tests; data won't survive restarts).
    /// </summary>
    public string? MasterKeyBase64 { get; set; }

    /// <summary>Soft-decay curve parameters (PRD REQ-4.1).</summary>
    public SoftDecayOptions Decay { get; set; } = new();
}
