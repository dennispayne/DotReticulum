using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace DotReticulum.Core;

public enum PacketType : byte { Data = 0, Announce = 1, LinkRequest = 2, Proof = 3 }
public enum PacketHeaderType : byte { Header1 = 0, Header2 = 1 }
public enum DestinationType : byte { Single = 0, Group = 1, Plain = 2, Link = 3 }
public enum TransportType : byte { Broadcast = 0, Transport = 1 }

/// <summary>
/// A validated wire packet. Payload is already encoded/encrypted by the caller;
/// this primitive does not encrypt, route, or segment data.
/// </summary>
/// <remarks>
/// Parsing retains the supplied memory without copying. Its owner must keep it
/// alive and unchanged for the lifetime of this packet and all returned slices.
/// </remarks>
public sealed class Packet
{
    public const int Mtu = 500;
    public const int HashLength = 16;
    public const int Header1Length = 19;
    public const int Header2Length = 35;
    public const int HopLimit = 128;

    private Packet(ReadOnlyMemory<byte> raw) => Raw = raw;

    public ReadOnlyMemory<byte> Raw { get; }
    public byte Flags => Raw.Span[0];
    public byte Hops => Raw.Span[1];
    public PacketHeaderType HeaderType => (PacketHeaderType)((Flags >> 6) & 1);
    public bool ContextFlag => (Flags & 0x20) != 0;
    public TransportType TransportType => (TransportType)((Flags >> 4) & 1);
    public DestinationType DestinationType => (DestinationType)((Flags >> 2) & 3);
    public PacketType Type => (PacketType)(Flags & 3);
    public int HeaderLength => HeaderType == PacketHeaderType.Header2 ? Header2Length : Header1Length;
    public ReadOnlyMemory<byte> TransportId => HeaderType == PacketHeaderType.Header2 ? Raw.Slice(2, HashLength) : ReadOnlyMemory<byte>.Empty;
    public ReadOnlyMemory<byte> DestinationHash => Raw.Slice(HeaderLength - HashLength - 1, HashLength);
    public byte Context => Raw.Span[HeaderLength - 1];
    public ReadOnlyMemory<byte> Payload => Raw[HeaderLength..];

    public static Packet Parse(ReadOnlyMemory<byte> raw)
    {
        if (!TryParse(raw, out var packet))
            throw new FormatException("Malformed packet: invalid length, empty payload, or hop count.");
        return packet;
    }

    public static bool TryParse(ReadOnlyMemory<byte> raw, [NotNullWhen(true)] out Packet? packet)
    {
        packet = null;
        if (raw.Length < Header1Length + 1 || raw.Length > Mtu)
            return false;
        var span = raw.Span;
        var headerLength = (span[0] & 0x40) != 0 ? Header2Length : Header1Length;
        if (span[1] >= HopLimit || raw.Length <= headerLength)
            return false;

        // Upstream ignores bit 7 when decoding flags, but preserves the raw byte.
        packet = new Packet(raw);
        return true;
    }

    public static Packet Create(
        PacketType type, DestinationType destinationType, ReadOnlySpan<byte> destinationHash,
        ReadOnlySpan<byte> payload, byte context = 0, int hops = 0,
        PacketHeaderType headerType = PacketHeaderType.Header1,
        TransportType transportType = TransportType.Broadcast,
        ReadOnlySpan<byte> transportId = default, bool contextFlag = false)
    {
        if ((byte)type > 3) throw new ArgumentOutOfRangeException(nameof(type));
        if ((byte)destinationType > 3) throw new ArgumentOutOfRangeException(nameof(destinationType));
        if ((byte)headerType > 1) throw new ArgumentOutOfRangeException(nameof(headerType));
        if ((byte)transportType > 1) throw new ArgumentOutOfRangeException(nameof(transportType));
        if (hops is < 0 or >= HopLimit) throw new ArgumentOutOfRangeException(nameof(hops));
        if (destinationHash.Length != HashLength)
            throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destinationHash));
        if (headerType == PacketHeaderType.Header2 ? transportId.Length != HashLength : !transportId.IsEmpty)
            throw new ArgumentException("Only header 2 requires a 16-byte transport ID.", nameof(transportId));
        var headerLength = headerType == PacketHeaderType.Header2 ? Header2Length : Header1Length;
        if (payload.IsEmpty || payload.Length > Mtu - headerLength)
            throw new ArgumentException("Payload must be nonempty and fit within the 500-byte MTU.", nameof(payload));

        var raw = new byte[headerLength + payload.Length];
        var flags = ((int)headerType << 6) | (contextFlag ? 0x20 : 0) |
                    ((int)transportType << 4) | ((int)destinationType << 2) | (int)type;
        BinaryPrimitives.WriteUInt16BigEndian(raw, (ushort)((flags << 8) | hops));
        var destinationOffset = 2;
        if (headerType == PacketHeaderType.Header2)
        {
            transportId.CopyTo(raw.AsSpan(2));
            destinationOffset += HashLength;
        }
        destinationHash.CopyTo(raw.AsSpan(destinationOffset));
        raw[headerLength - 1] = context;
        payload.CopyTo(raw.AsSpan(headerLength));
        return new Packet(raw);
    }

    /// <summary>Copies the original wire bytes, including any received reserved bit 7.</summary>
    public int WriteTo(Span<byte> destination)
    {
        if (destination.Length < Raw.Length)
            throw new ArgumentException("Output buffer is too short.", nameof(destination));
        Raw.Span.CopyTo(destination);
        return Raw.Length;
    }

    /// <summary>SHA-256 of the lower flags nibble, destination, context and wire payload.</summary>
    public byte[] CalculateHash()
    {
        Span<byte> material = stackalloc byte[Mtu];
        material[0] = (byte)(Flags & 0x0f);
        var hashable = Raw.Span[(HeaderLength - HashLength - 1)..];
        hashable.CopyTo(material[1..]);
        return SHA256.HashData(material[..(hashable.Length + 1)]);
    }

    public byte[] CalculateTruncatedHash() => CalculateHash()[..HashLength];
}
