using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Core.Tests;

// Fixtures/crypto-vectors.json (linked from the Crypto tests) was produced by executing upstream
// Reticulum (markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b, RNS.Identity) through
// tests/DotReticulum.Crypto.Tests/GenerateCryptoVectors.py. Ciphertexts are upstream Identity.encrypt output
// (with and without a ratchet); signatures, public keys and hashes are upstream values for fixed private keys.
// The reverse direction (upstream decrypts our ciphertext) is checked by tests/Interop/VerifyDotNetOutput.py.
public class IdentityTests
{
    private static List<JsonElement> Vectors() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "crypto-vectors.json")))
            .RootElement.GetProperty("identities").EnumerateArray().ToList();

    private static byte[] B(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    [Fact]
    public void MatchesUpstreamKeysHashesAndSignatures()
    {
        var vectors = Vectors();
        Assert.Equal(3, vectors.Count);
        foreach (var v in vectors)
        {
            using var id = Identity.FromPrivateKey(B(v, "private_key"));
            Assert.Equal(B(v, "public_key"), id.ExportPublicKey());
            Assert.Equal(B(v, "hash"), id.Hash);
            Assert.Equal(B(v, "private_key"), id.ExportPrivateKey());
            // Ed25519 is deterministic, so our signature equals upstream's byte for byte.
            Assert.Equal(B(v, "signature"), id.Sign(B(v, "sign_message")));

            using var pub = Identity.FromPublicKey(B(v, "public_key"));
            Assert.Equal(id.Hash, pub.Hash);
            Assert.True(pub.Verify(B(v, "signature"), B(v, "sign_message")));
        }
    }

    [Fact]
    public void DecryptsUpstreamCiphertexts()
    {
        foreach (var v in Vectors())
        {
            using var id = Identity.FromPrivateKey(B(v, "private_key"));
            Assert.Equal(B(v, "message"), id.Decrypt(B(v, "ciphertext")));

            var ratchetCt = B(v, "ratchet_ciphertext");
            var ratchet = B(v, "ratchet_private");
            Assert.Equal(B(v, "message"), id.Decrypt(ratchetCt, [ratchet]));
            Assert.Equal(B(v, "message"), id.Decrypt(ratchetCt, [new byte[32], ratchet]));
            // Without the ratchet key (or when enforced against the wrong one) it must not decrypt.
            Assert.Null(id.Decrypt(ratchetCt));
            Assert.Null(id.Decrypt(ratchetCt, [new byte[32]], enforceRatchets: true));
            Assert.Null(id.Decrypt(B(v, "ciphertext"), [ratchet], enforceRatchets: true));
            // Non-enforced ratchet list falls back to the identity key.
            Assert.Equal(B(v, "message"), id.Decrypt(B(v, "ciphertext"), [ratchet]));
        }
    }

    [Fact]
    public void RatchetEncryptRoundTrips()
    {
        using var id = Identity.Generate();
        var ratchetPrivate = RandomNumberGenerator.GetBytes(32);
        var ratchetPublic = DotReticulum.Crypto.X25519.GetPublicKey(ratchetPrivate);
        var ct = id.Encrypt([1, 2, 3], ratchetPublic);
        Assert.Equal([1, 2, 3], id.Decrypt(ct, [ratchetPrivate], enforceRatchets: true));
        Assert.Null(id.Decrypt(ct));
    }

    [Fact]
    public void GenerateEncryptDecryptSignRoundTrip()
    {
        using var id = Identity.Generate();
        Assert.True(id.HasPrivateKey);
        Assert.Equal(64, id.ExportPublicKey().Length);
        Assert.Equal(16, id.Hash.Length);
        Assert.Equal(SHA256.HashData(id.ExportPublicKey())[..16], id.Hash);

        using var pub = Identity.FromPublicKey(id.ExportPublicKey());
        var ct = pub.Encrypt("hello"u8);
        Assert.Equal(32 + 48 + 16, ct.Length);
        Assert.Equal("hello"u8.ToArray(), id.Decrypt(ct));

        var sig = id.Sign("m"u8);
        Assert.True(pub.Verify(sig, "m"u8));
        Assert.False(pub.Verify(sig, "n"u8));
        Assert.False(pub.Verify(sig[..63], "m"u8));
    }

    [Fact]
    public void TamperedOrTruncatedCiphertextReturnsNull()
    {
        using var id = Identity.Generate();
        var ct = id.Encrypt("payload"u8);
        for (var i = 0; i < ct.Length; i++)
        {
            var bad = (byte[])ct.Clone();
            bad[i] ^= 1;
            Assert.Null(id.Decrypt(bad));
        }
        for (var len = 0; len < ct.Length; len++) Assert.Null(id.Decrypt(ct.AsSpan(0, len)));
        using var other = Identity.Generate();
        Assert.Null(other.Decrypt(ct));
    }

    [Fact]
    public void LowOrderEphemeralKeyIsRejected()
    {
        using var id = Identity.Generate();
        var ct = id.Encrypt("payload"u8);
        ct.AsSpan(0, 32).Clear();
        Assert.Null(id.Decrypt(ct));
    }

    [Fact]
    public void MalformedKeysAreRejected()
    {
        using var id = Identity.Generate();
        var pub = id.ExportPublicKey();
        Assert.Throws<ArgumentException>(() => Identity.FromPrivateKey(new byte[63]));
        Assert.Throws<ArgumentException>(() => Identity.FromPrivateKey(new byte[65]));
        Assert.Throws<ArgumentException>(() => Identity.FromPublicKey(pub[..63]));
        Assert.Throws<ArgumentException>(() => Identity.FromPublicKey([.. pub, 0]));

        var lowOrderX = (byte[])pub.Clone();
        lowOrderX.AsSpan(0, 32).Clear();
        Assert.Throws<ArgumentException>(() => Identity.FromPublicKey(lowOrderX));

        var badEd = (byte[])pub.Clone();
        badEd.AsSpan(32, 32).Clear();
        badEd[32] = 1; // identity point
        Assert.Throws<ArgumentException>(() => Identity.FromPublicKey(badEd));
    }

    [Fact]
    public void PublicOnlyIdentityCannotSignDecryptOrExportPrivate()
    {
        using var id = Identity.Generate();
        using var pub = Identity.FromPublicKey(id.ExportPublicKey());
        Assert.False(pub.HasPrivateKey);
        Assert.Throws<InvalidOperationException>(() => pub.Sign([1]));
        Assert.Throws<InvalidOperationException>(() => pub.Decrypt(new byte[100]));
        Assert.Throws<InvalidOperationException>(() => pub.ExportPrivateKey());
    }

    [Fact]
    public void DisposeZeroesAndBlocksUse()
    {
        var id = Identity.Generate();
        var prv = id.ExportPrivateKey();
        Assert.Equal(64, prv.Length);
        id.Dispose();
        id.Dispose();
        Assert.False(id.HasPrivateKey);
        Assert.Throws<ObjectDisposedException>(() => id.Sign([1]));
        Assert.Throws<ObjectDisposedException>(() => id.ExportPrivateKey());
    }
}
