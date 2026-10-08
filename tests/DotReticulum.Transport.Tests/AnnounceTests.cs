using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Transport.Tests;

public class AnnounceTests
{
    private static readonly byte[] ReferencePacket = Convert.FromHexString(
        "01008083452306ea7a0733216a502f5f6283008f40c5adb68f25624ae5b214ea767a6ec94d829d3d7b5e1ad1ba6f3e2138285f29acbae141bccaf0b22e1a94d34d0bc7361e526d0bfe12c89794bc9322966dd73a2c54c2856d61ef90cca1b2c3d4e5006553f100e15e1c039cbb248d2b8bc0606239baf9d6d35697cfd974daf0a2166be6e38fb617427a0926f3bc68bc2817d41625bd8a8a5cf13618019a8ff04d6f286440c20d757073747265616d2d766563746f72");

    [Fact]
    public void ValidatesPinnedUpstreamPythonAnnounce()
    {
        var packet = Packet.Parse(ReferencePacket);
        Assert.True(Announce.TryValidate(packet, out var announce));
        Assert.NotNull(announce);
        Assert.Equal(Convert.FromHexString("8083452306ea7a0733216a502f5f6283"),
            announce.DestinationHash.ToArray());
        Assert.Equal(Convert.FromHexString(
            "8f40c5adb68f25624ae5b214ea767a6ec94d829d3d7b5e1ad1ba6f3e2138285f29acbae141bccaf0b22e1a94d34d0bc7361e526d0bfe12c89794bc9322966dd7"),
            announce.PublicKey.ToArray());
        Assert.Equal(Convert.FromHexString("3a2c54c2856d61ef90cc"), announce.NameHash.ToArray());
        Assert.Equal(Convert.FromHexString("a1b2c3d4e5006553f100"), announce.RandomHash.ToArray());
        Assert.Equal("upstream-vector", System.Text.Encoding.UTF8.GetString(announce.AppData.Span));
    }

    [Fact]
    public void RejectsModifiedAnnounceFieldsAndUnsupportedRatchets()
    {
        foreach (var offset in new[] { 2, Packet.Header1Length, Packet.Header1Length + 64,
                     Packet.Header1Length + 74, Packet.Header1Length + 84,
                     Packet.Header1Length + 148 })
        {
            var modified = (byte[])ReferencePacket.Clone();
            modified[offset] ^= 1;
            Assert.False(Announce.TryValidate(Packet.Parse(modified), out var announce));
            Assert.Null(announce);
        }

        var ratcheted = (byte[])ReferencePacket.Clone();
        ratcheted[0] |= 0x20;
        Assert.False(Announce.TryValidate(Packet.Parse(ratcheted), out _));

        var nonAnnounce = (byte[])ReferencePacket.Clone();
        nonAnnounce[0] = (byte)((nonAnnounce[0] & 0xfc) | (byte)PacketType.Data);
        Assert.False(Announce.TryValidate(Packet.Parse(nonAnnounce), out _));
    }

    [Fact]
    public void CreatesSignedAnnounceAndEnforcesMtu()
    {
        using var identity = Identity.Generate();
        var packet = Announce.Create(identity, "example.echo", [1, 2, 3]);

        Assert.Equal(PacketType.Announce, packet.Type);
        Assert.Equal(DestinationType.Single, packet.DestinationType);
        Assert.False(packet.ContextFlag);
        Assert.True(Announce.TryValidate(packet, out var announce));
        Assert.Equal(new byte[] { 1, 2, 3 }, announce!.AppData.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Announce.Create(identity, "example.echo", new byte[Announce.MaximumAppDataLength + 1]));
        using var publicIdentity = Identity.FromPublicKey(identity.ExportPublicKey());
        Assert.Throws<InvalidOperationException>(() => Announce.Create(publicIdentity, "example.echo"));
    }

    [Fact]
    public void RejectsTruncatedAnnouncePayload()
    {
        var tooShort = Packet.Create(PacketType.Announce, DestinationType.Single, new byte[16], new byte[1]);
        Assert.False(Announce.TryValidate(tooShort, out var announce));
        Assert.Null(announce);
    }
}

public class AnnounceRateLimiterTests
{
    [Fact]
    public void LimitsPerDestinationAndRefreshesLeastRecentlyAcceptedEntry()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var limiter = new AnnounceRateLimiter(TimeSpan.FromSeconds(10), capacity: 2, clock);
        var first = new byte[16];
        var second = Enumerable.Repeat((byte)1, 16).ToArray();
        var third = Enumerable.Repeat((byte)2, 16).ToArray();

        Assert.True(limiter.TryAccept(first));
        Assert.False(limiter.TryAccept(first));
        Assert.True(limiter.TryAccept(second));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(limiter.TryAccept(first));
        Assert.True(limiter.TryAccept(third));
        Assert.False(limiter.TryAccept(third));
        Assert.True(limiter.TryAccept(second));
    }

    [Fact]
    public void ValidatesLimitsAndDestinationHashSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnnounceRateLimiter(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnnounceRateLimiter(TimeSpan.FromSeconds(1), 0));
        var limiter = new AnnounceRateLimiter(TimeSpan.FromSeconds(1));
        Assert.Throws<ArgumentException>(() => limiter.TryAccept(new byte[15]));
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
