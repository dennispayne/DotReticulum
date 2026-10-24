using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace DotReticulum.Core;

public enum AnnounceStatus
{
    Valid,
    NotAnnounce,
    Malformed,
    InvalidPublicKey,
    InvalidSignature,
    DestinationMismatch,
}

/// <summary>The fields of a structurally parsed announce payload.</summary>
public sealed class AnnounceData
{
    internal AnnounceData(byte[] publicKey, byte[] nameHash, byte[] randomHash, byte[]? ratchet, byte[] signature, byte[]? appData)
    {
        PublicKey = publicKey;
        NameHash = nameHash;
        RandomHash = randomHash;
        Ratchet = ratchet;
        Signature = signature;
        AppData = appData;
    }

    public byte[] PublicKey { get; }
    public byte[] NameHash { get; }
    public byte[] RandomHash { get; }

    /// <summary>The 32-byte ratchet public key, or null when the context flag was not set.</summary>
    public byte[]? Ratchet { get; }

    public byte[] Signature { get; }

    /// <summary>
    /// Upstream semantics: null when the payload is no longer than the non-ratchet minimum, otherwise the (possibly empty)
    /// remainder after the signature.
    /// </summary>
    public byte[]? AppData { get; }

    /// <summary>The announced emission time, seconds since the Unix epoch, from the last 5 bytes of the random hash.</summary>
    public long Emitted => Announce.EmittedFromRandomHash(RandomHash);
}

public sealed record AnnounceValidation(AnnounceStatus Status, AnnounceData? Data, Identity? Identity)
{
    public bool IsValid => Status == AnnounceStatus.Valid;
}

/// <summary>
/// Announce creation and validation, byte-compatible with upstream <c>Destination.announce</c> and <c>Identity.validate_announce</c>:
/// payload = public_key(64) || name_hash(10) || random_hash(10) || [ratchet(32)] || signature(64) || app_data, with the context
/// flag set exactly when a ratchet is present, and the signature over destination_hash || public_key || name_hash || random_hash || [ratchet] || app_data.
/// </summary>
public static class Announce
{
    public const int RandomHashLength = 10;
    public const int SignatureLength = 64;
    public const byte PathResponseContext = 0x0B;

    private const int KeyEnd = Identity.KeySize;
    private const int NameEnd = KeyEnd + Destination.NameHashLength;
    private const int RandomEnd = NameEnd + RandomHashLength;

    /// <summary>Smallest valid payload without a ratchet.</summary>
    public const int MinPayloadLength = RandomEnd + SignatureLength;

    /// <summary>Upstream random hash: 5 random bytes followed by the 5-byte big-endian emission timestamp (seconds).</summary>
    public static byte[] CreateRandomHash(long unixSeconds)
    {
        var hash = new byte[RandomHashLength];
        RandomNumberGenerator.Fill(hash.AsSpan(0, 5));
        WriteTimestamp(hash.AsSpan(5), unixSeconds);
        return hash;
    }

    public static long EmittedFromRandomHash(ReadOnlySpan<byte> randomHash)
    {
        if (randomHash.Length != RandomHashLength) throw new ArgumentException("Random hash must be 10 bytes.", nameof(randomHash));
        long value = 0;
        foreach (var b in randomHash[5..]) value = (value << 8) | b;
        return value;
    }

    /// <summary>
    /// Builds an announce packet for a local Single destination. When <paramref name="ratchets"/> is supplied it is rotated
    /// (if due) and its newest public key is included and the context flag set. <paramref name="randomHash"/> may be supplied
    /// for deterministic output; otherwise one is generated from <paramref name="unixSeconds"/>.
    /// </summary>
    public static Packet Create(
        Identity identity,
        Destination destination,
        ReadOnlySpan<byte> appData = default,
        DestinationRatchets? ratchets = null,
        bool pathResponse = false,
        long? unixSeconds = null,
        ReadOnlySpan<byte> randomHash = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Type != DestinationType.Single) throw new ArgumentException("Only SINGLE destinations can be announced.", nameof(destination));
        if (destination.IdentityHash is null || !destination.IdentityHash.AsSpan().SequenceEqual(identity.Hash))
            throw new ArgumentException("The destination is not bound to this identity.", nameof(destination));
        if (!randomHash.IsEmpty && randomHash.Length != RandomHashLength) throw new ArgumentException("Random hash must be 10 bytes.", nameof(randomHash));
        var maximumAppData = Packet.Mtu - Packet.Header1Size - MinPayloadLength - (ratchets is null ? 0 : Identity.RatchetSize);
        if (appData.Length > maximumAppData) throw new ArgumentException("Announce application data exceeds the packet MTU.", nameof(appData));

        var now = unixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var random = randomHash.IsEmpty ? CreateRandomHash(now) : randomHash.ToArray();
        var ratchet = ratchets is null ? [] : ratchets.Rotate(now);
        var publicKey = identity.ExportPublicKey();

        var signed = new byte[Destination.HashLength + publicKey.Length + destination.NameHash.Length + random.Length + ratchet.Length + appData.Length];
        var o = 0;
        Append(signed, ref o, destination.Hash);
        Append(signed, ref o, publicKey);
        Append(signed, ref o, destination.NameHash);
        Append(signed, ref o, random);
        Append(signed, ref o, ratchet);
        Append(signed, ref o, appData);
        var signature = identity.Sign(signed);

        var payload = new byte[publicKey.Length + destination.NameHash.Length + random.Length + ratchet.Length + signature.Length + appData.Length];
        o = 0;
        Append(payload, ref o, publicKey);
        Append(payload, ref o, destination.NameHash);
        Append(payload, ref o, random);
        Append(payload, ref o, ratchet);
        Append(payload, ref o, signature);
        Append(payload, ref o, appData);

        return Packet.Create(
            PacketType.Announce,
            DestinationType.Single,
            destination.Hash,
            payload,
            context: pathResponse ? PathResponseContext : (byte)0,
            contextFlag: ratchet.Length > 0);
    }

    /// <summary>Splits an announce payload per the context flag. Fails only when the payload is too short.</summary>
    public static bool TryParse(Packet packet, [NotNullWhen(true)] out AnnounceData? data)
    {
        ArgumentNullException.ThrowIfNull(packet);
        data = null;
        var p = packet.Payload.Span;
        var ratchetLength = packet.ContextFlag ? Identity.RatchetSize : 0;
        if (p.Length < MinPayloadLength + ratchetLength) return false;

        var ratchetEnd = RandomEnd + ratchetLength;
        var sigEnd = ratchetEnd + SignatureLength;
        // Upstream compares against the non-ratchet minimum when deciding whether app_data is None.
        byte[]? appData = p.Length > MinPayloadLength ? p[sigEnd..].ToArray() : null;
        data = new AnnounceData(
            p[..KeyEnd].ToArray(),
            p[KeyEnd..NameEnd].ToArray(),
            p[NameEnd..RandomEnd].ToArray(),
            ratchetLength > 0 ? p[RandomEnd..ratchetEnd].ToArray() : null,
            p[ratchetEnd..sigEnd].ToArray(),
            appData);
        return true;
    }

    /// <summary>Full upstream <c>validate_announce</c>: signature first, then destination hash = SHA-256(name_hash || identity_hash)[..16].</summary>
    public static AnnounceValidation Validate(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.PacketType != PacketType.Announce) return new(AnnounceStatus.NotAnnounce, null, null);
        if (!TryParse(packet, out var data)) return new(AnnounceStatus.Malformed, null, null);

        Identity identity;
        try { identity = Identity.FromPublicKey(data.PublicKey); }
        catch (ArgumentException) { return new(AnnounceStatus.InvalidPublicKey, data, null); }

        var signed = new byte[Destination.HashLength + data.PublicKey.Length + data.NameHash.Length + data.RandomHash.Length
                              + (data.Ratchet?.Length ?? 0) + (packet.Payload.Length - SignedPrefixLength(packet))];
        var o = 0;
        Append(signed, ref o, packet.DestinationHash.Span);
        Append(signed, ref o, data.PublicKey);
        Append(signed, ref o, data.NameHash);
        Append(signed, ref o, data.RandomHash);
        if (data.Ratchet is not null) Append(signed, ref o, data.Ratchet);
        Append(signed, ref o, packet.Payload.Span[SignedPrefixLength(packet)..]);

        if (!identity.Verify(data.Signature, signed)) return new(AnnounceStatus.InvalidSignature, data, identity);

        var material = new byte[Destination.NameHashLength + Identity.HashSize];
        data.NameHash.CopyTo(material, 0);
        identity.Hash.CopyTo(material, Destination.NameHashLength);
        var expected = SHA256.HashData(material)[..Destination.HashLength];
        return CryptographicOperations.FixedTimeEquals(expected, packet.DestinationHash.Span)
            ? new(AnnounceStatus.Valid, data, identity)
            : new(AnnounceStatus.DestinationMismatch, data, identity);
    }

    private static int SignedPrefixLength(Packet packet) =>
        MinPayloadLength + (packet.ContextFlag ? Identity.RatchetSize : 0);

    private static void WriteTimestamp(Span<byte> destination, long unixSeconds)
    {
        if (unixSeconds < 0 || unixSeconds >= 1L << 40) throw new ArgumentOutOfRangeException(nameof(unixSeconds), "Timestamp must fit in 5 bytes.");
        Span<byte> eight = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(eight, unixSeconds);
        eight[3..].CopyTo(destination);
    }

    private static void Append(byte[] target, ref int offset, ReadOnlySpan<byte> source)
    {
        source.CopyTo(target.AsSpan(offset));
        offset += source.Length;
    }
}
