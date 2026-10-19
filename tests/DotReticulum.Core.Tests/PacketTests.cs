using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Core.Tests;

// Vectors: Fixtures/packet-vectors.json, produced by EXECUTING upstream Reticulum
// (markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b, RNS/Packet.py: pack/unpack/get_hash/getTruncatedHash)
// with GeneratePacketVectors.py. Upstream validity is recorded per vector (`upstream_valid`).
// Intentional hardening versus upstream unpack(): packets longer than the MTU (500) are rejected unless a larger
// per-link MTU is passed explicitly; upstream's unpack() does not itself check length.
public class PacketTests
{
    private static JsonElement Root { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "packet-vectors.json"))).RootElement;

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    private static List<JsonElement> Section(string name) => Root.GetProperty(name).EnumerateArray().ToList();

    private static void AssertMatchesUpstream(JsonElement v, Packet p)
    {
        Assert.Equal(v.GetProperty("flags").GetInt32() & 0x7f, p.Flags & 0x7f);
        Assert.Equal(v.GetProperty("hops").GetInt32(), p.Hops);
        Assert.Equal(v.GetProperty("header_type").GetInt32(), (int)p.HeaderType);
        Assert.Equal(v.GetProperty("context_flag").GetInt32() == 1, p.ContextFlag);
        Assert.Equal(v.GetProperty("transport_type").GetInt32(), (int)p.TransportType);
        Assert.Equal(v.GetProperty("destination_type").GetInt32(), (int)p.DestinationType);
        Assert.Equal(v.GetProperty("packet_type").GetInt32(), (int)p.PacketType);
        Assert.Equal(Hex(v, "transport_id"), p.TransportId.ToArray());
        Assert.Equal(Hex(v, "destination_hash"), p.DestinationHash.ToArray());
        Assert.Equal(v.GetProperty("context").GetInt32(), p.Context);
        Assert.Equal(Hex(v, "payload"), p.Payload.ToArray());
        Assert.Equal(Hex(v, "hash"), p.CalculateHash());
        Assert.Equal(Hex(v, "truncated_hash"), p.CalculateTruncatedHash());
    }

    [Fact]
    public void Constants()
    {
        Assert.Equal(500, Packet.Mtu);
        Assert.Equal(128, Packet.HopLimit);
        Assert.Equal(19, Packet.Header1Size);
        Assert.Equal(35, Packet.Header2Size);
    }

    [Fact]
    public void All128FlagCombinationsMatchUpstreamAndRoundTrip()
    {
        var vectors = Section("all_flags");
        Assert.Equal(128, vectors.Count);
        var header2 = 0;
        foreach (var v in vectors)
        {
            var raw = Hex(v, "raw");
            var p = Packet.Parse(raw);
            AssertMatchesUpstream(v, p);
            if (p.HeaderType == HeaderType.Header2) header2++;
            Assert.Equal(raw, p.ToArray());

            // Rebuild from fields (Create) and compare the wire bytes.
            var rebuilt = Packet.Create(p.PacketType, p.DestinationType, p.DestinationHash, p.Payload, p.Context, p.HeaderType,
                p.TransportId, p.TransportType, p.ContextFlag, p.Hops);
            Assert.Equal(raw, rebuilt.ToArray());
            Assert.Equal(p.CalculateHash(), rebuilt.CalculateHash());
        }
        Assert.Equal(64, header2);
        Assert.Equal(80, vectors.Count(v => v.GetProperty("upstream_packed").GetBoolean()));
    }

    [Fact]
    public void CanonicalUpstreamPackets()
    {
        var vectors = Section("canonical");
        Assert.Equal(8, vectors.Count);
        foreach (var v in vectors)
        {
            var raw = Hex(v, "raw");
            var p = Packet.Parse(raw);
            AssertMatchesUpstream(v, p);
            Assert.Equal(raw, p.ToArray());
            Assert.Equal(raw.Length, p.Length);
        }
    }

    [Fact]
    public void HashIgnoresHopsTransportIdAndHighFlagBits()
    {
        foreach (var v in Section("canonical"))
        {
            var raw = Hex(v, "raw");
            var original = Packet.Parse(raw).CalculateHash();

            var changedHops = (byte[])raw.Clone();
            changedHops[1] ^= 0x55;
            changedHops[1] &= 0x7f;
            Assert.Equal(original, Packet.Parse(changedHops).CalculateHash());

            // Context flag (bit 5), transport type (bit 4) and the reserved bit (bit 7) are outside the hashed nibble.
            var changedFlags = (byte[])raw.Clone();
            changedFlags[0] ^= 0xb0;
            Assert.Equal(original, Packet.Parse(changedFlags).CalculateHash());

            var changedPayload = (byte[])raw.Clone();
            changedPayload[^1] ^= 1;
            Assert.NotEqual(original, Packet.Parse(changedPayload).CalculateHash());
        }
    }

    [Fact]
    public void ReservedBitIsAcceptedPreservedAndExcludedFromHash()
    {
        var vectors = Section("reserved_bit");
        Assert.NotEmpty(vectors);
        foreach (var v in vectors)
        {
            var raw = Hex(v, "raw");
            Assert.True(v.GetProperty("upstream_valid").GetBoolean());
            var p = Packet.Parse(raw);
            Assert.True(p.ReservedBit);
            Assert.Equal(raw[0], p.Flags);
            Assert.Equal(raw, p.ToArray());
            Assert.Equal(Hex(v, "hash"), p.CalculateHash());
            Assert.Equal(Hex(v, "truncated_hash"), p.CalculateTruncatedHash());

            var without = (byte[])raw.Clone();
            without[0] &= 0x7f;
            Assert.False(Packet.Parse(without).ReservedBit);
            Assert.Equal(p.CalculateHash(), Packet.Parse(without).CalculateHash());
        }
    }

    [Fact]
    public void HopBoundaries()
    {
        foreach (var v in Section("hops"))
        {
            var raw = Hex(v, "raw");
            var valid = v.GetProperty("upstream_valid").GetBoolean();
            Assert.Equal(valid, Packet.TryParse(raw, out _));
            Assert.Equal(raw[1] < 128, valid);
            if (!valid) Assert.Throws<FormatException>(() => Packet.Parse(raw));
        }
        var dest = new byte[16];
        Assert.Equal(127, Packet.Create(PacketType.Data, DestinationType.Single, dest, new byte[] { 1 }, hops: 127).Hops);
        Assert.Throws<ArgumentOutOfRangeException>(() => Packet.Create(PacketType.Data, DestinationType.Single, dest, new byte[] { 1 }, hops: 128));
        Assert.Throws<ArgumentOutOfRangeException>(() => Packet.Create(PacketType.Data, DestinationType.Single, dest, new byte[] { 1 }, hops: 255));
    }

    [Fact]
    public void MtuBoundaries()
    {
        foreach (var v in Section("mtu"))
        {
            var raw = Hex(v, "raw");
            var total = v.GetProperty("total_length").GetInt32();
            Assert.Equal(total, raw.Length);
            // Upstream unpack() accepts all three sizes; this codec additionally enforces the MTU by default.
            Assert.True(v.GetProperty("upstream_valid").GetBoolean());
            Assert.Equal(total <= 500, Packet.TryParse(raw, out _));
            Assert.True(Packet.TryParse(raw, out var p, mtu: 1000));
            AssertMatchesUpstream(v, p!);
            // Upstream pack() succeeded exactly when the header-1 packet fit in the MTU.
            if (raw[0] == 0x00) Assert.Equal(total <= 500, v.GetProperty("upstream_packed").GetBoolean());
        }

        var dest = new byte[16];
        var tid = new byte[16];
        Assert.Equal(500, Packet.Create(PacketType.Data, DestinationType.Single, dest, new byte[481]).Length);
        Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Single, dest, new byte[482]));
        Assert.Equal(500, Packet.Create(PacketType.Data, DestinationType.Single, dest, new byte[465], headerType: HeaderType.Header2, transportId: tid).Length);
        Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Single, dest, new byte[466], headerType: HeaderType.Header2, transportId: tid));
        Assert.Equal(1000, Packet.Create(PacketType.Data, DestinationType.Link, dest, new byte[981], mtu: 1000).Length);
    }

    [Fact]
    public void EveryTruncationMatchesUpstream()
    {
        var vectors = Section("truncation");
        Assert.True(vectors.Count > 50);
        var validCount = 0;
        foreach (var v in vectors)
        {
            var raw = Hex(v, "raw");
            var upstreamValid = v.GetProperty("upstream_valid").GetBoolean();
            Assert.Equal(upstreamValid, Packet.TryParse(raw, out var p));
            if (upstreamValid)
            {
                validCount++;
                AssertMatchesUpstream(v, p!);
            }
            else
            {
                Assert.Throws<FormatException>(() => Packet.Parse(raw));
            }
        }
        // 12-byte payloads: every prefix with at least one payload byte is valid (12 per header type), header-only prefixes are not.
        Assert.Equal(24, validCount);
    }

    [Fact]
    public void HeaderOnlyAndEmptyPayloadAreInvalid()
    {
        Assert.False(Packet.TryParse(ReadOnlyMemory<byte>.Empty, out _));
        Assert.False(Packet.TryParse(new byte[19], out _));
        Assert.True(Packet.TryParse(new byte[20], out _));
        var h2Header = new byte[35];
        h2Header[0] = 0x40;
        Assert.False(Packet.TryParse(h2Header, out _));
        Assert.True(Packet.TryParse(h2Header.Append((byte)0).ToArray(), out _));
        Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void ParseIsZeroCopy()
    {
        var raw = Hex(Section("canonical")[0], "raw");
        var p = Packet.Parse(raw);
        ref var payloadStart = ref MemoryMarshal.GetReference(p.Payload.Span);
        Assert.True(Unsafe.AreSame(ref payloadStart, ref raw[19]));
        ref var destStart = ref MemoryMarshal.GetReference(p.DestinationHash.Span);
        Assert.True(Unsafe.AreSame(ref destStart, ref raw[2]));
    }

    [Fact]
    public void CreateValidatesArguments()
    {
        var dest = new byte[16];
        var payload = new byte[] { 1 };
        Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Single, new byte[15], payload));
        Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Single, new byte[17], payload));
        Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Single, dest, payload, headerType: HeaderType.Header2));
        Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Single, dest, payload, headerType: HeaderType.Header2, transportId: new byte[15]));
        Assert.Throws<ArgumentException>(() => Packet.Create(PacketType.Data, DestinationType.Single, dest, payload, transportId: new byte[16]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Packet.Create((PacketType)4, DestinationType.Single, dest, payload));
        Assert.Throws<ArgumentOutOfRangeException>(() => Packet.Create(PacketType.Data, (DestinationType)4, dest, payload));
    }

    [Fact]
    public void WriteToRejectsShortBuffersAndReturnsLength()
    {
        var raw = Hex(Section("canonical")[4], "raw");
        var p = Packet.Parse(raw);
        Assert.Throws<ArgumentException>(() => p.WriteTo(new byte[raw.Length - 1]));
        var buffer = new byte[raw.Length + 10];
        Assert.Equal(raw.Length, p.WriteTo(buffer));
        Assert.Equal(raw, buffer[..raw.Length]);
    }
}
