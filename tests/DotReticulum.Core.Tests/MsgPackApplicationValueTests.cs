using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Core.Tests;

// Independently executed RNS a29bfb51c468945c96a1ee22cae0114e993e2391 umsgpack.py.
// Source SHA-256 0710f48f495193a764048c883b27b51cc7e7f7ae12486d4aad859994271cba50.
// GenerateMsgPackApplicationVectors.py verifies actual RNS/LXMF imports before generation.
[Trait("Category", "Unit")]
public class MsgPackApplicationValueTests
{
    private static JsonElement Vectors { get; } = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "msgpack-application-values-a29bfb51.json"))).RootElement;

    private static object? Value(string name)
    {
        var parts = name.Split(':');
        return parts[0] switch
        {
            "ext" => new MsgPackExtension(-42, Enumerable.Range(0, int.Parse(parts[1])).Select(i => (byte)i).ToArray()),
            "exttype" => new MsgPackExtension(sbyte.Parse(parts[1]), new byte[] { 1 }),
            "timestamp" => new MsgPackTimestamp(long.Parse(parts[1]), uint.Parse(parts[2])),
            "str" => new string('x', int.Parse(parts[1])),
            "bin" => new byte[int.Parse(parts[1])],
            "array" => new object?[int.Parse(parts[1])],
            "map" => Enumerable.Range(0, int.Parse(parts[1])).ToDictionary(i => (object)(long)i, _ => (object?)null),
            "unicode" => "héllo ☃ 😀",
            "binary-text-keys" => new Dictionary<object, object?> { [new byte[] { 0xAB }] = 1L, ["AB"] = 2L },
            "scalar-keys" => new Dictionary<object, object?>
            {
                [false] = "bool", [-3L] = "int", [1UL << 63] = "uint", [1.5] = "float",
                [new byte[] { (byte)'x' }] = "bin", ["x"] = "str",
                [new MsgPackExtension(7, new byte[] { (byte)'x' })] = "ext",
            },
            "negative-zero" => -0.0,
            "nan" => BitConverter.UInt64BitsToDouble(0x7FF8000000000000),
            "positive-infinity" => double.PositiveInfinity,
            "negative-infinity" => double.NegativeInfinity,
            "single" => 1.5,
            _ => throw new InvalidOperationException(name),
        };
    }

    private static byte[] Wire(string name) => Convert.FromHexString(Vectors.GetProperty("cases")
        .EnumerateArray().Single(c => c.GetProperty("name").GetString() == name).GetProperty("hex").GetString()!);

    [Fact]
    public void ApplicationVectorsHaveVerifiedProvenance()
    {
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", Vectors.GetProperty("pinned_sha").GetString());
        Assert.Equal("0710f48f495193a764048c883b27b51cc7e7f7ae12486d4aad859994271cba50",
            Vectors.GetProperty("source_sha256").GetString());
        Assert.Equal(51, Vectors.GetProperty("cases").GetArrayLength());
    }

    [Fact]
    public void NativeValuesMatchIndependentReferenceEncodingAndRoundTrip()
    {
        foreach (var vector in Vectors.GetProperty("cases").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            if (name == "single")
            {
                Assert.Equal(1.5, MsgPack.UnpackApplicationValue(Wire(name)));
                continue; // Python's opt-in float32 writer is not the default application contract.
            }
            var packed = MsgPack.Pack(Value(name));
            Assert.Equal(vector.GetProperty("length").GetInt32(), packed.Length);
            Assert.Equal(vector.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(packed)));
            if (vector.GetProperty("hex").ValueKind == JsonValueKind.String)
                Assert.Equal(vector.GetProperty("hex").GetString(), Convert.ToHexStringLower(packed));
            Assert.Equal(packed, MsgPack.Pack(MsgPack.UnpackApplicationValue(packed)));
        }
    }

    [Fact]
    public void ReferenceFailuresAreExplicitAndTryUnpackClearsTheResult()
    {
        Assert.Equal(9, Vectors.GetProperty("rejected").GetArrayLength());
        foreach (var vector in Vectors.GetProperty("rejected").EnumerateArray())
        {
            var packed = Convert.FromHexString(vector.GetProperty("hex").GetString()!);
            Assert.Throws<FormatException>(() => MsgPack.UnpackApplicationValue(packed));
            Assert.Throws<FormatException>(() => MsgPack.Unpack(packed));
            Assert.False(MsgPack.TryUnpackApplicationValue(packed, out var native));
            Assert.Null(native);
            Assert.False(MsgPack.TryUnpack(packed, out var legacy));
            Assert.Null(legacy);
        }
    }

    [Fact]
    public void LegacyBinaryKeyProjectionIsPreservedWithoutSilentCollisions()
    {
        var packed = MsgPack.Pack(new Dictionary<object, object?> { [new byte[] { 0xAB, 0xCD }] = "value" });
        var legacy = Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(packed));
        Assert.Equal("value", legacy["ABCD"]);
        var native = Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(packed));
        Assert.Equal("value", native[new byte[] { 0xAB, 0xCD }]);
        Assert.Equal("value", native[new ReadOnlyMemory<byte>(new byte[] { 0xAB, 0xCD })]);
        Assert.IsType<byte[]>(Assert.Single(native).Key);
        var collision = Wire("binary-text-keys");
        Assert.Throws<FormatException>(() => MsgPack.Unpack(collision));
        var distinct = Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(collision));
        Assert.Equal(1L, distinct[new byte[] { 0xAB }]);
        Assert.Equal(2L, distinct["AB"]);
        Assert.Equal(collision, MsgPack.Pack(distinct));
    }

    [Fact]
    public void NumericKeysUseExactPythonEqualityWithoutDoubleRounding()
    {
        var map = Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(Wire("scalar-keys")));
        Assert.Equal("bool", map[0L]);
        Assert.Equal("bool", map[0.0]);
        Assert.Equal("uint", map[(double)(1UL << 63)]);
        Assert.False(map.ContainsKey((1UL << 63) + 1));
        var exact = new Dictionary<object, object?>
        {
            [9007199254740992L] = 1L, [9007199254740993L] = 2L,
            [long.MaxValue] = 3L, [1UL << 63] = 4L,
        };
        var decoded = Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(MsgPack.Pack(exact)));
        Assert.Equal(4, decoded.Count);
        Assert.Equal(2L, decoded[9007199254740993L]);
        Assert.Throws<ArgumentException>(() => MsgPack.Pack(new Dictionary<object, object?> { [true] = 1, [1L] = 2 }));
        Assert.Throws<ArgumentException>(() => MsgPack.Pack(new List<KeyValuePair<object, object?>>
        {
            new(new byte[] { 1 }, 1), new(new byte[] { 1 }, 2),
        }));
    }

    [Theory]
    [InlineData("820101cb3ff000000000000002")]
    [InlineData("820001cb800000000000000002")]
    [InlineData("82cf800000000000000001cb43e000000000000002")]
    public void NumericDuplicatesAreRejectedAcrossEncodings(string hex)
    {
        Assert.Throws<FormatException>(() => MsgPack.UnpackApplicationValue(Convert.FromHexString(hex)));
        Assert.Throws<FormatException>(() => MsgPack.UnpackApplicationValue(Convert.FromHexString(hex), allowExtendedMapKeys: true));
    }

    [Fact]
    public void RestrictionsAreDiagnosedRatherThanLossyCoercions()
    {
        foreach (var observation in Vectors.GetProperty("intentional_restrictions").EnumerateArray())
            Assert.Throws<FormatException>(() => MsgPack.UnpackApplicationValue(Convert.FromHexString(
                observation.GetProperty("hex").GetString()!)));
        foreach (var value in new object[] { decimal.One, DateTime.UnixEpoch, new List<object?>(), System.Numerics.BigInteger.One, (char)'a' })
            Assert.Throws<ArgumentException>(() => MsgPack.Pack(value));
        foreach (var key in new object[] { new object?[] { 1L }, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => MsgPack.Pack(new Dictionary<object, object?> { [key] = 1L }));
        Assert.Throws<ArgumentException>(() => MsgPack.Pack(new List<KeyValuePair<object, object?>> { new(null!, 1L) }));
        Assert.Throws<ArgumentException>(() => MsgPack.Pack("\uD800"));
        Assert.Throws<ArgumentException>(() => MsgPack.Pack("\uDC00"));
    }

    private static MsgPackMap Map(params (object? Key, object? Value)[] entries) =>
        new(entries.Select(entry => new KeyValuePair<object?, object?>(entry.Key, entry.Value)).ToArray());

    [Fact]
    public void ExtendedMapsMatchExecutedReferenceBytesWithoutChangingDefaultReaders()
    {
        Assert.Equal(8, Vectors.GetProperty("extended_map_keys").GetArrayLength());
        foreach (var vector in Vectors.GetProperty("extended_map_keys").EnumerateArray())
        {
            var value = vector.GetProperty("name").GetString() switch
            {
                "null" => Map((null, "nil")),
                "tuple" => Map((new object?[] { 1L, 2L }, "tuple")),
                "nested-tuple" => Map((new object?[] { null, new object?[] { 1L, 2L }, new byte[] { (byte)'x' } }, "nested")),
                "positive-infinity" => Map((double.PositiveInfinity, "infinity")),
                "negative-infinity" => Map((double.NegativeInfinity, "infinity")),
                "nan" => Map((Value("nan"), "nan")),
                "distinct-nans" => Map((Value("nan"), 1L), (Value("nan"), 2L)),
                "mixed" => Map(("normal", Map((null, 1L))),
                    (new byte[] { (byte)'x' }, Map((new object?[] { true, 2.5 }, new object?[] { null, 3L })))),
                _ => throw new InvalidOperationException(),
            };
            var wire = Convert.FromHexString(vector.GetProperty("hex").GetString()!);
            Assert.Equal(wire, MsgPack.Pack(value));
            Assert.Throws<FormatException>(() => MsgPack.UnpackApplicationValue(wire));
            Assert.True(MsgPack.TryUnpackApplicationValue(wire, out var decoded, allowExtendedMapKeys: true));
            Assert.Equal(vector.GetProperty("repacked_hex").GetString(), Convert.ToHexStringLower(MsgPack.Pack(decoded)));
            if (decoded is MsgPackMap map)
                Assert.Equal(vector.GetProperty("entries").GetInt32(), map.Count);
        }
    }

    [Fact]
    public void ExtendedModeKeepsOrdinaryMapsAndBinaryKeyEquality()
    {
        var wire = Wire("scalar-keys");
        var decoded = Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(wire, allowExtendedMapKeys: true));
        Assert.Equal("bin", decoded[new byte[] { (byte)'x' }]);
        Assert.Equal(wire, MsgPack.Pack(decoded));
        var entries = new[] { new KeyValuePair<object?, object?>(null, "original") };
        var map = new MsgPackMap(entries);
        entries[0] = new(null, "changed");
        Assert.Equal("original", map[0].Value);
    }

    [Fact]
    public void ExtendedMapsRejectStructuralNumericDuplicatesAndUnhashableKeys()
    {
        foreach (var duplicate in new[]
        {
            Map((null, 1L), (null, 2L)),
            Map((new object?[] { true, new byte[] { 1 } }, 1L),
                (new object?[] { 1.0, new byte[] { 1 } }, 2L)),
            Map((double.PositiveInfinity, 1L), (float.PositiveInfinity, 2L)),
            Map((new Dictionary<object, object?>(), 1L)),
            Map((new object?[] { Map((null, 1L)) }, 1L)),
        })
            Assert.Throws<ArgumentException>(() => MsgPack.Pack(duplicate));
    }

    [Fact]
    public void ExtendedMapsRetainBoundsAndClearTryResults()
    {
        var key = new object?[1];
        key[0] = key;
        Assert.Throws<ArgumentException>(() => MsgPack.Pack(Map((key, 1L))));
        var bounded = Map((new object?[] { new object?[] { 1L } }, 2L));
        Assert.Throws<ArgumentException>(() => MsgPack.Pack(bounded, new MsgPackLimits { MaxDepth = 2 }));
        var wire = MsgPack.Pack(bounded);
        Assert.False(MsgPack.TryUnpackApplicationValue(wire, out var decoded,
            new MsgPackLimits { MaxDepth = 2 }, allowExtendedMapKeys: true));
        Assert.Null(decoded);
        for (var length = 0; length < wire.Length; length++)
        {
            Assert.False(MsgPack.TryUnpackApplicationValue(wire.AsSpan(0, length), out decoded, allowExtendedMapKeys: true));
            Assert.Null(decoded);
        }
        var nans = Map(Enumerable.Range(0, 1000).Select(i => ((object?)double.NaN, (object?)(long)i)).ToArray());
        var decodedNaNs = Assert.IsType<MsgPackMap>(MsgPack.UnpackApplicationValue(MsgPack.Pack(nans), allowExtendedMapKeys: true));
        Assert.Equal(1000, decodedNaNs.Count);
    }

    [Theory]
    [InlineData("a1ff")]
    [InlineData("a2c080")]
    [InlineData("a3eda080")]
    [InlineData("a4f4908080")]
    [InlineData("d901ff")]
    [InlineData("da0001ff")]
    [InlineData("db00000001ff")]
    public void EveryStringHeaderUsesStrictUtf8(string hex) =>
        Assert.Throws<FormatException>(() => MsgPack.Unpack(Convert.FromHexString(hex)));

    [Fact]
    public void AllExtensionFormsRejectEveryTruncationAndTrailingData()
    {
        foreach (var name in new[] { "ext:0", "ext:1", "ext:2", "ext:3", "ext:4", "ext:8", "ext:16", "ext:17",
                     "ext:255", "timestamp:0:0", "timestamp:4294967296:0", "timestamp:-1:123456000" })
        {
            var packed = Wire(name);
            for (var length = 0; length < packed.Length; length++)
                Assert.Throws<FormatException>(() => MsgPack.UnpackApplicationValue(packed.AsSpan(0, length)));
            Assert.Throws<FormatException>(() => MsgPack.UnpackApplicationValue([.. packed, 0]));
        }
    }

    [Fact]
    public void TimestampRetainsNanosecondsAndValidatesRatherThanNormalizingMalformedFields()
    {
        foreach (var seconds in new[] { long.MinValue, -1L, 0L, 1L << 34, long.MaxValue })
        {
            var timestamp = new MsgPackTimestamp(seconds, 999999999);
            Assert.Equal(timestamp, MsgPack.UnpackApplicationValue(MsgPack.Pack(timestamp)));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsgPackTimestamp(0, 1000000000));
        Assert.Throws<ArgumentException>(() => new MsgPackExtension(-1, new byte[4]));
        Assert.Throws<FormatException>(() => MsgPack.UnpackApplicationValue(Convert.FromHexString("c70cff3b9aca000000000000000000")));
        Assert.Throws<FormatException>(() => MsgPack.UnpackApplicationValue(Convert.FromHexString("d7ffee6b280000000000")));
    }

    [Fact]
    public void ExtensionOwnsItsPayloadAndHasValueEquality()
    {
        var data = new byte[] { 1, 2 };
        var extension = new MsgPackExtension(7, data);
        data[0] = 9;
        Assert.Equal(new byte[] { 1, 2 }, extension.Data.ToArray());
        Assert.Equal(new MsgPackExtension(7, new byte[] { 1, 2 }), extension);
        Assert.NotEqual(new MsgPackExtension(8, new byte[] { 1, 2 }), extension);
        Assert.Equal(extension.GetHashCode(), new MsgPackExtension(7, new byte[] { 1, 2 }).GetHashCode());
    }

    [Fact]
    public void DepthCountsEdgesAndCyclesFailWithinTheConfiguredBound()
    {
        object? nested = null;
        for (var i = 0; i < 32; i++) nested = new object?[] { nested };
        var packed = MsgPack.Pack(nested);
        Assert.Equal(packed, MsgPack.Pack(MsgPack.Unpack(packed)));
        var overDepth = new object?[] { nested };
        Assert.Throws<ArgumentException>(() => MsgPack.Pack(overDepth));
        Assert.Throws<FormatException>(() => MsgPack.Unpack([0x91, .. packed]));
        var cycle = new object?[1];
        cycle[0] = cycle;
        Assert.Throws<ArgumentException>(() => MsgPack.Pack(cycle));
        Assert.Throws<ArgumentException>(() => MsgPack.Pack(new object?[] { 0 }, new MsgPackLimits { MaxDepth = 0 }));
        Assert.Equal(new byte[] { 0x90 }, MsgPack.Pack(Array.Empty<object?>(), new MsgPackLimits { MaxDepth = 0 }));
    }

    [Fact]
    public void LimitsApplyBeforeAllocationOnBothReadAndWrite()
    {
        var encoded = new MsgPackLimits { MaxEncodedBytes = 3 };
        Assert.Equal(new byte[] { 0xC4, 1, 7 }, MsgPack.Pack(new byte[] { 7 }, encoded));
        Assert.Throws<ArgumentException>(() => MsgPack.Pack(new byte[2], encoded));
        Assert.Throws<FormatException>(() => MsgPack.Unpack(new byte[] { 0xC4, 2, 0, 0 }, encoded));
        var bytes = new MsgPackLimits { MaxValueBytes = 1 };
        foreach (var value in new object[] { new byte[2], "xx", new MsgPackExtension(7, new byte[2]), new MsgPackTimestamp(0) })
        {
            Assert.Throws<ArgumentException>(() => MsgPack.Pack(value, bytes));
            Assert.Throws<FormatException>(() => MsgPack.Unpack(MsgPack.Pack(value), bytes));
        }
        var count = new MsgPackLimits { MaxContainerEntries = 1 };
        Assert.Equal(new byte[] { 0x91, 0xC0 }, MsgPack.Pack(new object?[] { null }, count));
        Assert.Equal(new byte[] { 0x81, 0, 0xC0 }, MsgPack.Pack(new Dictionary<object, object?> { [0L] = null }, count));
        foreach (var value in new object[] { new object?[] { 1, 2 }, new Dictionary<object, object?> { [1] = 1, [2] = 2 } })
        {
            Assert.Throws<ArgumentException>(() => MsgPack.Pack(value, count));
            Assert.Throws<FormatException>(() => MsgPack.Unpack(MsgPack.Pack(value), count));
        }
        var total = new MsgPackLimits { MaxValues = 3 };
        var nested = new object?[] { new object?[] { null }, null };
        Assert.Throws<ArgumentException>(() => MsgPack.Pack(nested, total));
        Assert.Throws<FormatException>(() => MsgPack.Unpack(MsgPack.Pack(nested), total));
        Assert.Equal(new byte[] { 0x91, 0x91, 0xC0 }, MsgPack.Pack(new object?[] { new object?[] { null } }, total));
        Assert.Equal(new byte[] { 0x81, 0, 0xC0 }, MsgPack.Pack(new Dictionary<object, object?> { [0L] = null }, total));
        var empty = new MsgPackLimits { MaxValueBytes = 0, MaxContainerEntries = 0, MaxValues = 1 };
        foreach (var value in new object[] { "", Array.Empty<byte>(), Array.Empty<object?>(), new Dictionary<object, object?>(), new MsgPackExtension(7, []) })
            Assert.Equal(MsgPack.Pack(value), MsgPack.Pack(MsgPack.Unpack(MsgPack.Pack(value), empty), empty));
        foreach (var hex in new[] { "c6ffffffff", "dbffffffff", "c9ffffffff00", "ddffffffff", "dfffffffff", "df000000020000" })
            Assert.Throws<FormatException>(() => MsgPack.Unpack(Convert.FromHexString(hex)));
    }

    [Fact]
    public void InvalidPoliciesAreProgrammerErrorsEvenForTryUnpack()
    {
        foreach (var limits in new[]
        {
            new MsgPackLimits { MaxDepth = 129 }, new MsgPackLimits { MaxDepth = -1 },
            new MsgPackLimits { MaxValues = 0 }, new MsgPackLimits { MaxValueBytes = -1 },
            new MsgPackLimits { MaxContainerEntries = -1 }, new MsgPackLimits { MaxEncodedBytes = 0 },
        })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MsgPack.Pack(null, limits));
            Assert.Throws<ArgumentOutOfRangeException>(() => MsgPack.Unpack(new byte[] { 0xC0 }, limits));
            Assert.Throws<ArgumentOutOfRangeException>(() => MsgPack.TryUnpackApplicationValue(new byte[] { 0xC0 }, out _, limits));
        }
    }

    [Theory]
    [InlineData(-128)]
    [InlineData(0)]
    [InlineData(127)]
    public void NativeSmallIntegerTypesAreAccepted(int value)
    {
        Assert.Equal(MsgPack.Pack((long)value), MsgPack.Pack((sbyte)value));
        Assert.Equal(MsgPack.Pack((long)value), MsgPack.Pack((short)value));
        if (value >= 0)
        {
            Assert.Equal(MsgPack.Pack((long)value), MsgPack.Pack((byte)value));
            Assert.Equal(MsgPack.Pack((long)value), MsgPack.Pack((ushort)value));
        }
    }

    [Fact]
    public void NumberConversionsRetainTruncationButRejectNonFiniteAndOutOfRangeIntegers()
    {
        Assert.Equal(1L, MsgPack.ToInt64(1.9));
        Assert.Equal(long.MinValue, MsgPack.ToInt64((double)long.MinValue));
        foreach (var value in new object?[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity,
                     9223372036854775808d, ulong.MaxValue, null, "1", true })
            Assert.Throws<FormatException>(() => MsgPack.ToInt64(value));
    }
}
