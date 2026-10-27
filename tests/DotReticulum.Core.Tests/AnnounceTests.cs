using System.Text.Json;

namespace DotReticulum.Core.Tests;

// Vectors: Fixtures/announce-vectors.json, produced by EXECUTING upstream Reticulum (Destination.announce, Identity.validate_announce)
// markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b via GenerateAnnounceVectors.py. The upstream verdict for every
// tampered/truncated variant is recorded in the fixture and compared with ours; Ed25519 is deterministic, so re-creating an
// upstream announce with the same identity, random hash, ratchet and app_data must reproduce the upstream bytes exactly.
[Trait("Category", "Unit")]
public class AnnounceTests
{
    private static JsonElement Root { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "announce-vectors.json"))).RootElement;

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    private static byte[]? HexOrNull(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Convert.FromHexString(e.GetProperty(name).GetString()!);

    public static IEnumerable<object[]> AnnounceLabels() =>
        Root.GetProperty("announces").EnumerateArray().Select(a => new object[] { a.GetProperty("label").GetString()! });

    private static JsonElement Vector(string label) =>
        Root.GetProperty("announces").EnumerateArray().Single(a => a.GetProperty("label").GetString() == label);

    [Theory]
    [MemberData(nameof(AnnounceLabels))]
    public void UpstreamAnnounces_AreAccepted_WithMatchingFields(string label)
    {
        var v = Vector(label);
        var packet = Packet.Parse(Hex(v, "raw"));
        var result = Announce.Validate(packet);

        Assert.Equal(AnnounceStatus.Valid, result.Status);
        Assert.True(v.GetProperty("upstream_valid").GetBoolean());
        Assert.Equal(v.GetProperty("context_flag").GetBoolean(), packet.ContextFlag);
        Assert.Equal((byte)v.GetProperty("context").GetInt32(), packet.Context);
        Assert.Equal(Hex(v, "identity_public"), result.Data!.PublicKey);
        Assert.Equal(Hex(v, "name_hash"), result.Data.NameHash);
        Assert.Equal(Hex(v, "destination_hash"), packet.DestinationHash.ToArray());
        Assert.Equal(HexOrNull(v, "ratchet"), result.Data.Ratchet);
        // Upstream records what it remembered as app_data (None vs empty vs bytes).
        Assert.Equal(HexOrNull(v, "upstream_recalled_app_data"), result.Data.AppData);
        Assert.Equal(PacketType.Announce, packet.PacketType);
        Assert.Equal(DestinationType.Single, packet.DestinationType);
    }

    [Theory]
    [MemberData(nameof(AnnounceLabels))]
    public void RecreatingAnUpstreamAnnounce_ReproducesTheUpstreamBytes(string label)
    {
        var v = Vector(label);
        var upstream = Packet.Parse(Hex(v, "raw"));
        var result = Announce.Validate(upstream);
        var identity = Identity.FromPrivateKey(Hex(v, "identity_private"));
        var destination = Destination.Create(DestinationType.Single, v.GetProperty("app_name").GetString()!,
            v.GetProperty("aspects").EnumerateArray().Select(a => a.GetString()!).ToArray(), identity);
        Assert.Equal(Hex(v, "destination_hash"), destination.Hash);

        var emitted = result.Data!.Emitted;
        DestinationRatchets? ratchets = HexOrNull(v, "ratchet_private") is { } prv
            ? DestinationRatchets.FromPrivateKeys([prv], emitted)
            : null;
        var appData = HexOrNull(v, "app_data") ?? [];

        var created = Announce.Create(identity, destination, appData, ratchets,
            pathResponse: upstream.Context == Announce.PathResponseContext, unixSeconds: emitted, randomHash: result.Data.RandomHash);
        Assert.Equal(upstream.ToArray(), created.ToArray());
    }

    [Fact]
    public void TamperedAndTruncatedUpstreamVariants_MatchUpstreamVerdicts()
    {
        var count = 0;
        foreach (var t in Root.GetProperty("tampered").EnumerateArray())
        {
            count++;
            var raw = Hex(t, "raw");
            var what = $"{t.GetProperty("base").GetString()}/{t.GetProperty("what").GetString()}";
            if (!Packet.TryParse(raw, out var packet))
            {
                Assert.False(t.GetProperty("upstream_valid").GetBoolean(), what);
                continue;
            }

            var result = Announce.Validate(packet);
            Assert.True(t.GetProperty("upstream_valid").GetBoolean() == result.IsValid, $"{what}: status {result.Status}");
            var signatureOk = result.Status is AnnounceStatus.Valid or AnnounceStatus.DestinationMismatch;
            Assert.True(t.GetProperty("upstream_signature_valid").GetBoolean() == signatureOk, $"{what}: signature, status {result.Status}");
        }

        Assert.True(count > 60);
    }

    [Fact]
    public void ValidSignatureForTheWrongDestinationHash_IsReportedAsMismatch()
    {
        var t = Root.GetProperty("tampered").EnumerateArray().Single(x => x.GetProperty("what").GetString() == "destination_hash_mismatch_valid_signature");
        Assert.True(t.GetProperty("upstream_signature_valid").GetBoolean());
        Assert.False(t.GetProperty("upstream_valid").GetBoolean());
        Assert.Equal(AnnounceStatus.DestinationMismatch, Announce.Validate(Packet.Parse(Hex(t, "raw"))).Status);
    }

    [Fact]
    public void NonAnnouncePackets_AreNotAnnounces()
    {
        var packet = Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], new byte[200]);
        Assert.Equal(AnnounceStatus.NotAnnounce, Announce.Validate(packet).Status);
    }

    [Fact]
    public void ShortPayloads_AreMalformed_AndRatchetFlagRequiresTheExtraBytes()
    {
        var plain = Packet.Create(PacketType.Announce, DestinationType.Single, new byte[16], new byte[Announce.MinPayloadLength - 1]);
        Assert.Equal(AnnounceStatus.Malformed, Announce.Validate(plain).Status);
        var flagged = Packet.Create(PacketType.Announce, DestinationType.Single, new byte[16], new byte[Announce.MinPayloadLength + 31], contextFlag: true);
        Assert.Equal(AnnounceStatus.Malformed, Announce.Validate(flagged).Status);
    }

    [Fact]
    public void GeneratedRandomHash_IsFiveRandomBytesThenBigEndianTimestamp()
    {
        var hash = Announce.CreateRandomHash(0x0102030405);
        Assert.Equal(10, hash.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, hash[5..]);
        Assert.Equal(0x0102030405, Announce.EmittedFromRandomHash(hash));
        Assert.NotEqual(hash[..5], Announce.CreateRandomHash(0x0102030405)[..5]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Announce.CreateRandomHash(1L << 40));
    }

    [Fact]
    public void Create_RoundTripsWithAndWithoutRatchetsAndAppData()
    {
        using var identity = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "dotreticulum", ["tests", "roundtrip"], identity);
        using var ratchets = new DestinationRatchets();
        foreach (var (rat, app) in new (DestinationRatchets?, byte[])[] { (null, []), (null, [1, 2, 3]), (ratchets, []), (ratchets, [9, 9]) })
        {
            var packet = Announce.Create(identity, destination, app, rat, unixSeconds: 1_700_000_000);
            Assert.Equal(rat is not null, packet.ContextFlag);
            var parsed = Packet.Parse(packet.ToArray());
            var result = Announce.Validate(parsed);
            Assert.Equal(AnnounceStatus.Valid, result.Status);
            Assert.Equal(1_700_000_000, result.Data!.Emitted);
            Assert.Equal(app, result.Data.AppData ?? []);
            Assert.Equal(rat?.CurrentPublicKey, result.Data.Ratchet);
        }
    }

    [Fact]
    public void Create_RejectsWrongDestinationsAndOversizedAppData()
    {
        using var identity = Identity.Generate();
        using var other = Identity.Generate();
        var mine = Destination.Create(DestinationType.Single, "dotreticulum", ["tests"], identity);
        Assert.Throws<ArgumentException>(() => Announce.Create(other, mine));
        Assert.Throws<ArgumentException>(() => Announce.Create(identity, Destination.Create(DestinationType.Plain, "dotreticulum", ["tests"])));
        Assert.Throws<ArgumentException>(() => Announce.Create(identity, mine, new byte[Packet.Mtu]));
        using var publicOnly = Identity.FromPublicKey(identity.ExportPublicKey());
        Assert.Throws<InvalidOperationException>(() => Announce.Create(publicOnly, mine));
    }

    [Fact]
    public void Ratchets_RotateOnlyWhenDue_AndTrimToRetained()
    {
        using var ratchets = new DestinationRatchets { Interval = TimeSpan.FromSeconds(100), Retained = 3 };
        var first = ratchets.Rotate(1000);
        Assert.Equal(first, ratchets.Rotate(1100));
        var second = ratchets.Rotate(1101);
        Assert.NotEqual(first, second);
        ratchets.Rotate(1300);
        ratchets.Rotate(1500);
        Assert.Equal(3, ratchets.Count);
        Assert.Equal(DestinationRatchets.GetRatchetId(ratchets.CurrentPublicKey!), SHA256Prefix(ratchets.CurrentPublicKey!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ratchets.Retained = 0);
    }

    private static byte[] SHA256Prefix(byte[] data) => System.Security.Cryptography.SHA256.HashData(data)[..10];

    [Fact]
    public void Ratchets_DecryptEncryptedToAnnouncedRatchet_AndEnforceRejectsIdentityKey()
    {
        using var identity = Identity.Generate();
        using var ratchets = new DestinationRatchets();
        var publicRatchet = ratchets.Rotate(1);
        var viaRatchet = identity.Encrypt("secret"u8, publicRatchet);
        var viaIdentity = identity.Encrypt("plain"u8);

        Assert.Equal("secret"u8.ToArray(), ratchets.Decrypt(identity, viaRatchet));
        Assert.Equal("plain"u8.ToArray(), ratchets.Decrypt(identity, viaIdentity));
        ratchets.Enforce = true;
        Assert.Null(ratchets.Decrypt(identity, viaIdentity));
        Assert.Equal("secret"u8.ToArray(), ratchets.Decrypt(identity, viaRatchet));

        // An older ratchet still decrypts after rotation.
        ratchets.Interval = TimeSpan.Zero;
        ratchets.Rotate(100);
        Assert.Equal(2, ratchets.Count);
        Assert.Equal("secret"u8.ToArray(), ratchets.Decrypt(identity, viaRatchet));
    }
}
