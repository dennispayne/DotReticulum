using System.Security.Cryptography;
using Curve = Org.BouncyCastle.Math.EC.Rfc8032.Ed25519;

namespace DotReticulum.Crypto;

/// <summary>Raw RFC 8032 Ed25519 keys (32-byte seed, 32-byte public key).</summary>
public static class Ed25519
{
    public const int KeySize = 32;
    public const int SignatureSize = 64;

    public static byte[] GeneratePrivateKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static byte[] GetPublicKey(ReadOnlySpan<byte> privateKey)
    {
        RequireKey(privateKey);
        byte[] seed = privateKey.ToArray();
        try
        {
            byte[] result = new byte[KeySize];
            Curve.GeneratePublicKey(seed, 0, result, 0);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    public static bool IsValidPublicKey(ReadOnlySpan<byte> publicKey) =>
        publicKey.Length == KeySize && Curve.ValidatePublicKeyFull(publicKey.ToArray(), 0);

    public static byte[] Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        RequireKey(privateKey);
        byte[] seed = privateKey.ToArray();
        byte[] data = message.ToArray();
        try
        {
            byte[] signature = new byte[SignatureSize];
            Curve.Sign(seed, 0, data, 0, data.Length, signature, 0);
            return signature;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureSize || !IsValidPublicKey(publicKey)) return false;
        byte[] data = message.ToArray();
        try { return Curve.Verify(signature.ToArray(), 0, publicKey.ToArray(), 0, data, 0, data.Length); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize) throw new ArgumentException("Ed25519 seeds must be 32 bytes.", nameof(key));
    }
}
