using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Crypto;

namespace DotReticulum.Crypto.Tests;

public class TokenTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void RoundTripsAllLengthsAndHasExpectedSize(int keySize)
    {
        using var token = new Token(RandomNumberGenerator.GetBytes(keySize));
        for (var n = 0; n <= 70; n++)
        {
            var pt = RandomNumberGenerator.GetBytes(n);
            var t = token.Encrypt(pt);
            Assert.Equal(Token.Overhead + (n / 16 + 1) * 16, t.Length);
            Assert.Equal(pt, token.Decrypt(t));
        }
    }

    [Fact]
    public void EncryptIsRandomised()
    {
        using var token = new Token(Token.GenerateKey());
        Assert.NotEqual(token.Encrypt([1, 2, 3]), token.Encrypt([1, 2, 3]));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void EveryTamperedByteIsRejected(int keySize)
    {
        using var token = new Token(RandomNumberGenerator.GetBytes(keySize));
        var t = token.Encrypt(RandomNumberGenerator.GetBytes(20));
        for (var i = 0; i < t.Length; i++)
        {
            var bad = (byte[])t.Clone();
            bad[i] ^= 0x01;
            Assert.False(token.VerifyHmac(bad), $"byte {i} (IV 0-15, ciphertext, MAC last 32)");
            Assert.Throws<CryptographicException>(() => token.Decrypt(bad));
            Assert.False(token.TryDecrypt(bad, out _));
        }
    }

    [Fact]
    public void WrongKeyAndTruncationAreRejected()
    {
        using var a = new Token(Token.GenerateKey());
        using var b = new Token(Token.GenerateKey());
        var t = a.Encrypt([9, 9, 9]);
        Assert.Throws<CryptographicException>(() => b.Decrypt(t));
        for (var len = 0; len < t.Length; len++)
        {
            Assert.Throws<CryptographicException>(() => a.Decrypt(t.AsSpan(0, len)));
        }
        Assert.Throws<CryptographicException>(() => a.Decrypt([.. t, 0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(48)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(128)]
    public void InvalidKeyLengthsAreRejected(int length) =>
        Assert.Throws<ArgumentException>(() => new Token(new byte[length]));

    [Fact]
    public void ValidHmacWithBadPaddingOrBlockAlignmentIsRejected()
    {
        var key = RandomNumberGenerator.GetBytes(64);
        using var token = new Token(key);

        byte[] Seal(byte[] ciphertext)
        {
            var iv = RandomNumberGenerator.GetBytes(16);
            var body = iv.Concat(ciphertext).ToArray();
            return [.. body, .. HMACSHA256.HashData(key.AsSpan(0, 32), body)];
        }

        using var aes = Aes.Create();
        aes.Key = key[32..];
        // A block that decrypts to 16 zero bytes: padding value 0 is invalid PKCS7.
        var ivZero = new byte[16];
        var raw = aes.EncryptCbc(new byte[16], ivZero, PaddingMode.None);
        byte[] sealedBad = [.. ivZero, .. raw];
        byte[] tokenBadPad = [.. sealedBad, .. HMACSHA256.HashData(key.AsSpan(0, 32), sealedBad)];
        Assert.True(token.VerifyHmac(tokenBadPad));
        Assert.Throws<CryptographicException>(() => token.Decrypt(tokenBadPad));

        Assert.Throws<CryptographicException>(() => token.Decrypt(Seal(new byte[17])));
        Assert.Throws<CryptographicException>(() => token.Decrypt(Seal([])));
    }

    [Fact]
    public void DisposedTokenCannotBeUsed()
    {
        var token = new Token(Token.GenerateKey());
        token.Dispose();
        token.Dispose();
        Assert.Throws<ObjectDisposedException>(() => token.Encrypt([1]));
        Assert.Throws<ObjectDisposedException>(() => token.Decrypt(new byte[64]));
    }
}

// Reference vectors in Fixtures/crypto-vectors.json were produced by EXECUTING upstream Reticulum
// (markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b, RNS.Cryptography.Token / hkdf)
// via GenerateCryptoVectors.py. Tokens there are upstream-produced; we must open them.
// The reverse direction (upstream opens our tokens) is checked by tests/Interop/VerifyDotNetOutput.py.
public class PythonReferenceCryptoTests
{
    private static JsonElement Load() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "crypto-vectors.json"))).RootElement;

    [Fact]
    public void FixtureIsFromPinnedUpstream() =>
        Assert.Equal("e40191b3d193b46b7f2d8a44424a594cd758839b", Load().GetProperty("upstream_sha").GetString());

    [Fact]
    public void OpensUpstreamProducedTokens()
    {
        var vectors = Load().GetProperty("tokens").EnumerateArray().ToList();
        Assert.Equal(18, vectors.Count);
        foreach (var v in vectors)
        {
            using var token = new Token(Convert.FromHexString(v.GetProperty("key").GetString()!));
            var plaintext = Convert.FromHexString(v.GetProperty("plaintext").GetString()!);
            Assert.Equal(plaintext, token.Decrypt(Convert.FromHexString(v.GetProperty("token").GetString()!)));
        }
    }

    [Fact]
    public void HkdfMatchesUpstream()
    {
        var vectors = Load().GetProperty("hkdf").EnumerateArray().ToList();
        Assert.NotEmpty(vectors);
        foreach (var v in vectors)
        {
            var okm = KeyDerivation.HkdfSha256(
                Convert.FromHexString(v.GetProperty("ikm").GetString()!),
                v.GetProperty("length").GetInt32(),
                Convert.FromHexString(v.GetProperty("salt").GetString()!),
                Convert.FromHexString(v.GetProperty("info").GetString()!));
            Assert.Equal(v.GetProperty("okm").GetString(), Convert.ToHexStringLower(okm));
        }
    }
}
