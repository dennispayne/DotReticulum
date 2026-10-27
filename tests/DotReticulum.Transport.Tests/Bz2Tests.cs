using System.Text.Json;

namespace DotReticulum.Transport.Tests;

[Trait("Category", "Unit")]
public class Bz2Tests
{
    private static JsonElement Root { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bz2-vectors.json"))).RootElement;

    [Fact]
    public void DecompressesPythonBz2Streams()
    {
        foreach (var c in Root.GetProperty("cases").EnumerateArray())
        {
            var plain = Convert.FromHexString(c.GetProperty("plain_hex").GetString()!);
            var packed = Convert.FromHexString(c.GetProperty("bz2_hex").GetString()!);
            Assert.Equal(plain, Bz2.Decompress(packed, 1 << 26));
        }
    }

    [Fact]
    public void RoundTripsAndEnforcesMaxSize()
    {
        var data = new byte[200_000];
        new Random(5).NextBytes(data.AsSpan(0, 5000));
        var packed = Bz2.Compress(data);
        Assert.True(packed.Length < data.Length);
        Assert.Equal(data, Bz2.Decompress(packed, 1 << 20));
        Assert.Throws<InvalidDataException>(() => Bz2.Decompress(packed, 1000));
        Assert.Throws<InvalidDataException>(() => Bz2.Decompress([1, 2, 3, 4, 5, 6], 1000));
    }
}
