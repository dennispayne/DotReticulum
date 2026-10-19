using System.Security.Cryptography;
using Bc = Org.BouncyCastle.Math.EC.Rfc8032.Ed25519;

namespace DotReticulum.Crypto;

/// <summary>Raw Ed25519 (RFC 8032) with 32-byte seeds, 32-byte public keys and 64-byte signatures.</summary>
public static class Ed25519
{
    public const int SeedSize = 32;
    public const int PublicKeySize = 32;
    public const int SignatureSize = 64;

    public static byte[] GenerateSeed() => RandomNumberGenerator.GetBytes(SeedSize);

    public static byte[] GetPublicKey(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize) throw new ArgumentException("Ed25519 seed must be 32 bytes.", nameof(seed));
        var sk = seed.ToArray();
        var pk = new byte[PublicKeySize];
        try
        {
            Bc.GeneratePublicKey(sk, 0, pk, 0);
            return pk;
        }
        finally { CryptographicOperations.ZeroMemory(sk); }
    }

    /// <summary>
    /// True when the encoding decodes to a point of the prime-order subgroup. This rejects non-canonical
    /// encodings, off-curve values and small-order (including identity) points.
    /// </summary>
    public static bool IsValidPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeySize) return false;
        try { return Bc.ValidatePublicKeyFull(publicKey.ToArray(), 0); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        if (seed.Length != SeedSize) throw new ArgumentException("Ed25519 seed must be 32 bytes.", nameof(seed));
        var sk = seed.ToArray();
        var pk = new byte[PublicKeySize];
        var sig = new byte[SignatureSize];
        try
        {
            Bc.GeneratePublicKey(sk, 0, pk, 0);
            var m = message.ToArray();
            Bc.Sign(sk, 0, pk, 0, m, 0, m.Length, sig, 0);
            return sig;
        }
        finally { CryptographicOperations.ZeroMemory(sk); }
    }

    /// <summary>Returns false for any invalid public key, signature length or signature.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureSize || !IsValidPublicKey(publicKey)) return false;
        var m = message.ToArray();
        try { return Bc.Verify(signature.ToArray(), 0, publicKey.ToArray(), 0, m, 0, m.Length); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }
}
