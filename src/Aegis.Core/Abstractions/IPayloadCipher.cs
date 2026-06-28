namespace Aegis.Core.Abstractions;

/// <summary>
/// Authenticated encryption applied to every payload before it reaches the
/// permanent storage layer (PRD REQ-2.3). Keys are derived per-namespace so a
/// leaked blob cannot be read without the namespace key.
/// </summary>
public interface IPayloadCipher
{
    /// <summary>Encrypt plaintext for a given namespace key context.</summary>
    byte[] Encrypt(string namespaceKey, ReadOnlySpan<byte> plaintext);

    /// <summary>Decrypt a payload previously produced by <see cref="Encrypt"/>.</summary>
    byte[] Decrypt(string namespaceKey, ReadOnlySpan<byte> ciphertext);
}
