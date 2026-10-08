using System.Runtime.InteropServices;

namespace DotReticulum.Core.Tests;

public class PacketTests
{
    private static readonly byte[] Address = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Transport = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Data = Convert.FromHexString("00ff807265746963756c756d");

    // Executed upstream Packet.pack(), unpack(), and get_hash() at revision
    // e40191b3d193b46b7f2d8a44424a594cd758839b. Reproduce with GenerateUpstreamVectors.py.
    [Theory]
    [InlineData("007f000102030405060708090a0b0c0d0e0f0e00ff807265746963756c756d", "6c0a3ed722998ef1e4379aedf260606599a737abb595e11dc5999817cc58012f")]
    [InlineData("257f000102030405060708090a0b0c0d0e0f0e00ff807265746963756c756d", "0bdebd8829fe9970edb7c8f7830de24ab6e216a8567f45a45161ccacb7f477bb")]
    [InlineData("0a7f000102030405060708090a0b0c0d0e0f0e00ff807265746963756c756d", "c5a1d936b31b765150d66ef52196613357014550534f9dba133e0eef181ebbff")]
    [InlineData("2f7f000102030405060708090a0b0c0d0e0f0e00ff807265746963756c756d", "40a46d6f09f5b1d8f73eb3246071effc8fba04f583b1721cf0e5811bf415c721")]
    [InlineData("507f101112131415161718191a1b1c1d1e1f000102030405060708090a0b0c0d0e0f0e00ff807265746963756c756d", "6c0a3ed722998ef1e4379aedf260606599a737abb595e11dc5999817cc58012f")]
    [InlineData("757f101112131415161718191a1b1c1d1e1f000102030405060708090a0b0c0d0e0f0e00ff807265746963756c756d", "0bdebd8829fe9970edb7c8f7830de24ab6e216a8567f45a45161ccacb7f477bb")]
    [InlineData("5a7f101112131415161718191a1b1c1d1e1f000102030405060708090a0b0c0d0e0f0e00ff807265746963756c756d", "c5a1d936b31b765150d66ef52196613357014550534f9dba133e0eef181ebbff")]
    [InlineData("7f7f101112131415161718191a1b1c1d1e1f000102030405060708090a0b0c0d0e0f0e00ff807265746963756c756d", "40a46d6f09f5b1d8f73eb3246071effc8fba04f583b1721cf0e5811bf415c721")]
    public void MatchesGenuineUpstreamWireAndHashes(string wireHex, string hashHex)
    {
        var raw = Convert.FromHexString(wireHex);
        var parsed = Packet.Parse(raw);
        Assert.Equal(Convert.FromHexString(hashHex), parsed.CalculateHash());
        Assert.Equal(Convert.FromHexString(hashHex)[..16], parsed.CalculateTruncatedHash());
        var packed = Packet.Create(parsed.Type, parsed.DestinationType, Address, Data,
            parsed.Context, parsed.Hops, parsed.HeaderType, parsed.TransportType,
            parsed.TransportId.Span, parsed.ContextFlag);
        Assert.Equal(raw, packed.Raw.ToArray());
        Assert.Equal(Address, parsed.DestinationHash.ToArray());
        Assert.Equal(Data, parsed.Payload.ToArray());
        Assert.Equal(0x0e, parsed.Context);
        Assert.Equal(127, parsed.Hops);
    }

    public static IEnumerable<object[]> FlagCombinations()
    {
        for (var flags = 0; flags < 128; flags++)
            yield return new object[] { flags };
    }

    [Theory]
    [MemberData(nameof(FlagCombinations))]
    public void PacksAllTypeDestinationHeaderTransportAndContextFlagCombinations(int flags)
    {
        var header = (PacketHeaderType)((flags >> 6) & 1);
        var packet = Packet.Create((PacketType)(flags & 3), (DestinationType)((flags >> 2) & 3),
            Address, Data, context: 0xf0, headerType: header,
            transportType: (TransportType)((flags >> 4) & 1),
            transportId: header == PacketHeaderType.Header2 ? Transport : [],
            contextFlag: (flags & 0x20) != 0);
        Assert.Equal(flags, packet.Flags);
        var parsed = Packet.Parse(packet.Raw);
        Assert.Equal(packet.Type, parsed.Type);
        Assert.Equal(packet.DestinationType, parsed.DestinationType);
        Assert.Equal(header, parsed.HeaderType);
        Assert.Equal(packet.TransportType, parsed.TransportType);
        Assert.Equal(packet.ContextFlag, parsed.ContextFlag);
        Assert.Equal(0xf0, parsed.Context); // Unknown contexts remain opaque on the wire.
        Assert.Equal(header == PacketHeaderType.Header2 ? Transport : [], parsed.TransportId.ToArray());
        Assert.Equal(packet.CalculateHash(), parsed.CalculateHash());
    }

    [Fact]
    public void ReservedBitSevenIsAcceptedPreservedAndExcludedFromHash()
    {
        foreach (var flags in Enumerable.Range(0, 128))
        {
            var raw = new byte[((flags & 0x40) != 0 ? Packet.Header2Length : Packet.Header1Length) + 1];
            raw[0] = (byte)flags;
            raw[^1] = 1;
            var normal = Packet.Parse(raw.ToArray());
            raw[0] |= 0x80;
            var reserved = Packet.Parse(raw);
            Assert.Equal(normal.Type, reserved.Type);
            Assert.Equal(normal.DestinationType, reserved.DestinationType);
            Assert.Equal(normal.HeaderType, reserved.HeaderType);
            Assert.Equal(normal.ContextFlag, reserved.ContextFlag);
            Assert.Equal(normal.TransportType, reserved.TransportType);
            Assert.Equal(normal.CalculateHash(), reserved.CalculateHash());
            var copied = new byte[raw.Length];
            Assert.Equal(raw.Length, reserved.WriteTo(copied));
            Assert.Equal(raw, copied);
        }
    }

    [Theory]
    [InlineData(PacketHeaderType.Header1)]
    [InlineData(PacketHeaderType.Header2)]
    public void RejectsEveryTruncatedHeaderAndEmptyPayload(PacketHeaderType header)
    {
        var minimum = header == PacketHeaderType.Header1 ? Packet.Header1Length : Packet.Header2Length;
        for (var length = 0; length <= minimum; length++)
        {
            var raw = new byte[length];
            if (length > 0) raw[0] = (byte)((int)header << 6);
            Assert.False(Packet.TryParse(raw, out var packet));
            Assert.Null(packet);
            Assert.Throws<FormatException>(() => Packet.Parse(raw));
        }
    }

    [Theory]
    [InlineData(PacketHeaderType.Header1)]
    [InlineData(PacketHeaderType.Header2)]
    public void EnforcesExactMtuBoundaryForBothHeaders(PacketHeaderType header)
    {
        var headerLength = header == PacketHeaderType.Header1 ? Packet.Header1Length : Packet.Header2Length;
        var transportId = header == PacketHeaderType.Header2 ? Transport : [];
        var packet = Packet.Create(PacketType.Data, DestinationType.Plain, Address,
            new byte[Packet.Mtu - headerLength], headerType: header, transportId: transportId);
        Assert.Equal(500, packet.Raw.Length);
        Assert.True(Packet.TryParse(packet.Raw, out _));
        var oversized = new byte[501];
        packet.Raw.Span.CopyTo(oversized);
        Assert.False(Packet.TryParse(oversized, out _));
        Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Plain,
            Address, new byte[Packet.Mtu - headerLength + 1], headerType: header, transportId: transportId));
        Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Plain,
            Address, [], headerType: header, transportId: transportId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    public void AcceptsValidHopBoundary(int hops)
    {
        var packet = Packet.Create(PacketType.Data, DestinationType.Plain, Address, Data, hops: hops);
        Assert.Equal(hops, Packet.Parse(packet.Raw).Hops);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(255)]
    public void RejectsInvalidWireHopCounts(int hops)
    {
        foreach (var header in Enum.GetValues<PacketHeaderType>())
        {
            var raw = Packet.Create(PacketType.Data, DestinationType.Plain, Address, Data,
                headerType: header, transportId: header == PacketHeaderType.Header2 ? Transport : []).Raw.ToArray();
            raw[1] = (byte)hops;
            Assert.False(Packet.TryParse(raw, out _));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(256)]
    public void RejectsInvalidOutboundHops(int hops) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Packet.Create(PacketType.Data, DestinationType.Plain, Address, Data, hops: hops));

    [Fact]
    public void RejectsMalformedFieldsAndInvalidEnums()
    {
        foreach (var length in new[] { 0, 15, 17 })
        {
            Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Plain,
                new byte[length], Data));
            Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Plain,
                Address, Data, headerType: PacketHeaderType.Header2, transportId: new byte[length]));
        }
        Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Plain,
            Address, Data, transportId: Transport));
        Assert.Throws<ArgumentOutOfRangeException>(() => Packet.Create((PacketType)4, DestinationType.Plain, Address, Data));
        Assert.Throws<ArgumentOutOfRangeException>(() => Packet.Create(PacketType.Data, (DestinationType)4, Address, Data));
        Assert.Throws<ArgumentOutOfRangeException>(() => Packet.Create(PacketType.Data, DestinationType.Plain, Address, Data,
            headerType: (PacketHeaderType)2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Packet.Create(PacketType.Data, DestinationType.Plain, Address, Data,
            transportType: (TransportType)2));
    }

    [Fact]
    public void ParsingSlicesCallerMemoryWithoutCopies()
    {
        var wire = Packet.Create(PacketType.Proof, DestinationType.Link, Address, Data,
            headerType: PacketHeaderType.Header2, transportId: Transport).Raw.ToArray();
        var storage = new byte[wire.Length + 12];
        wire.CopyTo(storage, 7);
        var packet = Packet.Parse(storage.AsMemory(7, wire.Length));
        foreach (var memory in new[] { packet.Raw, packet.TransportId, packet.DestinationHash, packet.Payload })
        {
            Assert.True(MemoryMarshal.TryGetArray(memory, out var segment));
            Assert.Same(storage, segment.Array);
        }
        Assert.Equal(7 + Packet.Header2Length, GetOffset(packet.Payload));
        Assert.Equal(wire, packet.Raw.ToArray());
        Assert.Throws<ArgumentException>(() => packet.WriteTo(new byte[wire.Length - 1]));
    }

    private static int GetOffset(ReadOnlyMemory<byte> memory)
    {
        Assert.True(MemoryMarshal.TryGetArray(memory, out var segment));
        return segment.Offset;
    }

    [Fact]
    public void HashExcludesRoutingFieldsButIncludesAllContentFields()
    {
        var packet = Packet.Create(PacketType.Data, DestinationType.Plain, Address, Data, context: 0x0e);
        var baseline = packet.CalculateHash();
        var transported = Packet.Create(PacketType.Data, DestinationType.Plain, Address, Data, context: 0x0e,
            hops: 127, headerType: PacketHeaderType.Header2, transportType: TransportType.Transport,
            transportId: Transport, contextFlag: true);
        Assert.Equal(baseline, transported.CalculateHash());
        var raw = transported.Raw.ToArray();
        raw[2] ^= 1;
        Assert.Equal(baseline, Packet.Parse(raw).CalculateHash());
        foreach (var offset in new[] { 0, 18, 34, 35, raw.Length - 1 })
        {
            var changed = transported.Raw.ToArray();
            changed[offset] ^= 1;
            Assert.NotEqual(baseline, Packet.Parse(changed).CalculateHash());
        }
        raw = transported.Raw.ToArray();
        raw[0] ^= 4; // Destination type is also part of the hashed lower nibble.
        Assert.NotEqual(baseline, Packet.Parse(raw).CalculateHash());
    }
}
