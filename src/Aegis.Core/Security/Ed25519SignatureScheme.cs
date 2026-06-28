using Aegis.Core.Abstractions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Crypto.Signers;

namespace Aegis.Core.Security;

/// <summary>
/// Ed25519 request signing (PRD REQ-1.3) implemented with the open-source
/// BouncyCastle library. Keys and signatures are exchanged as hex strings so
/// they travel easily in HTTP headers.
/// </summary>
public sealed class Ed25519SignatureScheme : ISignatureScheme
{
    public (string PublicKeyHex, string PrivateKeyHex) GenerateKeyPair()
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();

        var priv = (Ed25519PrivateKeyParameters)pair.Private;
        var pub = (Ed25519PublicKeyParameters)pair.Public;

        return (Convert.ToHexString(pub.GetEncoded()).ToLowerInvariant(),
                Convert.ToHexString(priv.GetEncoded()).ToLowerInvariant());
    }

    public string Sign(string privateKeyHex, ReadOnlySpan<byte> message)
    {
        var priv = new Ed25519PrivateKeyParameters(Convert.FromHexString(privateKeyHex), 0);
        ISigner signer = new Ed25519Signer();
        signer.Init(forSigning: true, priv);

        var bytes = message.ToArray();
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return Convert.ToHexString(signer.GenerateSignature()).ToLowerInvariant();
    }

    public bool Verify(string publicKeyHex, ReadOnlySpan<byte> message, string signatureHex)
    {
        try
        {
            var pub = new Ed25519PublicKeyParameters(Convert.FromHexString(publicKeyHex), 0);
            ISigner verifier = new Ed25519Signer();
            verifier.Init(forSigning: false, pub);

            var bytes = message.ToArray();
            verifier.BlockUpdate(bytes, 0, bytes.Length);
            return verifier.VerifySignature(Convert.FromHexString(signatureHex));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptoException)
        {
            // Malformed key/signature is treated as a failed verification, not a crash.
            return false;
        }
    }
}
