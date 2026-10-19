using System.Security.Cryptography;
using System.Text;

namespace DotReticulum.Core;

/// <summary>
/// Destination naming and addressing, matching upstream <c>RNS.Destination</c>:
/// name_hash = SHA-256(utf8(app_name.aspect1.aspect2...))[..10] (the identity is NOT part of the name hash),
/// destination hash = SHA-256(name_hash [|| identity_hash])[..16]. Plain destinations hold no identity.
/// </summary>
public sealed class Destination
{
    public const int NameHashLength = 10;
    public const int HashLength = 16;

    private Destination(DestinationType type, string appName, string[] aspects, byte[]? identityHash)
    {
        Type = type;
        AppName = appName;
        Aspects = aspects;
        IdentityHash = identityHash;
        FullName = ExpandName(identityHash, appName, aspects);
        NameHash = ComputeNameHash(appName, aspects);
        Hash = ComputeHash(identityHash, appName, aspects);
    }

    public DestinationType Type { get; }
    public string AppName { get; }
    public IReadOnlyList<string> Aspects { get; }

    /// <summary>The identity hash this destination is bound to, or null (always null for plain destinations).</summary>
    public byte[]? IdentityHash { get; }

    /// <summary>Human-readable name; for identity-bound destinations the identity's hex hash is appended.</summary>
    public string FullName { get; }

    public byte[] NameHash { get; }
    public byte[] Hash { get; }

    /// <summary>
    /// Creates a Single, Group or Plain destination. Single requires an identity hash, Plain forbids one
    /// (upstream: "PLAIN cannot hold an identity"), Group accepts either. Link destinations are derived from link
    /// requests, not from names, and cannot be created here.
    /// </summary>
    public static Destination Create(DestinationType type, string appName, ReadOnlySpan<string> aspects, ReadOnlySpan<byte> identityHash = default)
    {
        if (type == DestinationType.Link) throw new ArgumentException("Link destinations are not name-derived.", nameof(type));
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (type == DestinationType.Plain && !identityHash.IsEmpty) throw new ArgumentException("A plain destination cannot hold an identity.", nameof(identityHash));
        if (type == DestinationType.Single && identityHash.IsEmpty) throw new ArgumentException("A single destination requires an identity.", nameof(identityHash));
        if (!identityHash.IsEmpty && identityHash.Length != HashLength) throw new ArgumentException("Identity hash must be 16 bytes.", nameof(identityHash));
        var aspectArray = aspects.ToArray();
        ValidateName(appName, aspectArray);
        return new Destination(type, appName, aspectArray, identityHash.IsEmpty ? null : identityHash.ToArray());
    }

    public static Destination Create(DestinationType type, string appName, ReadOnlySpan<string> aspects, Identity identity) =>
        Create(type, appName, aspects, identity.Hash);

    /// <summary>Upstream <c>expand_name</c>. The identity hex hash (lowercase) is appended only when an identity hash is supplied.</summary>
    public static string ExpandName(ReadOnlySpan<byte> identityHash, string appName, ReadOnlySpan<string> aspects)
    {
        ValidateName(appName, aspects);
        var name = new StringBuilder(appName);
        foreach (var aspect in aspects) name.Append('.').Append(aspect);
        if (!identityHash.IsEmpty) name.Append('.').Append(Convert.ToHexStringLower(identityHash));
        return name.ToString();
    }

    /// <summary>10-byte name hash; never includes the identity.</summary>
    public static byte[] ComputeNameHash(string appName, ReadOnlySpan<string> aspects) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(ExpandName(default, appName, aspects)))[..NameHashLength];

    /// <summary>Upstream <c>Destination.hash</c>: SHA-256(name_hash [|| identity_hash])[..16].</summary>
    public static byte[] ComputeHash(ReadOnlySpan<byte> identityHash, string appName, ReadOnlySpan<string> aspects)
    {
        if (!identityHash.IsEmpty && identityHash.Length != HashLength) throw new ArgumentException("Identity hash must be 16 bytes.", nameof(identityHash));
        var nameHash = ComputeNameHash(appName, aspects);
        var material = new byte[nameHash.Length + identityHash.Length];
        nameHash.CopyTo(material, 0);
        identityHash.CopyTo(material.AsSpan(nameHash.Length));
        return SHA256.HashData(material)[..HashLength];
    }

    /// <summary>Upstream <c>app_and_aspects_from_name</c>: splits on '.'.</summary>
    public static (string AppName, string[] Aspects) SplitFullName(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        var parts = fullName.Split('.');
        return (parts[0], parts[1..]);
    }

    /// <summary>Upstream <c>hash_from_name_and_identity</c>.</summary>
    public static byte[] ComputeHashFromNameAndIdentity(string fullName, ReadOnlySpan<byte> identityHash)
    {
        var (app, aspects) = SplitFullName(fullName);
        return ComputeHash(identityHash, app, aspects);
    }

    private static void ValidateName(string appName, ReadOnlySpan<string> aspects)
    {
        ArgumentNullException.ThrowIfNull(appName);
        if (appName.Contains('.')) throw new ArgumentException("Dots can't be used in app names.", nameof(appName));
        foreach (var aspect in aspects)
        {
            ArgumentNullException.ThrowIfNull(aspect);
            if (aspect.Contains('.')) throw new ArgumentException("Dots can't be used in aspects.", nameof(aspects));
        }
    }
}
