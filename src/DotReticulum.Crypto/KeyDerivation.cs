using System.Security.Cryptography;

namespace DotReticulum.Crypto;

public static class KeyDerivation
{
    public const int MaxOutputLength = 255 * 32;

    /// <summary>
    /// HKDF-SHA256 (RFC 5869). An empty salt is replaced with 32 zero bytes, matching upstream's
    /// <c>RNS.Cryptography.hkdf</c> (this is also what RFC 5869 specifies).
    /// </summary>
    public static byte[] HkdfSha256(ReadOnlySpan<byte> ikm, int length, ReadOnlySpan<byte> salt = default, ReadOnlySpan<byte> info = default)
    {
        if (length < 1 || length > MaxOutputLength) throw new ArgumentOutOfRangeException(nameof(length), "HKDF length must be 1..8160.");
        if (ikm.IsEmpty) throw new ArgumentException("Cannot derive a key from empty input material.", nameof(ikm));
        var output = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, info);
        return output;
    }
}
