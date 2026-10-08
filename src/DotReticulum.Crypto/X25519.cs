using System.Security.Cryptography;
using Curve = Org.BouncyCastle.Math.EC.Rfc7748.X25519;

namespace DotReticulum.Crypto;

/// <summary>Raw RFC 7748 X25519 Diffie-Hellman operations.</summary>
public static class X25519
{
    public const int KeySize = 32;

    public static byte[] GeneratePrivateKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static byte[] GetPublicKey(ReadOnlySpan<byte> privateKey)
    {
        RequireKey(privateKey);
        byte[] scalar = privateKey.ToArray();
        try
        {
            byte[] result = new byte[KeySize];
            Curve.GeneratePublicKey(scalar, 0, result, 0);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(scalar); }
    }

    public static byte[] Agree(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        RequireKey(privateKey);
        RequireKey(publicKey);
        byte[] scalar = privateKey.ToArray();
        byte[] shared = new byte[KeySize];
        try
        {
            if (!Curve.CalculateAgreement(scalar, 0, publicKey.ToArray(), 0, shared, 0))
                throw new CryptographicException("Invalid or low-order X25519 public key.");
            return shared;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(shared);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(scalar); }
    }

    public static bool IsValidPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != KeySize) return false;
        // A fixed, non-secret probe scalar detects points yielding all-zero agreement.
        Span<byte> probe = stackalloc byte[KeySize];
        probe.Clear();
        try
        {
            byte[] shared = Agree(probe, publicKey);
            CryptographicOperations.ZeroMemory(shared);
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize) throw new ArgumentException("X25519 keys must be 32 bytes.", nameof(key));
    }
}
