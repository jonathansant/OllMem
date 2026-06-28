using System.Security.Cryptography;
using System.Text;
using Aegis.Core.Abstractions;

namespace Aegis.Core.Security;

/// <summary>
/// AES-256-GCM authenticated encryption for payloads at rest (PRD REQ-2.3).
/// A per-namespace key is derived from a single master key via HKDF-SHA256, so
/// each namespace is cryptographically compartmentalised and a leaked blob is
/// useless without both the master key and the namespace context.
///
/// Wire format: [12-byte nonce][16-byte tag][ciphertext].
/// </summary>
public sealed class AesGcmPayloadCipher : IPayloadCipher
{
    private const int NonceSize = 12; // 96-bit nonce recommended for GCM
    private const int TagSize = 16;   // 128-bit authentication tag
    private const int KeySize = 32;   // AES-256

    private readonly byte[] _masterKey;

    public AesGcmPayloadCipher(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length < KeySize)
            throw new ArgumentException($"Master key must be at least {KeySize} bytes.", nameof(masterKey));
        _masterKey = masterKey.ToArray();
    }

    /// <summary>Create a cipher with a randomly generated master key (dev / tests).</summary>
    public static AesGcmPayloadCipher CreateEphemeral()
    {
        var key = RandomNumberGenerator.GetBytes(KeySize);
        return new AesGcmPayloadCipher(key);
    }

    public byte[] Encrypt(string namespaceKey, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> key = stackalloc byte[KeySize];
        DeriveKey(namespaceKey, key);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(output.AsSpan(0));
        tag.CopyTo(output.AsSpan(NonceSize));
        ciphertext.CopyTo(output.AsSpan(NonceSize + TagSize));
        return output;
    }

    public byte[] Decrypt(string namespaceKey, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < NonceSize + TagSize)
            throw new ArgumentException("Ciphertext is too short to be valid.", nameof(ciphertext));

        Span<byte> key = stackalloc byte[KeySize];
        DeriveKey(namespaceKey, key);

        var nonce = ciphertext[..NonceSize];
        var tag = ciphertext.Slice(NonceSize, TagSize);
        var body = ciphertext[(NonceSize + TagSize)..];

        var plaintext = new byte[body.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, body, tag, plaintext);
        return plaintext;
    }

    private void DeriveKey(string namespaceKey, Span<byte> destination)
    {
        // HKDF binds the derived key to the namespace, enforcing compartmentalisation.
        var info = Encoding.UTF8.GetBytes($"aegis-v2-ns:{namespaceKey}");
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _masterKey, destination, salt: null, info);
    }
}
