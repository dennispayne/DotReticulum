using System.Security.Cryptography;
using static DotReticulum.Crypto.Tests.PythonVectors;

namespace DotReticulum.Crypto.Tests;

public class TokenTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(32)]
    public void MatchesPythonToken(int keyLength)
    {
        using var token = new Token(Sequence(0, keyLength));
        byte[] expected = keyLength == 64 ? Token256 : Token128;
        Assert.Equal(expected, token.Encrypt(Message, Iv));
        Assert.True(token.VerifyHmac(expected));
        Assert.Equal(Message, token.Decrypt(expected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(1024)]
    public void RoundTripsAndUsesFreshIvs(int length)
    {
        using var token = new Token(Private);
        byte[] data = new byte[length];
        byte[] encrypted = token.Encrypt(data);
        Assert.Equal(48 + (length / 16 + 1) * 16, encrypted.Length);
        Assert.Equal(data, token.Decrypt(encrypted));
        Assert.NotEqual(encrypted, token.Encrypt(data));
    }

    [Fact]
    public void RejectsTamperingEveryByteAndWrongKey()
    {
        using var token = new Token(Private);
        for (int i = 0; i < Token256.Length; i++)
        {
            byte[] altered = (byte[])Token256.Clone();
            altered[i] ^= 1;
            Assert.False(token.VerifyHmac(altered));
            Assert.Throws<CryptographicException>(() => token.Decrypt(altered));
        }
        using var wrong = new Token(Sequence(1, 64));
        Assert.Throws<CryptographicException>(() => wrong.Decrypt(Token256));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(79)]
    public void RejectsMalformedTokens(int length)
    {
        using var token = new Token(Private);
        Assert.False(token.VerifyHmac(new byte[length]));
        Assert.Throws<CryptographicException>(() => token.Decrypt(new byte[length]));
    }

    [Fact]
    public void AuthenticatesBeforeAttemptingPaddingAndRejectsAuthenticatedBadPadding()
    {
        using var token = new Token(Private);
        byte[] badPadding = token.Encrypt([], Iv);
        // CBC bit-flip in IV turns the final 0x10 padding byte into zero.
        badPadding[15] ^= 0x10;
        var error = Assert.Throws<CryptographicException>(() => token.Decrypt(badPadding));
        Assert.Equal("Malformed token or invalid authentication tag.", error.Message);
        HMACSHA256.HashData(Private.AsSpan(0, 32), badPadding.AsSpan(0, badPadding.Length - 32),
            badPadding.AsSpan(badPadding.Length - 32));
        Assert.True(token.VerifyHmac(badPadding));
        Assert.Throws<CryptographicException>(() => token.Decrypt(badPadding));
    }

    [Fact]
    public void RejectsInvalidKeysIvsAndDisposedUse()
    {
        foreach (int length in new[] { 0, 16, 31, 33, 63, 65 })
            Assert.Throws<ArgumentException>(() => new Token(new byte[length]));
        byte[] key = (byte[])Private.Clone();
        var token = new Token(key);
        Array.Clear(key);
        Assert.Equal(Token256, token.Encrypt(Message, Iv));
        Assert.Throws<ArgumentException>(() => token.Encrypt(Message, new byte[15]));
        token.Dispose();
        token.Dispose();
        Assert.Throws<ObjectDisposedException>(() => token.Encrypt(Message));
        Assert.Throws<ObjectDisposedException>(() => token.Decrypt(Token256));
        Assert.Throws<ObjectDisposedException>(() => token.VerifyHmac(Token256));
    }
}
