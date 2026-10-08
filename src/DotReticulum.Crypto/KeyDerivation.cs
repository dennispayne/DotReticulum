using System.Security.Cryptography;

namespace DotReticulum.Crypto;

public static class KeyDerivation
{
    /// <summary>RFC 5869 HKDF-SHA256. Reticulum uses an empty info field.</summary>
    public static byte[] HkdfSha256(ReadOnlySpan<byte> inputKeyMaterial, int length,
        ReadOnlySpan<byte> salt = default, ReadOnlySpan<byte> info = default)
    {
        if (length is <= 0 or > 255 * 32) throw new ArgumentOutOfRangeException(nameof(length));
        byte[] result = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, result, salt, info);
        return result;
    }
}
