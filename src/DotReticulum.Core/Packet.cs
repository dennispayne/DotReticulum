using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace DotReticulum.Core;

public enum HeaderType : byte { Header1 = 0, Header2 = 1 }

/// <summary>The single transport bit of the flags byte (upstream BROADCAST = 0, TRANSPORT = 1).</summary>
public enum TransportType : byte { Broadcast = 0, Transport = 1 }

public enum DestinationType : byte { Single = 0, Group = 1, Plain = 2, Link = 3 }

public enum PacketType : byte { Data = 0, Announce = 1, LinkRequest = 2, Proof = 3 }

/// <summary>
/// Reticulum packet wire codec. Layout: flags(1) hops(1) [transport_id(16) if header 2] destination_hash(16) context(1) payload.
/// Flags (high to low): reserved(1) header_type(1) context_flag(1) transport_type(1) destination_type(2) packet_type(2).
/// This is a codec only: no encryption, routing or segmentation. Instances are immutable; fields of a parsed
/// packet are zero-copy slices of the buffer passed to <see cref="Parse"/>, which must not be modified afterwards.
/// </summary>
public sealed class Packet
{
    public const int Mtu = 500;
    public const int HopLimit = 128;
    public const int HashLength = 16;
    public const int Header1Size = 19;
    public const int Header2Size = 35;

    private Packet(bool reserved, HeaderType headerType, bool contextFlag, TransportType transportType,
        DestinationType destinationType, PacketType packetType, byte hops, ReadOnlyMemory<byte> transportId,
        ReadOnlyMemory<byte> destinationHash, byte context, ReadOnlyMemory<byte> payload)
    {
        ReservedBit = reserved;
        HeaderType = headerType;
        ContextFlag = contextFlag;
        TransportType = transportType;
        DestinationType = destinationType;
        PacketType = packetType;
        Hops = hops;
        TransportId = transportId;
        DestinationHash = destinationHash;
        Context = context;
        Payload = payload;
    }

    /// <summary>Bit 7 of the flags byte. Accepted and preserved on re-emit, but excluded from the packet hash.</summary>
    public bool ReservedBit { get; }
    public HeaderType HeaderType { get; }
    public bool ContextFlag { get; }
    public TransportType TransportType { get; }
    public DestinationType DestinationType { get; }
    public PacketType PacketType { get; }
    public byte Hops { get; }

    /// <summary>16 bytes for header 2; empty for header 1.</summary>
    public ReadOnlyMemory<byte> TransportId { get; }
    public ReadOnlyMemory<byte> DestinationHash { get; }
    public byte Context { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public byte Flags =>
        (byte)((ReservedBit ? 0x80 : 0) | ((int)HeaderType << 6) | (ContextFlag ? 0x20 : 0) |
               ((int)TransportType << 4) | ((int)DestinationType << 2) | (int)PacketType);

    public int HeaderSize => HeaderType == HeaderType.Header2 ? Header2Size : Header1Size;

    public int Length => HeaderSize + Payload.Length;

    public static Packet Create(
        PacketType packetType,
        DestinationType destinationType,
        ReadOnlyMemory<byte> destinationHash,
        ReadOnlyMemory<byte> payload,
        byte context = 0,
        HeaderType headerType = HeaderType.Header1,
        ReadOnlyMemory<byte> transportId = default,
        TransportType transportType = TransportType.Broadcast,
        bool contextFlag = false,
        byte hops = 0,
        bool reservedBit = false,
        int mtu = Mtu)
    {
        if (!Enum.IsDefined(packetType)) throw new ArgumentOutOfRangeException(nameof(packetType));
        if (!Enum.IsDefined(destinationType)) throw new ArgumentOutOfRangeException(nameof(destinationType));
        if (!Enum.IsDefined(headerType)) throw new ArgumentOutOfRangeException(nameof(headerType));
        if (!Enum.IsDefined(transportType)) throw new ArgumentOutOfRangeException(nameof(transportType));
        if (hops >= HopLimit) throw new ArgumentOutOfRangeException(nameof(hops), $"Hop count must be below {HopLimit}.");
        if (destinationHash.Length != HashLength) throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destinationHash));
        if (payload.IsEmpty) throw new ArgumentException("Payload must not be empty.", nameof(payload));
        if (headerType == HeaderType.Header2)
        {
            if (transportId.Length != HashLength) throw new ArgumentException("Header 2 requires a 16-byte transport ID.", nameof(transportId));
        }
        else if (!transportId.IsEmpty)
        {
            throw new ArgumentException("Header 1 packets cannot carry a transport ID.", nameof(transportId));
        }

        var packet = new Packet(reservedBit, headerType, contextFlag, transportType, destinationType, packetType,
            hops, transportId, destinationHash, context, payload);
        if (packet.Length > mtu) throw new ArgumentException($"Packet size of {packet.Length} exceeds MTU of {mtu} bytes.", nameof(payload));
        return packet;
    }

    /// <summary>Parses a packet (zero-copy). Throws <see cref="FormatException"/> for malformed input.</summary>
    public static Packet Parse(ReadOnlyMemory<byte> raw, int mtu = Mtu) =>
        TryParse(raw, out var packet, mtu) ? packet : throw new FormatException("Malformed Reticulum packet.");

    /// <summary>
    /// Rejects: hops &gt;= 128, truncated headers, empty payloads and packets longer than <paramref name="mtu"/>
    /// (upstream's per-link MTU may exceed 500 on discovered links; pass it explicitly then).
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> raw, [NotNullWhen(true)] out Packet? packet, int mtu = Mtu)
    {
        packet = null;
        var span = raw.Span;
        if (span.Length < 2 || span.Length > mtu) return false;

        var flags = span[0];
        var hops = span[1];
        if (hops >= HopLimit) return false;

        var headerType = (HeaderType)((flags >> 6) & 1);
        var headerSize = headerType == HeaderType.Header2 ? Header2Size : Header1Size;
        if (span.Length <= headerSize) return false; // header complete but no payload byte

        var offset = 2;
        ReadOnlyMemory<byte> transportId = default;
        if (headerType == HeaderType.Header2)
        {
            transportId = raw.Slice(offset, HashLength);
            offset += HashLength;
        }
        var destinationHash = raw.Slice(offset, HashLength);
        offset += HashLength;
        var context = span[offset++];

        packet = new Packet(
            (flags & 0x80) != 0,
            headerType,
            (flags & 0x20) != 0,
            (TransportType)((flags >> 4) & 1),
            (DestinationType)((flags >> 2) & 3),
            (PacketType)(flags & 3),
            hops, transportId, destinationHash, context, raw[offset..]);
        return true;
    }

    /// <summary>Serialises into <paramref name="destination"/>; returns bytes written. Throws if the buffer is too small.</summary>
    public int WriteTo(Span<byte> destination)
    {
        var length = Length;
        if (destination.Length < length) throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        destination[0] = Flags;
        destination[1] = Hops;
        var offset = 2;
        if (HeaderType == HeaderType.Header2)
        {
            TransportId.Span.CopyTo(destination[offset..]);
            offset += HashLength;
        }
        DestinationHash.Span.CopyTo(destination[offset..]);
        offset += HashLength;
        destination[offset++] = Context;
        Payload.Span.CopyTo(destination[offset..]);
        return length;
    }

    public byte[] ToArray()
    {
        var buffer = new byte[Length];
        WriteTo(buffer);
        return buffer;
    }

    /// <summary>SHA-256 over (flags &amp; 0x0f) || destination hash || context || payload (excludes hops and transport ID).</summary>
    public byte[] CalculateHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([(byte)(Flags & 0x0f)]);
        hash.AppendData(DestinationHash.Span);
        hash.AppendData([Context]);
        hash.AppendData(Payload.Span);
        return hash.GetHashAndReset();
    }

    public byte[] CalculateTruncatedHash() => CalculateHash()[..HashLength];
}
