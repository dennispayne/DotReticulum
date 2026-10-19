using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Core.Tests;

// Vectors: Fixtures/destination-vectors.json, produced by EXECUTING upstream Reticulum
// (markqvist/Reticulum @ e40191b3d193b46b7f2d8a44424a594cd758839b, RNS/Destination.py: constructor, expand_name, hash,
// hash_from_name_and_identity) with GenerateDestinationVectors.py.
public class DestinationTests
{
    private static JsonElement Root { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "destination-vectors.json"))).RootElement;

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    private static string[] Aspects(JsonElement e) => e.GetProperty("aspects").EnumerateArray().Select(a => a.GetString()!).ToArray();

    [Fact]
    public void SingleDestinationsMatchUpstream()
    {
        var vectors = Root.GetProperty("single").EnumerateArray().ToList();
        Assert.Equal(8, vectors.Count);
        foreach (var v in vectors)
        {
            var app = v.GetProperty("app_name").GetString()!;
            var aspects = Aspects(v);
            using var id = Identity.FromPrivateKey(Hex(v, "identity_private"));
            Assert.Equal(Hex(v, "identity_hash"), id.Hash);

            var d = Destination.Create(DestinationType.Single, app, aspects, id);
            Assert.Equal(v.GetProperty("full_name").GetString(), d.FullName);
            Assert.Equal(Hex(v, "name_hash"), d.NameHash);
            Assert.Equal(Hex(v, "hash"), d.Hash);
            Assert.Equal(10, d.NameHash.Length);
            Assert.Equal(16, d.Hash.Length);

            Assert.Equal(v.GetProperty("expand_name_no_identity").GetString(), Destination.ExpandName(default, app, aspects));
            Assert.Equal(Hex(v, "static_hash_identity_bytes"), Destination.ComputeHash(id.Hash, app, aspects));
            Assert.Equal(Hex(v, "static_hash_no_identity"), Destination.ComputeHash(default, app, aspects));
        }
    }

    [Fact]
    public void NameHashNeverIncludesTheIdentity()
    {
        foreach (var v in Root.GetProperty("single").EnumerateArray())
        {
            var d = Destination.Create(DestinationType.Single, v.GetProperty("app_name").GetString()!, Aspects(v), Hex(v, "identity_hash"));
            Assert.Equal(Destination.ComputeNameHash(d.AppName, Aspects(v)), d.NameHash);
            Assert.EndsWith("." + v.GetProperty("identity_hash").GetString(), d.FullName);
        }
    }

    [Fact]
    public void PlainDestinationsMatchUpstream()
    {
        var vectors = Root.GetProperty("plain").EnumerateArray().ToList();
        Assert.Equal(8, vectors.Count);
        foreach (var v in vectors)
        {
            var d = Destination.Create(DestinationType.Plain, v.GetProperty("app_name").GetString()!, Aspects(v));
            Assert.Null(d.IdentityHash);
            Assert.Equal(v.GetProperty("full_name").GetString(), d.FullName);
            Assert.Equal(Hex(v, "name_hash"), d.NameHash);
            Assert.Equal(Hex(v, "hash"), d.Hash);
        }
    }

    [Fact]
    public void GroupDestinationMatchesUpstream()
    {
        var v = Root.GetProperty("group").EnumerateArray().Single();
        var d = Destination.Create(DestinationType.Group, "grp", ["chat"], Hex(v, "identity_hash"));
        Assert.Equal(v.GetProperty("full_name").GetString(), d.FullName);
        Assert.Equal(Hex(v, "name_hash"), d.NameHash);
        Assert.Equal(Hex(v, "hash"), d.Hash);
        Assert.Equal(DestinationType.Group, d.Type);
    }

    [Fact]
    public void HashFromNameAndIdentityMatchesUpstream()
    {
        foreach (var v in Root.GetProperty("hash_from_name").EnumerateArray())
        {
            var id = Hex(v, "identity_hash");
            Assert.Equal(Hex(v, "hash"), Destination.ComputeHashFromNameAndIdentity(v.GetProperty("full_name").GetString()!, id));
            Assert.Equal(Hex(v, "hash_bytes"), Destination.ComputeHashFromNameAndIdentity(v.GetProperty("full_name").GetString()!, id));
        }
    }

    [Fact]
    public void SplitFullNameMatchesUpstreamSemantics()
    {
        var (app, aspects) = Destination.SplitFullName("lxmf.delivery");
        Assert.Equal("lxmf", app);
        Assert.Equal(["delivery"], aspects);
        (app, aspects) = Destination.SplitFullName("app");
        Assert.Equal("app", app);
        Assert.Empty(aspects);
        (app, aspects) = Destination.SplitFullName(".x");
        Assert.Equal("", app);
        Assert.Equal(["x"], aspects);
    }

    [Fact]
    public void InvalidInputsAreRejectedLikeUpstream()
    {
        foreach (var v in Root.GetProperty("invalid").EnumerateArray()) Assert.True(v.GetProperty("upstream_rejects").GetBoolean(), v.GetProperty("case").GetString());

        var hash = new byte[16];
        Assert.Throws<ArgumentException>(() => Destination.Create(DestinationType.Plain, "a.b", ["c"]));
        Assert.Throws<ArgumentException>(() => Destination.Create(DestinationType.Plain, "a", ["b.c"]));
        Assert.Throws<ArgumentException>(() => Destination.ExpandName(default, "a", ["b.c"]));
        Assert.Throws<ArgumentException>(() => Destination.Create(DestinationType.Plain, "a", [], hash));
        Assert.Throws<ArgumentException>(() => Destination.Create(DestinationType.Single, "a", []));
        Assert.Throws<ArgumentException>(() => Destination.Create(DestinationType.Link, "a", []));
        Assert.Throws<ArgumentException>(() => Destination.Create(DestinationType.Single, "a", [], new byte[15]));
        Assert.Throws<ArgumentException>(() => Destination.ComputeHash(new byte[17], "a", []));
    }
}
