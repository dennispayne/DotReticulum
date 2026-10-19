using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Core.Tests;

// Vectors: Fixtures/msgpack-vectors.json, produced by EXECUTING upstream's bundled umsgpack (GenerateMsgPackVectors.py).
public class MsgPackTests
{
    private static JsonElement Root { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "msgpack-vectors.json"))).RootElement;

    private static object? Build(string name)
    {
        var parts = name.Split(':', 3);
        switch (parts[0])
        {
            case "int":
                {
                    var v = System.Numerics.BigInteger.Parse(parts[1]);
                    return v > long.MaxValue ? (ulong)v : (long)v;
                }

            case "str":
                {
                    var n = int.Parse(parts[1]);
                    return parts[2].StartsWith('x') ? new string('x', n) : parts[2] == "" ? "" : parts[2] == "abc" ? "abc" : "h\u00e9llo \u2603";
                }

            case "bin":
                {
                    var n = int.Parse(parts[1]);
                    return n == 2 ? new byte[] { 1, 2 } : n == 255 ? Enumerable.Range(0, 255).Select(i => (byte)i).ToArray() : new byte[n];
                }

            case "float": return double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            case "nil": return null;
            case "true": return true;
            case "false": return false;
            case "array":
                return parts[1] switch
                {
                    "mixed" => new object?[] { 1L, "a", new byte[] { (byte)'x' }, null, true, 2.5, new object?[] { 3L, 4L } },
                    "20" => Enumerable.Range(0, 20).Select(i => (object?)(long)i).ToArray(),
                    _ => new object?[70000].Select(_ => (object?)0L).ToArray(),
                };
            case "map":
                return parts[1] == "mixed"
                    ? new List<KeyValuePair<object, object?>>
                    {
                        new("t", 1L), new("h", new byte[] { 0, 1 }), new("q", null), new("f", 3L),
                    }
                    : Enumerable.Range(0, 20).Select(i => new KeyValuePair<object, object?>(i.ToString(), (long)i)).ToList();
            default: throw new InvalidOperationException(name);
        }
    }

    [Fact]
    public void PackMatchesUpstreamBytes()
    {
        var count = 0;
        foreach (var c in Root.GetProperty("cases").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString()!;
            var expected = c.GetProperty("hex").GetString()!;
            Assert.Equal(expected, Convert.ToHexStringLower(MsgPack.Pack(Build(name))));
            count++;
        }

        Assert.True(count > 40);
    }

    [Fact]
    public void UnpackThenRepackIsIdempotentForUpstreamBytes()
    {
        foreach (var c in Root.GetProperty("cases").EnumerateArray())
        {
            var bytes = Convert.FromHexString(c.GetProperty("hex").GetString()!);
            var v = MsgPack.Unpack(bytes);
            if (v is Dictionary<object, object?>)
            {
                continue;
            }

            Assert.Equal(bytes, MsgPack.Pack(v));
        }
    }

    [Fact]
    public void UnpackReadsMapsAndRejectsMalformed()
    {
        var bytes = Convert.FromHexString(Root.GetProperty("cases").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "map:mixed").GetProperty("hex").GetString()!);
        var map = Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(bytes));
        Assert.Equal(1L, map["t"]);
        Assert.Equal(new byte[] { 0, 1 }, (byte[])map["h"]!);
        Assert.Null(map["q"]);
        Assert.False(MsgPack.TryUnpack(new byte[] { 0xC4, 0x05, 1 }, out _));
        Assert.False(MsgPack.TryUnpack(new byte[] { 0x01, 0x02 }, out _));
        Assert.False(MsgPack.TryUnpack(new byte[] { 0xC1 }, out _));
    }
}
