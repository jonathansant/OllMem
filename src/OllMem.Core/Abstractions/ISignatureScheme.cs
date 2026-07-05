namespace OllMem.Core.Abstractions;

/// <summary>
/// Ed25519 signing primitives used to assert data ownership and
/// compartmentalise namespaces (PRD REQ-1.3). All write requests must carry a
/// valid signature over a canonical request string.
/// </summary>
public interface ISignatureScheme
{
    /// <summary>Generate a fresh keypair; both values are returned as hex.</summary>
    (string PublicKeyHex, string PrivateKeyHex) GenerateKeyPair();

    /// <summary>Sign <paramref name="message"/> with a hex private key; returns hex signature.</summary>
    string Sign(string privateKeyHex, ReadOnlySpan<byte> message);

    /// <summary>Verify a hex signature over <paramref name="message"/> for a hex public key.</summary>
    bool Verify(string publicKeyHex, ReadOnlySpan<byte> message, string signatureHex);
}
