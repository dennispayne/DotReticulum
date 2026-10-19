using System.Numerics;
using System.Security.Cryptography;
using Bc = Org.BouncyCastle.Math.EC.Rfc7748.X25519;

namespace DotReticulum.Crypto;

/// <summary>Raw X25519 (RFC 7748) with 32-byte private and public keys.</summary>
public static class X25519
{
    public const int KeySize = 32;

    private static readonly BigInteger P = (BigInteger.One << 255) - 19;

    // u-coordinates of the points of order dividing 8 (canonical encodings; the non-canonical
    // aliases of 0, 1 and p-1 are rejected by the range check).
    private static readonly byte[][] SmallOrder =
    [
        Hex("0000000000000000000000000000000000000000000000000000000000000000"),
        Hex("0100000000000000000000000000000000000000000000000000000000000000"),
        Hex("e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800"),
        Hex("5f9c95bca3508c24b1d0b1559c83ef5b04445cc4581c8e86d8224eddd09f1157"),
        Hex("ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f"),
    ];

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    public static byte[] GeneratePrivateKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static byte[] GetPublicKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != KeySize) throw new ArgumentException("X25519 private key must be 32 bytes.", nameof(privateKey));
        var k = privateKey.ToArray();
        var pk = new byte[KeySize];
        try
        {
            Bc.ScalarMultBase(k, 0, pk, 0);
            return pk;
        }
        finally { CryptographicOperations.ZeroMemory(k); }
    }

    /// <summary>
    /// True for a 32-byte, canonical (bit 255 clear, value below 2^255-19), non-small-order u-coordinate.
    /// </summary>
    public static bool IsValidPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != KeySize) return false;
        if ((publicKey[31] & 0x80) != 0) return false;
        if (new BigInteger(publicKey, isUnsigned: true, isBigEndian: false) >= P) return false;
        foreach (var bad in SmallOrder)
        {
            if (CryptographicOperations.FixedTimeEquals(publicKey, bad)) return false;
        }
        return true;
    }

    /// <summary>Computes the shared secret; throws <see cref="CryptographicException"/> on an invalid peer key or all-zero output.</summary>
    public static byte[] Agree(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey)
    {
        if (privateKey.Length != KeySize) throw new ArgumentException("X25519 private key must be 32 bytes.", nameof(privateKey));
        if (!IsValidPublicKey(peerPublicKey)) throw new CryptographicException("Invalid X25519 public key.");
        var k = privateKey.ToArray();
        var shared = new byte[KeySize];
        try
        {
            Bc.ScalarMult(k, 0, peerPublicKey.ToArray(), 0, shared, 0);
            if (CryptographicOperations.FixedTimeEquals(shared, new byte[KeySize]))
            {
                throw new CryptographicException("X25519 produced an all-zero shared secret.");
            }
            return shared;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(shared);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(k); }
    }
}
