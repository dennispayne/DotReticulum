using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Interfaces.Tests;

// Vectors: Fixtures/hdlc-vectors.json, produced by EXECUTING upstream Reticulum's TCP HDLC code
// (markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b, RNS/Interfaces/TCPInterface.py
// sha256 d9a4fcaf6e76a9dcb810d335850ab0398d21054848fd84766a62f687a9ec942d: process_outgoing and read_loop)
// with GenerateHdlcVectors.py. Upstream's frame list is the oracle; DotReticulum additionally drops frames that are not
// parseable Reticulum packets or that contain an invalid escape sequence (see InvalidEscape tests).
[Trait("Category", "Unit")]
public class HdlcFramingTests
{
    private static JsonElement Root { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hdlc-vectors.json"))).RootElement;

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    private static List<byte[]> Decode(IEnumerable<byte[]> chunks, int max = Packet.Mtu)
    {
        var decoder = new HdlcFrameDecoder(max);
        var output = new List<byte[]>();
        foreach (var chunk in chunks)
        {
            decoder.Feed(chunk, output);
        }

        return output;
    }

    private static byte[] Pkt(string name) => Hex(Root.GetProperty("packets").GetProperty(name).GetString()!);

    public static IEnumerable<object[]> EncodeCases() =>
        Root.GetProperty("encode").EnumerateArray().Select(e => new object[] { e.GetProperty("name").GetString()! });

    public static IEnumerable<object[]> DecodeCases() =>
        Root.GetProperty("decode").EnumerateArray().Select(e => new object[] { e.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void EncodeMatchesUpstreamFrame(string name)
    {
        var v = Root.GetProperty("encode").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
        Assert.Equal(Hex(v.GetProperty("frame").GetString()!), HdlcPacketFraming.Encode(Hex(v.GetProperty("packet").GetString()!)));
    }

    [Theory]
    [MemberData(nameof(DecodeCases))]
    public void DecodeMatchesUpstreamFrames(string name)
    {
        var v = Root.GetProperty("decode").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
        var chunks = v.GetProperty("chunks").EnumerateArray().Select(c => Hex(c.GetString()!)).ToList();
        // Upstream hands every length-plausible frame to the packet layer; ours also requires a valid Reticulum packet.
        var expected = v.GetProperty("upstream_frames").EnumerateArray().Select(f => Hex(f.GetString()!))
            .Where(f => Packet.TryParse(f, out _)).ToList();

        var actual = Decode(chunks);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }

        // The same stream split into single bytes must decode identically.
        var split = Decode(chunks.SelectMany(c => c).Select(b => new[] { b }));
        Assert.Equal(actual.Count, split.Count);
        for (var i = 0; i < actual.Count; i++)
        {
            Assert.Equal(actual[i], split[i]);
        }
    }

    [Fact]
    public void KnownCasesProduceExpectedPackets()
    {
        var plain = Pkt("plain");
        var another = Pkt("another");
        Assert.Equal([plain], Decode([Hex(Root.GetProperty("decode").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "single").GetProperty("chunks")[0].GetString()!)]));
        Assert.Equal([Pkt("max")], Decode([HdlcPacketFraming.Encode(Pkt("max"))]));
        Assert.Equal([Pkt("max_escaped")], Decode([HdlcPacketFraming.Encode(Pkt("max_escaped"))]));
        Assert.Equal([plain, another], Decode([HdlcPacketFraming.Encode(plain), HdlcPacketFraming.Encode(another)]));
    }

    [Fact]
    public void OversizeFrameIsDroppedAndNextFrameRecovered()
    {
        var oversize = Pkt("oversize");
        Assert.Equal(501, oversize.Length);
        var plain = Pkt("plain");
        var stream = FrameRaw(oversize).Concat(HdlcPacketFraming.Encode(plain)).ToArray();
        Assert.Equal([plain], Decode([stream]));
    }

    [Fact]
    public void InvalidEscapeDiscardsOnlyThatFrame()
    {
        var plain = Pkt("plain");
        var another = Pkt("another");
        foreach (var bad in new byte[] { 0x41, 0x7D, 0x00, 0x5F })
        {
            var corrupted = new List<byte> { HdlcPacketFraming.Flag };
            corrupted.AddRange(plain.Take(10));
            corrupted.Add(HdlcPacketFraming.Escape);
            corrupted.Add(bad);
            corrupted.AddRange(plain.Skip(10));
            corrupted.Add(HdlcPacketFraming.Flag);
            var result = Decode([[.. corrupted], HdlcPacketFraming.Encode(another)]);
            Assert.Equal([another], result);
        }

        var divergence = Root.GetProperty("divergence")[0];
        Assert.True(divergence.GetProperty("upstream_passes_invalid_escape").GetBoolean());
    }

    [Fact]
    public void FlagInsideEscapeIsTruncatedFrame()
    {
        var plain = Pkt("plain");
        var another = Pkt("another");
        var stream = new List<byte> { HdlcPacketFraming.Flag };
        stream.AddRange(plain.Take(10));
        stream.Add(HdlcPacketFraming.Escape);
        stream.AddRange(HdlcPacketFraming.Encode(another));
        Assert.Equal([another], Decode([[.. stream]]));
    }

    [Fact]
    public void EncodeRejectsInvalidPackets()
    {
        Assert.Throws<ArgumentException>(() => HdlcPacketFraming.Encode(ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => HdlcPacketFraming.Encode(new byte[19]));
        Assert.Throws<ArgumentException>(() => HdlcPacketFraming.Encode(Pkt("oversize")));
    }

    [Fact]
    public void EncodeThenDecodeRoundTripsEveryByteValueInPayload()
    {
        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var packet = Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], payload).ToArray();
        Assert.Equal([packet], Decode([HdlcPacketFraming.Encode(packet)]));
    }

    [Fact]
    public void DecoderBytesBeforeFirstFlagAreIgnored()
    {
        var plain = Pkt("plain");
        Assert.Equal([plain], Decode([[0x01, 0x7D, 0x7D, 0x55], HdlcPacketFraming.Encode(plain)]));
    }

    [Fact]
    public void FuzzRandomBytesNeverThrowAndOnlyYieldValidPackets()
    {
        var rng = new Random(0xC0FFEE);
        for (var round = 0; round < 300; round++)
        {
            var data = new byte[rng.Next(0, 4000)];
            rng.NextBytes(data);
            // Bias towards framing bytes so escapes and flags are exercised heavily.
            for (var i = 0; i < data.Length; i++)
            {
                var r = rng.Next(8);
                if (r == 0) data[i] = HdlcPacketFraming.Flag;
                else if (r == 1) data[i] = HdlcPacketFraming.Escape;
                else if (r == 2) data[i] = 0x5E;
            }

            var decoder = new HdlcFrameDecoder();
            var output = new List<byte[]>();
            for (var offset = 0; offset < data.Length;)
            {
                var n = Math.Min(rng.Next(1, 64), data.Length - offset);
                decoder.Feed(data.AsSpan(offset, n), output);
                offset += n;
            }

            Assert.All(output, p =>
            {
                Assert.InRange(p.Length, 1, Packet.Mtu);
                Assert.True(Packet.TryParse(p, out _));
            });
        }
    }

    [Fact]
    public void FuzzValidFramesSurviveGarbageAndArbitrarySplits()
    {
        var rng = new Random(1234);
        for (var round = 0; round < 100; round++)
        {
            var expected = new List<byte[]>();
            var stream = new List<byte>();
            for (var i = 0; i < rng.Next(1, 12); i++)
            {
                var payload = new byte[rng.Next(1, 481)];
                rng.NextBytes(payload);
                var packet = Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], payload).ToArray();
                expected.Add(packet);
                var garbage = new byte[rng.Next(0, 40)];
                rng.NextBytes(garbage);
                stream.AddRange(garbage);
                stream.AddRange(HdlcPacketFraming.Encode(packet));
            }

            var decoder = new HdlcFrameDecoder();
            var output = new List<byte[]>();
            var bytes = stream.ToArray();
            for (var offset = 0; offset < bytes.Length;)
            {
                var n = Math.Min(rng.Next(1, 200), bytes.Length - offset);
                decoder.Feed(bytes.AsSpan(offset, n), output);
                offset += n;
            }

            // Garbage may occasionally also parse as a packet; every real packet must still appear in order.
            var next = 0;
            foreach (var o in output)
            {
                if (next < expected.Count && o.AsSpan().SequenceEqual(expected[next]))
                {
                    next++;
                }
            }

            Assert.Equal(expected.Count, next);
        }
    }

    private static byte[] FrameRaw(byte[] raw)
    {
        // Frames without the validity check of Encode, to feed oversize frames to the decoder.
        var output = new List<byte> { HdlcPacketFraming.Flag };
        foreach (var b in raw)
        {
            if (b is HdlcPacketFraming.Flag or HdlcPacketFraming.Escape)
            {
                output.Add(HdlcPacketFraming.Escape);
                output.Add((byte)(b ^ HdlcPacketFraming.EscapeMask));
            }
            else
            {
                output.Add(b);
            }
        }

        output.Add(HdlcPacketFraming.Flag);
        return [.. output];
    }
}
