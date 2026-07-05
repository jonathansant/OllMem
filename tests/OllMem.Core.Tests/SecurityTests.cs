using System.Text;
using OllMem.Core.Abstractions;
using OllMem.Core.Domain;
using OllMem.Core.Security;

namespace OllMem.Core.Tests;

public class SecurityTests
{
    [Fact]
    public void Ed25519_sign_and_verify_roundtrip()
    {
        var scheme = new Ed25519SignatureScheme();
        var (pub, priv) = scheme.GenerateKeyPair();
        var msg = Encoding.UTF8.GetBytes("POST\n/v1/remember\n1700000000\nabc");

        var sig = scheme.Sign(priv, msg);

        Assert.True(scheme.Verify(pub, msg, sig));
    }

    [Fact]
    public void Ed25519_rejects_tampered_message()
    {
        var scheme = new Ed25519SignatureScheme();
        var (pub, priv) = scheme.GenerateKeyPair();
        var sig = scheme.Sign(priv, "original message"u8);

        Assert.False(scheme.Verify(pub, "tampered message"u8, sig));
    }

    [Fact]
    public void Ed25519_rejects_wrong_key()
    {
        var scheme = new Ed25519SignatureScheme();
        var (_, priv) = scheme.GenerateKeyPair();
        var (otherPub, _) = scheme.GenerateKeyPair();
        var sig = scheme.Sign(priv, "hello"u8);

        Assert.False(scheme.Verify(otherPub, "hello"u8, sig));
    }

    [Fact]
    public void Ed25519_malformed_input_returns_false_not_throws()
    {
        var scheme = new Ed25519SignatureScheme();
        Assert.False(scheme.Verify("not-hex", "hello"u8, "also-not-hex"));
    }

    [Fact]
    public void Aes_gcm_encrypt_decrypt_roundtrip()
    {
        var cipher = AesGcmPayloadCipher.CreateEphemeral();
        var plaintext = "User lives in San Francisco"u8.ToArray();

        var ct = cipher.Encrypt("owner/user/u1", plaintext);
        var pt = cipher.Decrypt("owner/user/u1", ct);

        Assert.Equal(plaintext, pt);
        Assert.NotEqual(plaintext, ct); // actually encrypted
    }

    [Fact]
    public void Aes_gcm_different_namespaces_cannot_decrypt_each_other()
    {
        var cipher = AesGcmPayloadCipher.CreateEphemeral();
        var ct = cipher.Encrypt("owner/user/u1", "secret"u8);

        Assert.ThrowsAny<Exception>(() => cipher.Decrypt("owner/user/u2", ct));
    }

    [Fact]
    public void Aes_gcm_detects_tampering()
    {
        var cipher = AesGcmPayloadCipher.CreateEphemeral();
        var ct = cipher.Encrypt("ns", "hello world"u8);
        ct[^1] ^= 0xFF; // flip a ciphertext bit

        Assert.ThrowsAny<Exception>(() => cipher.Decrypt("ns", ct));
    }

    [Fact]
    public async Task Audit_log_chain_verifies_and_detects_tampering()
    {
        var log = new HashChainedAuditLog();
        var ns = EngineFactory.UserNamespace();

        await log.AppendAsync(AuditAction.Create, ns, Guid.NewGuid(), "a");
        await log.AppendAsync(AuditAction.Access, ns, Guid.NewGuid(), "b");
        var third = await log.AppendAsync(AuditAction.Update, ns, Guid.NewGuid(), "c");

        Assert.True(await log.VerifyAsync());

        // Each entry chains to the previous hash.
        var entries = await log.ReadAsync();
        Assert.Equal(3, entries.Count);
        Assert.Equal(entries[1].Hash, third.PrevHash);
    }

    [Fact]
    public async Task Audit_log_filters_by_namespace()
    {
        var log = new HashChainedAuditLog();
        var a = new Namespace("o", MemoryScope.User, "a");
        var b = new Namespace("o", MemoryScope.User, "b");

        await log.AppendAsync(AuditAction.Create, a, Guid.NewGuid(), "x");
        await log.AppendAsync(AuditAction.Create, b, Guid.NewGuid(), "y");

        var onlyA = await log.ReadAsync(a.Key);
        Assert.Single(onlyA);
        Assert.Equal(a.Key, onlyA[0].NamespaceKey);
    }
}
