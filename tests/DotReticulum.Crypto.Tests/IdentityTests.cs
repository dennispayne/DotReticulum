using System.Security.Cryptography;
using DotReticulum.Core;
using static DotReticulum.Crypto.Tests.PythonVectors;

namespace DotReticulum.Crypto.Tests;

public class IdentityTests
{
    [Fact]
    public void MatchesPythonIdentityKeysHashSignatureAndEncryption()
    {
        using var identity = Identity.FromPrivateKey(Private);
        Assert.True(identity.HasPrivateKey);
        Assert.Equal(Private, identity.ExportPrivateKey());
        Assert.Equal(Public, identity.ExportPublicKey());
        Assert.Equal(Hash, identity.Hash);
        Assert.Equal(Convert.ToHexStringLower(Hash), identity.HexHash);
        Assert.Equal(Signature, identity.Sign(Message));
        Assert.True(identity.Verify(Message, Signature));
        Assert.Equal(Message, identity.Decrypt(Encrypted));

        byte[] shared = X25519.Agree(Ephemeral, Public.AsSpan(0, 32));
        Assert.Equal(Shared, shared);
        byte[] derived = KeyDerivation.HkdfSha256(shared, 64, Hash);
        Assert.Equal(Derived, derived);
        using var token = new Token(derived);
        byte[] expected = X25519.GetPublicKey(Ephemeral).Concat(token.Encrypt(Message, Iv)).ToArray();
        Assert.Equal(Encrypted, expected);
    }

    [Fact]
    public void PublicIdentityCanEncryptAndVerifyButCannotSignOrDecrypt()
    {
        using var recipient = Identity.FromPrivateKey(Private);
        using var sender = Identity.FromPublicKey(Public);
        Assert.False(sender.HasPrivateKey);
        Assert.Equal(Public, sender.ExportPublicKey());
        Assert.Equal(Hash, sender.Hash);
        Assert.True(sender.Verify(Message, Signature));
        Assert.Equal(Message, recipient.Decrypt(sender.Encrypt(Message)));
        Assert.Throws<InvalidOperationException>(() => sender.Sign(Message));
        Assert.Throws<InvalidOperationException>(() => sender.ExportPrivateKey());
        Assert.Throws<InvalidOperationException>(() => sender.Decrypt(Encrypted));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(1024)]
    public void GeneratedIdentityRoundTrips(int size)
    {
        using var identity = Identity.Generate();
        byte[] data = new byte[size];
        Assert.Equal(64, identity.ExportPublicKey().Length);
        byte[] encrypted = identity.Encrypt(data);
        Assert.Equal(32 + 48 + (size / 16 + 1) * 16, encrypted.Length);
        Assert.Equal(data, identity.Decrypt(encrypted));
        Assert.True(identity.Verify(data, identity.Sign(data)));
        Assert.NotEqual(encrypted, identity.Encrypt(data));
        using var wrong = Identity.Generate();
        Assert.Throws<CryptographicException>(() => wrong.Decrypt(encrypted));
        Assert.False(wrong.Verify(data, identity.Sign(data)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(65)]
    public void RejectsMalformedKeys(int length)
    {
        Assert.Throws<ArgumentException>(() => Identity.FromPrivateKey(new byte[length]));
        Assert.Throws<ArgumentException>(() => Identity.FromPublicKey(new byte[length]));
    }

    [Fact]
    public void RejectsInvalidPublicIdentityComponents()
    {
        byte[] key = (byte[])Public.Clone();
        Array.Clear(key, 0, 32);
        Assert.Throws<CryptographicException>(() => Identity.FromPublicKey(key));
        Public.CopyTo(key, 0);
        Array.Clear(key, 32, 32);
        key[32] = 1;
        Assert.Throws<CryptographicException>(() => Identity.FromPublicKey(key));
    }

    [Fact]
    public void RejectsTamperingAndMalformedCiphertexts()
    {
        using var identity = Identity.FromPrivateKey(Private);
        for (int i = 0; i < Encrypted.Length; i++)
        {
            byte[] altered = (byte[])Encrypted.Clone();
            altered[i] ^= 1;
            Assert.Throws<CryptographicException>(() => identity.Decrypt(altered));
        }
        foreach (int length in new[] { 0, 31, 32, 95, 97 })
            Assert.Throws<CryptographicException>(() => identity.Decrypt(new byte[length]));
        byte[] lowOrder = (byte[])Encrypted.Clone();
        Array.Clear(lowOrder, 0, 32);
        Assert.Throws<CryptographicException>(() => identity.Decrypt(lowOrder));
        Assert.False(identity.Verify([], Signature));
    }

    [Fact]
    public void OwnsItsKeysAndExportsIndependentCopies()
    {
        byte[] input = (byte[])Private.Clone();
        using var identity = Identity.FromPrivateKey(input);
        Array.Clear(input);
        Array.Clear(identity.ExportPrivateKey());
        Array.Clear(identity.ExportPublicKey());
        Array.Clear(identity.Hash);
        Assert.Equal(Private, identity.ExportPrivateKey());
        Assert.Equal(Public, identity.ExportPublicKey());
        Assert.Equal(Hash, identity.Hash);
        Assert.Equal(Signature, identity.Sign(Message));
    }

    [Fact]
    public void RejectsDisposedUse()
    {
        var identity = Identity.Generate();
        identity.Dispose();
        identity.Dispose();
        Assert.Throws<ObjectDisposedException>(() => identity.ExportPrivateKey());
        Assert.Throws<ObjectDisposedException>(() => identity.ExportPublicKey());
        Assert.Throws<ObjectDisposedException>(() => identity.Hash);
        Assert.Throws<ObjectDisposedException>(() => identity.HasPrivateKey);
        Assert.Throws<ObjectDisposedException>(() => identity.Sign(Message));
        Assert.Throws<ObjectDisposedException>(() => identity.Verify(Message, Signature));
        Assert.Throws<ObjectDisposedException>(() => identity.Encrypt(Message));
        Assert.Throws<ObjectDisposedException>(() => identity.Decrypt(Encrypted));
    }
}
