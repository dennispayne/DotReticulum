using System.Security.Cryptography;

namespace DotReticulum.Crypto;

/// <summary>Reticulum's binary Fernet variant: IV || AES-CBC/PKCS7 || HMAC-SHA256.
/// No version, timestamp, or base64. Instances are not thread-safe.</summary>
public sealed class Token : IDisposable
{
    public const int Overhead = 48;
    public const int MinimumLength = 64;
    private readonly byte[] signingKey;
    private readonly Aes aes;
    private bool disposed;

    public Token(ReadOnlySpan<byte> key)
    {
        if (key.Length is not (32 or 64))
            throw new ArgumentException("Token keys must be 64 bytes (AES256) or 32 bytes (legacy AES128).", nameof(key));
        signingKey = key[..(key.Length / 2)].ToArray();
        aes = Aes.Create();
        byte[] encryptionKey = key[(key.Length / 2)..].ToArray();
        try { aes.Key = encryptionKey; }
        finally { CryptographicOperations.ZeroMemory(encryptionKey); }
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        Span<byte> iv = stackalloc byte[16];
        RandomNumberGenerator.Fill(iv);
        return Encrypt(plaintext, iv);
    }

    /// <summary>Encrypt with a supplied IV. Use only with independently random, non-repeating IVs.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> iv)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (iv.Length != 16) throw new ArgumentException("IV must be 16 bytes.", nameof(iv));
        int ciphertextLength = aes.GetCiphertextLengthCbc(plaintext.Length);
        byte[] token = new byte[checked(ciphertextLength + Overhead)];
        iv.CopyTo(token);
        aes.EncryptCbc(plaintext, iv, token.AsSpan(16, ciphertextLength), PaddingMode.PKCS7);
        HMACSHA256.HashData(signingKey, token.AsSpan(0, token.Length - 32), token.AsSpan(token.Length - 32));
        return token;
    }

    public bool VerifyHmac(ReadOnlySpan<byte> token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!HasValidLength(token.Length)) return false;
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(signingKey, token[..^32], expected);
        bool valid = CryptographicOperations.FixedTimeEquals(expected, token[^32..]);
        CryptographicOperations.ZeroMemory(expected);
        return valid;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!VerifyHmac(token)) throw new CryptographicException("Malformed token or invalid authentication tag.");
        return aes.DecryptCbc(token[16..^32], token[..16], PaddingMode.PKCS7);
    }

    public static bool HasValidLength(int length) => length >= MinimumLength && (length - Overhead) % 16 == 0;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(signingKey);
        aes.Dispose();
    }
}
