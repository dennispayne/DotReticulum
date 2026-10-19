using System.Security.Cryptography;

namespace DotReticulum.Crypto;

/// <summary>
/// Reticulum token: <c>IV(16) || AES-CBC/PKCS7 ciphertext || HMAC-SHA256(32)</c>. Not standard Fernet
/// (no version, timestamp or base64). A 64-byte key is 32 signing + 32 AES-256; a 32-byte key is 16 + 16 (AES-128).
/// </summary>
public sealed class Token : IDisposable
{
    public const int IvSize = 16;
    public const int MacSize = 32;
    public const int Overhead = 48;
    private const int BlockSize = 16;

    private readonly byte[] _signingKey;
    private readonly byte[] _encryptionKey;
    private bool _disposed;

    public Token(ReadOnlySpan<byte> key)
    {
        if (key.Length == 64)
        {
            _signingKey = key[..32].ToArray();
            _encryptionKey = key[32..].ToArray();
        }
        else if (key.Length == 32)
        {
            _signingKey = key[..16].ToArray();
            _encryptionKey = key[16..].ToArray();
        }
        else
        {
            throw new ArgumentException($"Token key must be 128 or 256 bits (32 or 64 bytes), not {key.Length * 8} bits.", nameof(key));
        }
    }

    public static byte[] GenerateKey(bool aes256 = true) => RandomNumberGenerator.GetBytes(aes256 ? 64 : 32);

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var padded = (plaintext.Length / BlockSize + 1) * BlockSize;
        var token = new byte[IvSize + padded + MacSize];
        RandomNumberGenerator.Fill(token.AsSpan(0, IvSize));
        using (var aes = Aes.Create())
        {
            aes.Key = _encryptionKey;
            aes.EncryptCbc(plaintext, token.AsSpan(0, IvSize), token.AsSpan(IvSize, padded), PaddingMode.PKCS7);
        }
        HMACSHA256.HashData(_signingKey, token.AsSpan(0, IvSize + padded), token.AsSpan(IvSize + padded));
        return token;
    }

    /// <summary>Verifies the HMAC (constant time) before decrypting. Throws <see cref="CryptographicException"/> on any failure.</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasValidLength(token)) throw new CryptographicException("Token length is invalid.");
        if (!VerifyHmac(token)) throw new CryptographicException("Token HMAC was invalid.");

        var ciphertext = token[IvSize..^MacSize];
        var buffer = new byte[ciphertext.Length];
        try
        {
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            var written = aes.DecryptCbc(ciphertext, token[..IvSize], buffer, PaddingMode.PKCS7);
            return buffer.AsSpan(0, written).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    public bool TryDecrypt(ReadOnlySpan<byte> token, out byte[] plaintext)
    {
        try
        {
            plaintext = Decrypt(token);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = [];
            return false;
        }
    }

    public bool VerifyHmac(ReadOnlySpan<byte> token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (token.Length <= MacSize) return false;
        Span<byte> expected = stackalloc byte[MacSize];
        HMACSHA256.HashData(_signingKey, token[..^MacSize], expected);
        return CryptographicOperations.FixedTimeEquals(expected, token[^MacSize..]);
    }

    private static bool HasValidLength(ReadOnlySpan<byte> token) =>
        token.Length >= Overhead + BlockSize && (token.Length - Overhead) % BlockSize == 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_signingKey);
        CryptographicOperations.ZeroMemory(_encryptionKey);
    }
}
