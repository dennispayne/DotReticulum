using System.Buffers.Binary;
using DotReticulum.Interfaces;

namespace DotReticulum.Interfaces.Tests;

// Oracle: pinned a29bfb51c468945c96a1ee22cae0114e993e2391, actual methods in
// GenerateParity6WeaveVectors.py. Simulator traffic is not physical/live acceptance.
[Trait("Category", "Unit")]
public sealed class Parity6WeaveTests
{
    private static WeaveOptions FastOptions => new()
    {
        ReconnectDelay = TimeSpan.FromMilliseconds(10),
        HandshakeTimeout = TimeSpan.FromSeconds(1),
        PeerJobInterval = TimeSpan.FromMilliseconds(20),
    };

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task DiscoveryConnectDeliveryAndCommandsMatchIndependentPythonOracle()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var serial = new Parity6WeaveSimulator(fixture);
        await using var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(serial), FastOptions, identity);
        await bearer.StartAsync(default);
        Assert.Equal(fixture.Write(0), await serial.Writes.ReadAsync());
        Assert.Equal(fixture.Write(1), await serial.Writes.ReadAsync());
        Assert.Equal(fixture.Bytes("remote_switch_id"), bearer.SwitchId);
        await UntilAsync(() => bearer.EndpointId is not null);
        Assert.Equal(fixture.Bytes("endpoint"), bearer.EndpointId);
        serial.Alive(fixture.Bytes("endpoint"));
        await UntilAsync(() => bearer.Peers.Count == 1);
        await bearer.Peers[0].SendAsync(fixture.Bytes("payload"), default);
        Assert.Equal(fixture.Write(2), await serial.Writes.ReadAsync());
        await bearer.SendCommandAsync(0x0a00, new byte[] { 1 });
        Assert.Equal(fixture.Write(3), await serial.Writes.ReadAsync());
        await bearer.SendCommandAsync(0x0a00, new byte[] { 0 });
        Assert.Equal(fixture.Write(4), await serial.Writes.ReadAsync());
        await bearer.SendCommandAsync(0x0100, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(fixture.Write(5), await serial.Writes.ReadAsync());
        Assert.Equal(8L, bearer.GetDiagnostics()["txb"]);
        Assert.Equal(0, bearer.GetDiagnostics()["queued_transmit_bytes"]);
        Assert.Equal(1024, bearer.HardwareMtu);
        Assert.Equal(250000L, bearer.Bitrate);
        Assert.True(bearer.SupportsMtuDiscovery);
    }

    [Fact]
    public void FramingAndSignedDiscoveryMatchOracleAndRejectMalformedBoundaries()
    {
        using var fixture = new Parity6WeaveFixture();
        Assert.Equal(fixture.Write(6), WeaveProtocol.Frame([255, 255, 255, 255], WeaveProtocol.Encapsulation, fixture.Bytes("payload")));
        var discovery = fixture.Bytes("discovery_response");
        Assert.True(WeaveProtocol.ValidateDiscovery(discovery, out var key));
        Assert.Equal(fixture.Bytes("remote_public_key"), key);
        discovery[^1] ^= 1;
        Assert.False(WeaveProtocol.ValidateDiscovery(discovery, out _));
        Assert.False(WeaveProtocol.ValidateDiscovery(discovery[..100], out _));
        var decoder = new WeaveProtocol.Decoder();
        var actual = new List<byte[]>();
        foreach (var b in fixture.Write(6)) decoder.Feed([b], actual.Add);
        Assert.Single(actual);
        Assert.Equal(fixture.Bytes("payload"), actual[0][5..]);
        decoder.Feed([0x7e, 1, 2, 3, 4, 5, 0x7d, 0x41, 0x7e], actual.Add);
        decoder.Feed([0x7e, 1, 2, 3, 4, 5, 0x7d, 0x7e], actual.Add);
        var maximum = Enumerable.Repeat((byte)0x11, WeaveProtocol.MaximumFrameLength).ToArray();
        decoder.Feed(WeaveProtocol.Encode(maximum), actual.Add);
        Assert.Equal(maximum, actual[^1]);
        decoder.Feed([0x7e], actual.Add);
        decoder.Feed(Enumerable.Repeat((byte)0x11, WeaveProtocol.MaximumFrameLength + 1).ToArray(), actual.Add);
        decoder.Feed([0x7e], actual.Add);
        decoder.Feed(fixture.Write(6), actual.Add);
        Assert.Equal(3, actual.Count);
        Assert.Equal(3, decoder.DroppedFrames);
        Assert.Throws<ArgumentOutOfRangeException>(() => WeaveProtocol.Encode(new byte[WeaveProtocol.MaximumFrameLength + 1]));
    }

    [Fact]
    public async Task ProcessingWithoutSubscribersPreservesPeerIdentityViaAndSharedDuplicates()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var serial = new Parity6WeaveSimulator(fixture);
        await using var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(serial), FastOptions, identity);
        await bearer.StartAsync(default);
        var a = fixture.Bytes("endpoint");
        var b = new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 };
        serial.Endpoint(a, [10, 20, 30]);
        serial.Endpoint(b, [10, 20, 30]);
        serial.Via(a, [1, 2, 3, 4]);
        await UntilAsync(() => bearer.Peers.Count == 2 && bearer.Peers[0].ViaSwitchId is not null);
        var peer = bearer.Peers.Single(p => p.EndpointId.SequenceEqual(a));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var received = peer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await received.MoveNextAsync());
        Assert.Equal(new byte[] { 10, 20, 30 }, received.Current.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, peer.ViaSwitchId);
        Assert.Equal(3L, bearer.GetDiagnostics()["rxb"]);
        Assert.Equal(0L, bearer.Peers.Single(p => p.EndpointId.SequenceEqual(b)).GetDiagnostics()["rxb"]);
        await bearer.SendAsync(new byte[] { 99 }, default); // Parent is intentionally not an endpoint broadcast.
        Assert.Equal(0L, bearer.GetDiagnostics()["txb"]);
    }

    [Fact]
    public async Task ObserversAreIsolatedAndStructuredDeviceStateIsProcessed()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var serial = new Parity6WeaveSimulator(fixture);
        await using var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(serial), FastOptions, identity);
        var adopted = 0;
        bearer.ChildConnected += _ => throw new InvalidOperationException("broken optional observer");
        bearer.ChildConnected += _ => adopted++;
        bearer.LogReceived += _ => throw new InvalidOperationException("broken log observer");
        bearer.Faulted += _ => throw new InvalidOperationException("broken diagnostic observer");
        byte[]? display = null;
        bearer.DisplayUpdated += data => display = data.ToArray();
        await bearer.StartAsync(default);
        await bearer.SetDisplayEnabledAsync(true);
        serial.Alive(fixture.Bytes("endpoint"));
        serial.Inject(Parity6WeaveSimulator.Log(fixture.Bytes("host_switch_id"), 0xe003, [42]));
        serial.Inject(Parity6WeaveSimulator.Log(fixture.Bytes("host_switch_id"), 0xe005, [0, 0, 0, 25, 0, 0, 0, 100]));
        var payload = new byte[12]; // cf + offset + total-size + three display bytes
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5), 3);
        payload[9] = 7; payload[10] = 8; payload[11] = 9;
        serial.Inject(WeaveProtocol.Frame(fixture.Bytes("host_switch_id"), WeaveProtocol.Display, payload));
        await UntilAsync(() => display is not null && adopted == 1);
        Assert.Equal(new byte[] { 7, 8, 9 }, display);
        Assert.Equal((byte)42, bearer.GetDiagnostics()["cpu_load"]);
        Assert.Equal(75d, bearer.GetDiagnostics()["mem_load"]);
        Assert.True((long)bearer.GetDiagnostics()["observer_errors"]! >= 3);
        Assert.Single(bearer.Peers);
    }

    [Fact]
    public async Task SharedTransmitBoundsCancellationAndFailureAccounting()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var serial = new Parity6WeaveSimulator(fixture);
        await using var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(serial),
            FastOptions with { MaximumQueuedTransmitFrames = 1, MaximumQueuedTransmitBytes = WeaveProtocol.MaximumPacketLength }, identity);
        await bearer.StartAsync(default);
        serial.Alive(fixture.Bytes("endpoint"));
        await UntilAsync(() => bearer.Peers.Count == 1);
        serial.BlockCommands = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = bearer.Peers[0].SendAsync(new byte[1000], default).AsTask();
        await UntilAsync(() => (int)bearer.GetDiagnostics()["queued_transmit_bytes"]! == 1000);
        using var canceled = new CancellationTokenSource();
        var second = bearer.SendCommandAsync(0x0100, new byte[30], canceled.Token).AsTask();
        Assert.False(second.IsCompleted);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, bearer.GetDiagnostics()["queued_transmit_frames"]);
        serial.BlockCommands.TrySetResult();
        await first;
        Assert.Equal(1000L, bearer.GetDiagnostics()["txb"]);
        serial.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => bearer.Peers[0].SendAsync(new byte[1], default).AsTask());
        Assert.Equal(1000L, bearer.GetDiagnostics()["txb"]);
        Assert.Equal(0, bearer.GetDiagnostics()["queued_transmit_bytes"]);
    }

    [Fact]
    public async Task UnplugReopensFreshDeviceAndClosesOldPeers()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var first = new Parity6WeaveSimulator(fixture);
        var second = new Parity6WeaveSimulator(fixture);
        var opens = 0;
        await using var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(Interlocked.Increment(ref opens) == 1 ? first : second), FastOptions, identity);
        var connections = 0;
        bearer.Connected += () => Interlocked.Increment(ref connections);
        await bearer.StartAsync(default);
        first.Alive(fixture.Bytes("endpoint"));
        await UntilAsync(() => bearer.Peers.Count == 1);
        var oldPeer = bearer.Peers[0];
        first.Unplug();
        await UntilAsync(() => connections == 2);
        Assert.False(oldPeer.IsConnected);
        Assert.Empty(bearer.Peers);
        second.Endpoint(fixture.Bytes("endpoint"), [9]);
        await UntilAsync(() => bearer.Peers.Count == 1);
        Assert.NotSame(oldPeer, bearer.Peers[0]);
        Assert.Equal(1L, bearer.GetDiagnostics()["reconnects"]);
    }

    [Fact]
    public async Task PeerLimitReceiveOverflowAndExpiryAreBounded()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var serial = new Parity6WeaveSimulator(fixture);
        var clock = new Parity6WeaveClock();
        await using var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(serial),
            FastOptions with { MaximumPeers = 1, ReceiveQueueCapacity = 1, PeeringTimeout = TimeSpan.FromSeconds(1) }, identity, clock);
        await bearer.StartAsync(default);
        serial.Endpoint(fixture.Bytes("endpoint"), [1]);
        serial.Endpoint(fixture.Bytes("endpoint"), [2]);
        serial.Alive([8, 7, 6, 5, 4, 3, 2, 1]);
        await UntilAsync(() => (long)bearer.GetDiagnostics()["dropped_frames"]! >= 2);
        Assert.Single(bearer.Peers);
        var peer = bearer.Peers[0];
        clock.Advance(TimeSpan.FromSeconds(2));
        await UntilAsync(() => bearer.Peers.Count == 0);
        Assert.False(peer.IsConnected);
        Assert.True(bearer.IsConnected);
    }

    [Fact]
    public async Task StartTimeoutCanceledStartAndFactoryFailureAreObservableAndDisposable()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var serial = new Parity6WeaveSimulator(fixture) { AutoHandshake = false };
        await using (var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(serial),
            FastOptions with { HandshakeTimeout = TimeSpan.FromMilliseconds(40) }, identity))
        {
            var invalid = fixture.Bytes("discovery_response");
            invalid[^1] ^= 1;
            serial.Inject(WeaveProtocol.Encode(invalid));
            await Assert.ThrowsAsync<TimeoutException>(() => bearer.StartAsync(default).AsTask());
            Assert.Null(bearer.SwitchId);
            Assert.False(serial.CanRead);
        }
        await using (var bearer = new WeavePacketInterface(_ => throw new IOException("owned factory failed"), FastOptions, identity))
            await Assert.ThrowsAsync<IOException>(() => bearer.StartAsync(default).AsTask());
        var opens = 0;
        await using (var bearer = new WeavePacketInterface(_ => { opens++; return ValueTask.FromResult<Stream>(new Parity6WeaveSimulator(fixture)); }, FastOptions, identity))
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bearer.StartAsync(canceled.Token).AsTask());
            Assert.Equal(0, opens);
        }
    }

    [Fact]
    public async Task DisposeDuringHandshakeCancelsReaderAndIsIdempotent()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var serial = new Parity6WeaveSimulator(fixture) { AutoHandshake = false };
        var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(serial), FastOptions, identity);
        var start = bearer.StartAsync(default).AsTask();
        await serial.Writes.ReadAsync();
        await bearer.DisposeAsync();
        await bearer.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.False(serial.CanRead);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bearer.StartAsync(default).AsTask());
    }

    [Fact]
    public async Task MalformedLogDisplayAndEndpointFramesDoNotTerminateDevice()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var serial = new Parity6WeaveSimulator(fixture);
        await using var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(serial), FastOptions, identity);
        await bearer.StartAsync(default);
        serial.Inject(WeaveProtocol.Frame(fixture.Bytes("host_switch_id"), WeaveProtocol.Log, [0]));
        serial.Inject(WeaveProtocol.Frame(fixture.Bytes("host_switch_id"), WeaveProtocol.Display, [0, 255, 255, 255, 255, 255, 255, 255, 255]));
        serial.Inject(WeaveProtocol.Frame(fixture.Bytes("host_switch_id"), WeaveProtocol.EndpointPacket, [1]));
        serial.Inject(WeaveProtocol.Frame([1, 2, 3, 4], WeaveProtocol.EndpointPacket, new byte[12]));
        serial.Endpoint(fixture.Bytes("endpoint"), [9]);
        await UntilAsync(() => bearer.Peers.Count == 1);
        Assert.True(bearer.IsConnected);
        Assert.True((long)bearer.GetDiagnostics()["dropped_frames"]! >= 4);
    }

    [Fact]
    public async Task DisposalCancelsBlockedTransmitAndDrainsReservations()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var serial = new Parity6WeaveSimulator(fixture);
        var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(serial), FastOptions, identity);
        try
        {
            await bearer.StartAsync(default);
            serial.Alive(fixture.Bytes("endpoint"));
            await UntilAsync(() => bearer.Peers.Count == 1);
            serial.BlockCommands = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var sending = bearer.Peers[0].SendAsync(new byte[1000], default).AsTask();
            await UntilAsync(() => (int)bearer.GetDiagnostics()["queued_transmit_frames"]! == 1);
            await bearer.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Assert.Equal(0, bearer.GetDiagnostics()["queued_transmit_frames"]);
            Assert.Equal(0, bearer.GetDiagnostics()["queued_transmit_bytes"]);
            Assert.False(serial.CanRead);
        }
        finally { await bearer.DisposeAsync(); }
    }

    [Fact]
    public async Task PacketMtuAndDuplicateTtlBoundariesAreEnforced()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var serial = new Parity6WeaveSimulator(fixture);
        var clock = new Parity6WeaveClock();
        await using var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(serial), FastOptions, identity, clock);
        await bearer.StartAsync(default);
        var maximum = new byte[WeaveProtocol.MaximumPacketLength];
        serial.Endpoint(fixture.Bytes("endpoint"), maximum);
        await UntilAsync(() => (long)bearer.GetDiagnostics()["rxb"]! == maximum.Length);
        var peer = bearer.Peers[0];
        await peer.SendAsync(maximum, default);
        Assert.Equal((long)maximum.Length, bearer.GetDiagnostics()["txb"]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => peer.SendAsync(ReadOnlyMemory<byte>.Empty, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => peer.SendAsync(new byte[maximum.Length + 1], default).AsTask());
        serial.Endpoint(fixture.Bytes("endpoint"), new byte[maximum.Length + 1]);
        await UntilAsync(() => (long)bearer.GetDiagnostics()["dropped_frames"]! == 1);
        clock.Advance(TimeSpan.FromSeconds(0.75));
        serial.Endpoint(fixture.Bytes("endpoint"), maximum);
        await UntilAsync(() => (long)bearer.GetDiagnostics()["rxb"]! == 2 * maximum.Length);
    }

    [Fact]
    public async Task EffectiveConfiguredBitrateIsConsistentForParentAndChildren()
    {
        using var fixture = new Parity6WeaveFixture();
        using var identity = fixture.HostIdentity();
        var serial = new Parity6WeaveSimulator(fixture);
        await using var bearer = new WeavePacketInterface(_ => ValueTask.FromResult<Stream>(serial),
            FastOptions with { ConfiguredBitrate = 372000 }, identity);
        await bearer.StartAsync(default);
        serial.Alive(fixture.Bytes("endpoint"));
        await UntilAsync(() => bearer.Peers.Count == 1);
        Assert.Equal(372000L, bearer.Bitrate);
        Assert.Equal(372000L, bearer.Peers[0].Bitrate);
        Assert.Equal(372000L, bearer.GetDiagnostics()["bitrate"]);
        Assert.Equal(372000L, bearer.Peers[0].GetDiagnostics()["bitrate"]);
        Assert.Equal("configured_estimate", bearer.GetDiagnostics()["dotreticulum_bitrate_kind"]);
        Assert.Equal(16, WeavePacketInterface.DefaultIfacSize);
    }
}
