using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Crypto;

namespace DotReticulum.Core.Tests;

/// <summary>
/// Independent fixtures execute verified RNS a29bfb51c468945c96a1ee22cae0114e993e2391 (1.5.7).
/// GenerateParity4IfacVectors.py records individual source SHA-256 values and the in-memory bearer adapter.
/// These are algorithm/bearer vectors, not live resource/channel interoperability evidence.
/// </summary>
[Trait("Category", "Unit")]
public sealed class Parity4IfacTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity4-ifac-vectors.json")));

    public static TheoryData<int> VectorIndices => new(Enumerable.Range(0, 8));

    [Theory]
    [MemberData(nameof(VectorIndices))]
    public void DerivationAndMaskedFramesMatchActualPython(int index)
    {
        using var fixtures = Vectors();
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", fixtures.RootElement.GetProperty("upstream_sha").GetString());
        var vector = fixtures.RootElement.GetProperty("vectors")[index];
        using var code = InterfaceAccessCode.Derive(vector.GetProperty("network_name").GetString(),
            vector.GetProperty("passphrase").GetString(), vector.GetProperty("size").GetInt32());
        Assert.Equal(vector.GetProperty("public_key").GetString(), Convert.ToHexStringLower(code.ExportPublicKey()));
        Assert.Equal(vector.GetProperty("signature").GetString(), Convert.ToHexStringLower(code.Signature.Span));
        var header = Convert.FromHexString(vector.GetProperty("raw_header").GetString()!);
        var raw = new byte[vector.GetProperty("packet_length").GetInt32()];
        header.CopyTo(raw, 0);
        for (var i = 0; i < raw.Length - header.Length; i++) raw[header.Length + i] = unchecked((byte)(i * 37 + 11));
        var frame = code.Protect(raw);
        Assert.Equal(vector.GetProperty("frame_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(frame)));
        if (vector.GetProperty("frame").GetString() is { } full) Assert.Equal(Convert.FromHexString(full), frame);
        foreach (var sample in vector.GetProperty("samples").EnumerateArray())
        {
            var expected = Convert.FromHexString(sample.GetProperty("hex").GetString()!);
            Assert.Equal(expected, frame.AsSpan(sample.GetProperty("offset").GetInt32(), expected.Length).ToArray());
        }
        Assert.True(code.TryUnprotect(frame, out var restored));
        Assert.Equal(raw, restored);
        using var wrongKey = InterfaceAccessCode.Derive(code.NetworkName, "different synthetic key", code.Size);
        Assert.False(wrongKey.TryUnprotect(frame, out _));
        using var wrongNetwork = InterfaceAccessCode.Derive("different synthetic network", vector.GetProperty("passphrase").GetString(), code.Size);
        Assert.False(wrongNetwork.TryUnprotect(frame, out _));
        frame[^1] ^= 1;
        Assert.False(code.TryUnprotect(frame, out _));
    }

    [Fact]
    public void BitrateMtuThresholdsMatchPythonWithoutWeakeningStrictHkdf()
    {
        using var fixtures = Vectors();
        foreach (var vector in fixtures.RootElement.GetProperty("bitrate_mtus").EnumerateArray())
        {
            var expected = vector.GetProperty("hardware_mtu");
            Assert.Equal(expected.ValueKind == JsonValueKind.Null ? Packet.Mtu : expected.GetInt32(),
                PacketInterfaceCapabilities.MtuForBitrate(vector.GetProperty("bitrate").GetInt64()));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyDerivation.HkdfSha256([1], 8161));
    }

    [Fact]
    public void MalformedMissingFlagTruncationAndBoundsAreRejected()
    {
        using var code = InterfaceAccessCode.Derive("test", "synthetic", 16);
        var packet = MakePacket(1).ToArray();
        var frame = code.Protect(packet);
        Assert.False(code.TryUnprotect([], out _));
        Assert.False(code.TryUnprotect([0x80], out _));
        Assert.False(code.TryUnprotect(frame.AsSpan(0, 18), out _));
        Assert.False(code.TryUnprotect(packet, out _));
        frame[0] &= 0x7f;
        Assert.False(code.TryUnprotect(frame, out _));
        frame[0] |= 0x80;
        Assert.False(code.TryUnprotect(frame.AsSpan(0, frame.Length - 1), out _));
        Assert.False(code.TryUnprotect(new byte[InterfaceAccessCode.MaximumPacketSize + 65], out _));
        packet[0] |= 0x80;
        Assert.Throws<ArgumentException>(() => code.Protect(packet));
        Assert.Throws<ArgumentException>(() => code.Protect([0, 0]));
        Assert.Throws<ArgumentException>(() => code.Protect(new byte[InterfaceAccessCode.MaximumPacketSize + 1]));
        Assert.Throws<ArgumentException>(() => InterfaceAccessCode.Derive("None", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => InterfaceAccessCode.Derive("test", null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => InterfaceAccessCode.Derive("test", null, 65));
    }

    [Fact]
    public async Task AuthenticationIsConcurrentDeterministicAndDisposedKeysCannotBeUsed()
    {
        var code = InterfaceAccessCode.Derive("test", "synthetic");
        var packet = MakePacket(4).ToArray();
        var expected = code.Protect(packet);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
        {
            Assert.Equal(expected, code.Protect(packet));
            Assert.True(code.TryUnprotect(expected, out var decoded));
            Assert.Equal(packet, decoded);
        })));
        code.Dispose();
        code.Dispose();
        Assert.Throws<ObjectDisposedException>(() => code.Protect(packet));
        Assert.Throws<ObjectDisposedException>(() => code.TryUnprotect(expected, out _));
        Assert.Throws<ObjectDisposedException>(() => code.ExportPublicKey());
    }

    [Fact]
    public async Task ManagerAuthenticatesBeforeParsingAndReportsViolationCounters()
    {
        var bearer = new MemoryBearer();
        await using var manager = new PacketInterfaceManager([bearer]);
        manager.SetInterfaceAccessCode(bearer, InterfaceAccessCode.Derive("test", "synthetic"));
        await manager.StartAsync();
        using var peer = InterfaceAccessCode.Derive("test", "synthetic");
        using var wrong = InterfaceAccessCode.Derive("test", "wrong");
        var raw = MakePacket(7).ToArray();
        await bearer.Inject(raw);
        await bearer.Inject(wrong.Protect(raw));
        await bearer.Inject([0x80, 0, 1]);
        var damaged = peer.Protect(raw);
        damaged[^1] ^= 1;
        await bearer.Inject(damaged);
        var malformed = raw.ToArray();
        malformed[1] = 128;
        await bearer.Inject(peer.Protect(malformed));
        await bearer.Inject(peer.Protect(raw));
        await using var inbound = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();
        Assert.True(await inbound.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Equal(raw, inbound.Current.Raw.ToArray());
        Assert.Same(bearer, inbound.Current.Source);
        Assert.Equal(4, manager.GetStatistics().Single().IfacViolations);
        Assert.Equal(5, manager.GetStatistics().Single().MalformedPackets);
        await manager.SendToAsync(MakePacket(8), bearer);
        var outbound = await bearer.Sent.Reader.ReadAsync().AsTask().WaitAsync(Patience);
        Assert.True(peer.TryUnprotect(outbound, out var decoded));
        Assert.Equal(MakePacket(8).ToArray(), decoded);
        Assert.Equal(MakePacket(8).Length + peer.Size, manager.GetStatistics().Single().SentBytes);
    }

    [Fact]
    public async Task ManagerRejectsUnexpectedIfacAndOversizedUnprotectedPackets()
    {
        var bearer = new MemoryBearer();
        await using var manager = new PacketInterfaceManager([bearer]);
        await manager.StartAsync();
        using var peer = InterfaceAccessCode.Derive("test", "synthetic");
        await bearer.Inject(peer.Protect(MakePacket(1).ToArray()));
        await bearer.Inject(Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[482], mtu: 501).ToArray());
        await bearer.Inject(MakePacket(9).ToArray());
        await using var inbound = manager.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await inbound.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Equal(9, inbound.Current.Payload.Span[0]);
        var stats = manager.GetStatistics().Single();
        Assert.Equal(1, stats.IfacViolations);
        Assert.Equal(2, stats.MalformedPackets);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await manager.SendRawToAsync(new byte[501], bearer));
    }

    [Fact]
    public async Task SpawnedPeersInheritAccessCodeWithoutLosingIngressIdentity()
    {
        var parent = new SpawningBearer();
        await using var manager = new PacketInterfaceManager([parent], channelCapacity: 1);
        manager.SetInterfaceAccessCode(parent, InterfaceAccessCode.Derive("test", "synthetic"));
        await manager.StartAsync();
        var child = new MemoryBearer();
        parent.Connect(child);
        using var peer = InterfaceAccessCode.Derive("test", "synthetic");
        await child.Inject(peer.Protect(MakePacket(3).ToArray()));
        await using var inbound = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();
        Assert.True(await inbound.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Same(child, inbound.Current.Source);
        await manager.SendToAsync(MakePacket(4), child);
        Assert.True(peer.TryUnprotect(await child.Sent.Reader.ReadAsync(), out var response));
        Assert.Equal(MakePacket(4).ToArray(), response);
        Assert.Equal(16, manager.GetStatistics().Single(s => ReferenceEquals(s.Interface, child)).IfacSize);
        await manager.DetachAsync(parent).AsTask().WaitAsync(Patience);
        Assert.Empty(manager.Interfaces);
    }

    [Fact]
    public async Task AuthenticatedBackpressureCanBeCancelledAndDetachedWithoutHanging()
    {
        var bearer = new MemoryBearer();
        await using var manager = new PacketInterfaceManager([], channelCapacity: 1, keepAlive: true);
        await manager.StartAsync();
        await manager.AttachAsync(bearer, accessCode: InterfaceAccessCode.Derive("test", "synthetic"));
        using var peer = InterfaceAccessCode.Derive("test", "synthetic");
        for (byte i = 0; i < 4; i++) await bearer.Inject(peer.Protect(MakePacket(i).ToArray()));
        using var deadline = new CancellationTokenSource(Patience);
        while (manager.GetStatistics().Single().BackpressureWaits == 0) await Task.Delay(5, deadline.Token);
        Assert.True(await manager.DetachAsync(bearer).AsTask().WaitAsync(Patience));
        Assert.Empty(manager.Interfaces);
    }

    private static Packet MakePacket(byte tag) => Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[] { tag, 1 });

    private class MemoryBearer : IPacketInterface
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateBounded<ReadOnlyMemory<byte>>(16);
        public Channel<byte[]> Sent { get; } = Channel.CreateUnbounded<byte[]>();
        public ValueTask Inject(byte[] raw) => _incoming.Writer.WriteAsync(raw);
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) => _incoming.Reader.ReadAllAsync(cancellationToken);
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => Sent.Writer.WriteAsync(packet.ToArray(), cancellationToken);
        public ValueTask DisposeAsync() { _incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class SpawningBearer : MemoryBearer, ISpawningPacketInterface
    {
        public event Action<IPacketInterface>? ChildConnected;
        public bool SendsThroughChildren => true;
        public void Connect(IPacketInterface child) => ChildConnected?.Invoke(child);
    }
}
