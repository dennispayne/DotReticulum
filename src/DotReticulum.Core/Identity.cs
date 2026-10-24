using System.Security.Cryptography;
using DotReticulum.Crypto;

namespace DotReticulum.Core;

/// <summary>
/// A Reticulum identity: an X25519 encryption key pair and an Ed25519 signing key pair. Public and private
/// key blobs are 64 bytes, X25519 first, then Ed25519. The hash is the first 16 bytes of SHA-256 of the public key.
/// </summary>
public sealed class Identity : IDisposable
{
    public const int KeySize = 64;
    public const int HashSize = 16;
    public const int DerivedKeySize = 64;
    public const int RatchetSize = 32;
    private const int HalfKey = 32;

    private readonly byte[] _publicKey;
    private readonly object _gate = new();
    private byte[]? _privateKey;
    private bool _disposed;

    private Identity(byte[] publicKey, byte[]? privateKey)
    {
        _publicKey = publicKey;
        _privateKey = privateKey;
        Hash = TruncatedHash(publicKey);
    }

    /// <summary>The 16-byte identity hash.</summary>
    public byte[] Hash { get; }

    public string HexHash => Convert.ToHexStringLower(Hash);

    public bool HasPrivateKey => _privateKey is not null;

    /// <summary>Last announced application data when recalled from a destination cache.</summary>
    public byte[]? AppData { get; set; }

    public static Identity Generate()
    {
        var x = X25519.GeneratePrivateKey();
        var ed = Ed25519.GenerateSeed();
        var privateKey = new byte[KeySize];
        try
        {
            x.CopyTo(privateKey, 0);
            ed.CopyTo(privateKey, HalfKey);
            return FromPrivateKey(privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(x);
            CryptographicOperations.ZeroMemory(ed);
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    /// <summary>Loads a 64-byte private key blob (X25519 private || Ed25519 seed).</summary>
    public static Identity FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != KeySize) throw new ArgumentException("Identity private key must be 64 bytes.", nameof(privateKey));
        var pub = new byte[KeySize];
        X25519.GetPublicKey(privateKey[..HalfKey]).CopyTo(pub, 0);
        Ed25519.GetPublicKey(privateKey[HalfKey..]).CopyTo(pub, HalfKey);
        return new Identity(pub, privateKey.ToArray());
    }

    /// <summary>Loads a 64-byte public key blob, rejecting low-order/non-canonical X25519 and invalid Ed25519 keys.</summary>
    public static Identity FromPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != KeySize) throw new ArgumentException("Identity public key must be 64 bytes.", nameof(publicKey));
        if (!X25519.IsValidPublicKey(publicKey[..HalfKey])) throw new ArgumentException("Invalid X25519 public key.", nameof(publicKey));
        if (!Ed25519.IsValidPublicKey(publicKey[HalfKey..])) throw new ArgumentException("Invalid Ed25519 public key.", nameof(publicKey));
        return new Identity(publicKey.ToArray(), null);
    }

    public static byte[] FullHash(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] TruncatedHash(ReadOnlySpan<byte> data) => SHA256.HashData(data)[..HashSize];

    public byte[] ExportPublicKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (byte[])_publicKey.Clone();
    }

    public byte[] ExportPrivateKey()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (_privateKey ?? throw new InvalidOperationException("Identity does not hold a private key.")).ToArray();
        }
    }

    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = _privateKey ?? throw new InvalidOperationException("Signing failed because identity does not hold a private key.");
            return Ed25519.Sign(key.AsSpan(HalfKey), message);
        }
    }

    public bool Verify(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Ed25519.Verify(_publicKey.AsSpan(HalfKey), message, signature);
    }

    /// <summary>
    /// Encrypts for this identity: <c>ephemeral_public(32) || token</c>. When <paramref name="ratchetPublicKey"/>
    /// is given, the ECDH is performed against that ratchet key instead of the identity's X25519 key (salt stays the identity hash).
    /// </summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> ratchetPublicKey = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = ratchetPublicKey.IsEmpty ? _publicKey.AsSpan(0, HalfKey) : ratchetPublicKey;
        var ephemeral = X25519.GeneratePrivateKey();
        byte[]? shared = null;
        byte[]? derived = null;
        try
        {
            shared = X25519.Agree(ephemeral, target);
            derived = KeyDerivation.HkdfSha256(shared, DerivedKeySize, Hash);
            using var token = new Token(derived);
            var body = token.Encrypt(plaintext);
            var result = new byte[HalfKey + body.Length];
            X25519.GetPublicKey(ephemeral).CopyTo(result, 0);
            body.CopyTo(result, HalfKey);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeral);
            if (shared is not null) CryptographicOperations.ZeroMemory(shared);
            if (derived is not null) CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <summary>
    /// Decrypts a token produced by <see cref="Encrypt"/>. Mirrors upstream: each ratchet private key is tried
    /// in order, then (unless <paramref name="enforceRatchets"/>) the identity key. Returns null on any failure;
    /// throws if the identity has no private key.
    /// </summary>
    public byte[]? Decrypt(ReadOnlySpan<byte> token, IReadOnlyList<byte[]>? ratchets = null, bool enforceRatchets = false)
        => DecryptWithMetadata(token, ratchets, enforceRatchets)?.Plaintext;

    /// <summary>Decrypts and reports the actual selected ratchet, including an older retained key.
    /// Base-key fallback has a null ratchet ID. Failure returns null, so stale metadata cannot escape from a previous call.</summary>
    public IdentityDecryptionResult? DecryptWithMetadata(ReadOnlySpan<byte> token, IReadOnlyList<byte[]>? ratchets = null,
        bool enforceRatchets = false)
    {
        lock (_gate) return DecryptCore(token, ratchets, enforceRatchets);
    }

    private IdentityDecryptionResult? DecryptCore(ReadOnlySpan<byte> token, IReadOnlyList<byte[]>? ratchets, bool enforceRatchets)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = _privateKey ?? throw new InvalidOperationException("Decryption failed because identity does not hold a private key.");
        if (token.Length <= HalfKey) return null;

        var peer = token[..HalfKey];
        var body = token[HalfKey..];

        if (ratchets is not null)
        {
            foreach (var ratchet in ratchets)
            {
                if (ratchet is null || ratchet.Length != RatchetSize) continue;
                var plaintext = TryDecryptWith(ratchet, peer, body);
                if (plaintext is not null)
                    return new IdentityDecryptionResult(plaintext, DestinationRatchets.GetRatchetId(X25519.GetPublicKey(ratchet)));
            }
        }

        if (enforceRatchets) return null;
        var fallback = TryDecryptWith(key.AsSpan(0, HalfKey), peer, body);
        return fallback is null ? null : new IdentityDecryptionResult(fallback, null);
    }

    private byte[]? TryDecryptWith(ReadOnlySpan<byte> x25519Private, ReadOnlySpan<byte> peer, ReadOnlySpan<byte> body)
    {
        byte[]? shared = null;
        byte[]? derived = null;
        try
        {
            shared = X25519.Agree(x25519Private, peer);
            derived = KeyDerivation.HkdfSha256(shared, DerivedKeySize, Hash);
            using var token = new Token(derived);
            return token.TryDecrypt(body, out var plaintext) ? plaintext : null;
        }
        catch (CryptographicException) { return null; }
        finally
        {
            if (shared is not null) CryptographicOperations.ZeroMemory(shared);
            if (derived is not null) CryptographicOperations.ZeroMemory(derived);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_privateKey is not null) CryptographicOperations.ZeroMemory(_privateKey);
            _privateKey = null;
        }
    }
}
