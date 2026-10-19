using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// Vectors produced by executing upstream RNS.Resource / ResourceAdvertisement (see GenerateResourceVectors.py): hashes, proof, bz2 stream,
// part split, map hashes, part packets and the msgpack advertisement must match byte for byte.
public class ResourceVectorTests
{
    private static readonly JsonElement Root =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "resource-vectors.json"))).RootElement;

    public static IEnumerable<object[]> Labels() => Root.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("label").GetString()! });

    private static JsonElement Case(string label) => Root.GetProperty("cases").EnumerateArray().First(c => c.GetProperty("label").GetString() == label);

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    [Fact]
    public void Constants_MatchUpstream()
    {
        Assert.Equal(Root.GetProperty("hashmapMaxLen").GetInt32(), ResourceAdvertisement.HashmapMaxLength);
        Assert.Equal(Root.GetProperty("collisionGuardSize").GetInt32(), ResourceAdvertisement.CollisionGuardSize);
        Assert.Equal(Packet.Mtu - 35 - 1, Root.GetProperty("sdu").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void HashesProofAndStream_MatchUpstream(string label)
    {
        var c = Case(label);
        var hashed = Hex(c, "hashedData");
        var random = Hex(c, "randomHash");
        Assert.Equal(Hex(c, "hash"), Resource.ComputeHash(hashed, random));
        Assert.Equal(Hex(c, "expectedProof"), Resource.ComputeExpectedProof(hashed, Hex(c, "hash")));

        var stream = Hex(c, "streamPlain");
        var body = stream.AsSpan(4);
        var inflated = c.GetProperty("compressed").GetBoolean() ? Bz2.Decompress(body.ToArray(), 1 << 26) : body.ToArray();
        Assert.Equal(hashed, inflated);

        // Our compressor must take the same decision as upstream and produce something upstream can read (covered live and in Bz2 vectors)
        var ours = Bz2.Compress(hashed);
        Assert.Equal(c.GetProperty("compressed").GetBoolean(), c.GetProperty("autoCompress").GetBoolean() && ours.Length < hashed.Length);
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void PartsMapHashesAndPartPackets_MatchUpstream(string label)
    {
        var c = Case(label);
        var random = Hex(c, "randomHash");
        var linkId = Convert.FromHexString(Root.GetProperty("linkId").GetString()!);
        var hashmap = Hex(c, "hashmap");
        var parts = c.GetProperty("parts").EnumerateArray().Select(p => Convert.FromHexString(p.GetString()!)).ToArray();
        var raws = c.GetProperty("partPacketRaw").EnumerateArray().Select(p => Convert.FromHexString(p.GetString()!)).ToArray();
        Assert.Equal(c.GetProperty("totalParts").GetInt32(), parts.Length);
        var sdu = Root.GetProperty("sdu").GetInt32();
        Assert.Equal(Hex(c, "streamPlain").Length + 0, parts.Sum(p => p.Length));
        Assert.All(parts.Take(parts.Length - 1), p => Assert.Equal(sdu, p.Length));

        for (var i = 0; i < parts.Length; i++)
        {
            Assert.Equal(hashmap.AsSpan(i * 4, 4).ToArray(), Resource.MapHash(parts[i], random));
            var packet = Packet.Create(PacketType.Data, DestinationType.Link, linkId, parts[i], LinkContext.Resource);
            Assert.Equal(raws[i], packet.ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void Advertisement_PacksIdenticallyAndUnpacks(string label)
    {
        var c = Case(label);
        var hashmap = Hex(c, "hashmap");
        var parts = c.GetProperty("totalParts").GetInt32();
        var isRequest = c.GetProperty("requestId").ValueKind != JsonValueKind.Null;
        var adv = new ResourceAdvertisement
        {
            TransferSize = c.GetProperty("transferSize").GetInt64(),
            DataSize = Hex(c, "hashedData").Length,
            Parts = parts,
            Hash = Hex(c, "hash"),
            RandomHash = Hex(c, "randomHash"),
            OriginalHash = Hex(c, "originalHash"),
            Flags = ResourceAdvertisement.BuildFlags(true, c.GetProperty("compressed").GetBoolean(), false, isRequest, false,
                c.GetProperty("metadataPacked").ValueKind != JsonValueKind.Null),
            SegmentIndex = 1,
            TotalSegments = 1,
            RequestId = isRequest ? Hex(c, "requestId") : null,
        };
        var expected = Hex(c, "advertisement");
        Assert.Equal(expected, adv.Pack(hashmap));

        var parsed = ResourceAdvertisement.Unpack(expected);
        Assert.Equal(adv.Hash, parsed.Hash);
        Assert.Equal(adv.Flags, parsed.Flags);
        Assert.Equal(adv.Parts, parsed.Parts);
        Assert.Equal(adv.TransferSize, parsed.TransferSize);
        Assert.Equal(adv.RequestId, parsed.RequestId);
        Assert.Equal(hashmap.AsSpan(0, Math.Min(parts, ResourceAdvertisement.HashmapMaxLength) * 4).ToArray(), parsed.Hashmap);
    }

    [Fact]
    public void HashmapUpdate_PayloadMatchesUpstream()
    {
        var c = Case("incompressible_hashmap_segments");
        var hashmap = Hex(c, "hashmap");
        var segment = hashmap[(ResourceAdvertisement.HashmapMaxLength * 4)..];
        byte[] ours = [.. Hex(c, "hash"), .. MsgPack.Pack(new object?[] { 1L, segment })];
        Assert.Equal(Hex(c, "hashmapUpdateSegment1"), ours);
    }
}


