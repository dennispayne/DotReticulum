using System.Security.Cryptography;
using System.Text;

namespace DotReticulum.Interfaces;

/// <summary>I2P public destination hashing and upstream-compatible key-file naming.</summary>
public static class I2pDestination
{
    public static string KeyFileName(string name, ReadOnlySpan<byte> transportIdentityHash, bool legacy = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!legacy && transportIdentityHash.Length != 16)
            throw new ArgumentException("Expected a 16-byte transport identity hash.", nameof(transportIdentityHash));
        var nameHash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        var input = legacy ? nameHash : nameHash.Concat(SHA256.HashData(transportIdentityHash)).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(input)) + ".i2p";
    }

    public static string PublicBase32(ReadOnlySpan<byte> publicDestination)
    {
        if (publicDestination.Length < 387 ||
            publicDestination.Length != 387 + ((publicDestination[385] << 8) | publicDestination[386]))
            throw new ArgumentException("Invalid I2P public destination.", nameof(publicDestination));
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var hash = SHA256.HashData(publicDestination);
        var output = new StringBuilder(52);
        uint accumulator = 0;
        var bits = 0;
        foreach (var value in hash)
        {
            accumulator = (accumulator << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(alphabet[(int)((accumulator >> bits) & 31)]);
            }
        }
        if (bits > 0) output.Append(alphabet[(int)((accumulator << (5 - bits)) & 31)]);
        return output.ToString();
    }

    internal static string Base32FromSamPublic(string encoded)
    {
        try
        {
            I2pSamConnection.ValidateToken(encoded);
            var decoded = Convert.FromBase64String(encoded.Replace('-', '+').Replace('~', '/'));
            return PublicBase32(decoded);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            throw new InvalidDataException("SAM supplied an invalid peer destination.", error);
        }
    }

    internal static string Base32FromPrivate(string encoded)
    {
        I2pSamConnection.ValidateToken(encoded);
        byte[] decoded;
        try { decoded = Convert.FromBase64String(encoded.Replace('-', '+').Replace('~', '/')); }
        catch (FormatException) { throw new InvalidDataException("Invalid I2P destination encoding."); }
        try
        {
            if (decoded.Length < 387) throw new InvalidDataException("Truncated I2P destination.");
            var length = 387 + ((decoded[385] << 8) | decoded[386]);
            if (decoded.Length <= length) throw new InvalidDataException("I2P private destination is missing private data.");
            return PublicBase32(decoded.AsSpan(0, length));
        }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }
}
