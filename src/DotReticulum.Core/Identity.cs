using System.Security.Cryptography;
using DotReticulum.Crypto;

namespace DotReticulum.Core;

/// <summary>A Reticulum identity: X25519 followed by Ed25519, each 32 bytes.
/// Dispose private identities when no longer needed. Instances are not thread-safe.</summary>
public sealed class Identity : IDisposable
{
    public const int KeySize = 64;
    public const int HashSize = 16;
    private readonly byte[] publicKey;
    private readonly byte[] hash;
    private readonly byte[]? privateKey;
    private bool disposed;

    private Identity(byte[] publicKey, byte[]? privateKey)
    {
        this.publicKey = publicKey;
        this.privateKey = privateKey;
        hash = SHA256.HashData(publicKey)[..HashSize];
    }

    public bool HasPrivateKey { get { ThrowIfDisposed(); return privateKey is not null; } }
    public byte[] Hash { get { ThrowIfDisposed(); return (byte[])hash.Clone(); } }
    public string HexHash { get { ThrowIfDisposed(); return Convert.ToHexStringLower(hash); } }

    public static Identity Generate()
    {
        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        try { return FromPrivateKey(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static Identity FromPrivateKey(ReadOnlySpan<byte> key)
    {
        RequireLength(key);
        byte[] publicKey = new byte[KeySize];
        X25519.GetPublicKey(key[..32]).CopyTo(publicKey, 0);
        Ed25519.GetPublicKey(key[32..]).CopyTo(publicKey, 32);
        return new Identity(publicKey, key.ToArray());
    }

    public static Identity FromPublicKey(ReadOnlySpan<byte> key)
    {
        RequireLength(key);
        if (!X25519.IsValidPublicKey(key[..32]) || !Ed25519.IsValidPublicKey(key[32..]))
            throw new CryptographicException("Invalid identity public key.");
        return new Identity(key.ToArray(), null);
    }

    public byte[] ExportPublicKey() { ThrowIfDisposed(); return (byte[])publicKey.Clone(); }
    /// <summary>The caller owns the returned secret and must clear it after use.</summary>
    public byte[] ExportPrivateKey() { RequirePrivateKey(); return (byte[])privateKey!.Clone(); }

    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        RequirePrivateKey();
        return Ed25519.Sign(privateKey.AsSpan(32), message);
    }

    public bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        ThrowIfDisposed();
        return Ed25519.Verify(publicKey.AsSpan(32), message, signature);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        ThrowIfDisposed();
        byte[] ephemeral = X25519.GeneratePrivateKey();
        byte[]? shared = null;
        byte[]? derived = null;
        try
        {
            byte[] ephemeralPublic = X25519.GetPublicKey(ephemeral);
            shared = X25519.Agree(ephemeral, publicKey.AsSpan(0, 32));
            derived = KeyDerivation.HkdfSha256(shared, 64, hash);
            using var token = new Token(derived);
            byte[] encrypted = token.Encrypt(plaintext);
            byte[] result = new byte[32 + encrypted.Length];
            ephemeralPublic.CopyTo(result, 0);
            encrypted.CopyTo(result, 32);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeral);
            if (shared is not null) CryptographicOperations.ZeroMemory(shared);
            if (derived is not null) CryptographicOperations.ZeroMemory(derived);
        }
    }

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        RequirePrivateKey();
        if (ciphertext.Length < 32 || !Token.HasValidLength(ciphertext.Length - 32))
            throw new CryptographicException("Malformed identity ciphertext.");
        byte[] shared = X25519.Agree(privateKey.AsSpan(0, 32), ciphertext[..32]);
        byte[]? derived = null;
        try
        {
            derived = KeyDerivation.HkdfSha256(shared, 64, hash);
            using var token = new Token(derived);
            return token.Decrypt(ciphertext[32..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            if (derived is not null) CryptographicOperations.ZeroMemory(derived);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (privateKey is not null) CryptographicOperations.ZeroMemory(privateKey);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private void RequirePrivateKey()
    {
        ThrowIfDisposed();
        if (privateKey is null) throw new InvalidOperationException("Identity does not hold a private key.");
    }
    private static void RequireLength(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize) throw new ArgumentException("Identity keys must be 64 bytes.", nameof(key));
    }
}
