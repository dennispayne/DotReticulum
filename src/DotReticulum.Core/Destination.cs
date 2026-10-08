using System.Security.Cryptography;
using System.Text;

namespace DotReticulum.Core;

/// <summary>Destination naming and address derivation, independent of identity key management.</summary>
public static class Destination
{
    public const int NameHashLength = 10;
    public const int HashLength = 16;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string ExpandName(string appName, params string[] aspects)
    {
        ArgumentNullException.ThrowIfNull(appName);
        ArgumentNullException.ThrowIfNull(aspects);
        if (appName.Contains('.'))
            throw new ArgumentException("App names cannot contain dots.", nameof(appName));
        foreach (var aspect in aspects)
        {
            ArgumentNullException.ThrowIfNull(aspect);
            if (aspect.Contains('.'))
                throw new ArgumentException("Aspects cannot contain dots.", nameof(aspects));
        }
        return string.Join(".", new[] { appName }.Concat(aspects));
    }

    public static byte[] CalculateNameHash(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SHA256.HashData(Utf8.GetBytes(name))[..NameHashLength];
    }

    /// <summary>
    /// Hashes a UTF-8 app.aspect name's 10-byte name hash plus an optional
    /// 16-byte identity hash. An empty identity hash denotes a plain destination.
    /// The name must not include an identity's hexadecimal display suffix.
    /// </summary>
    public static byte[] CalculateHash(string name, ReadOnlySpan<byte> identityHash = default)
    {
        if (!identityHash.IsEmpty && identityHash.Length != HashLength)
            throw new ArgumentException("Identity hash must be empty or 16 bytes.", nameof(identityHash));
        Span<byte> material = stackalloc byte[NameHashLength + HashLength];
        CalculateNameHash(name).CopyTo(material);
        identityHash.CopyTo(material[NameHashLength..]);
        return SHA256.HashData(material[..(NameHashLength + identityHash.Length)])[..HashLength];
    }

    public static byte[] CalculateHash(string name, DestinationType type, ReadOnlySpan<byte> identityHash = default)
    {
        if ((byte)type > 3) throw new ArgumentOutOfRangeException(nameof(type));
        if (type == DestinationType.Plain ? !identityHash.IsEmpty : identityHash.Length != HashLength)
            throw new ArgumentException("Plain destinations have no identity; other types require 16 bytes.", nameof(identityHash));
        return CalculateHash(name, identityHash);
    }
}
