using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotReticulum.Core;
using static DotReticulum.Interfaces.Tests.TestSupport;

namespace DotReticulum.Interfaces.Tests;

// GenerateParity6StreamVectors.py executes unmodified RNS 1.5.7
// a29bfb51c468945c96a1ee22cae0114e993e2391; source hashes are recorded in the fixture.
// All ports below are loopback sockets or owned stream simulators, never serial hardware.
public sealed class Parity6StreamTests
{
    private static JsonElement Vectors { get; } = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-streams.json"))).RootElement;
    private static byte[] Vector(string name) => Convert.FromHexString(Vectors.GetProperty(name).GetString()!);

    [Fact]
    public void FramingAndSerialDelimiterVectorsMatchReference()
    {
        var payload = Vector("payload");
        var hdlc = Vector("hdlc");
        Assert.Equal(hdlc, HdlcPacketFraming.Encode(payload, 564, validatePacket: false));
        Assert.Equal(Vector("kiss"), TcpKissFrameDecoder.Encode(payload));
        var kiss = new TcpKissFrameDecoder(564);
        var decoded = new List<byte[]>();
        foreach (var value in Vector("kiss")) kiss.Feed([value], decoded);
        Assert.Equal(payload, Assert.Single(decoded));

        foreach (var (key, input) in new[]
        {
            ("serial_separate_flags", hdlc.Concat(hdlc).ToArray()),
            ("serial_shared_flag", hdlc.Concat(hdlc.AsSpan(1).ToArray()).ToArray()),
        })
        {
            decoded.Clear();
            var decoder = new HdlcFrameDecoder(564, validatePackets: false, sharedFlags: false);
            foreach (var value in input) decoder.Feed([value], decoded);
            Assert.Equal(Vectors.GetProperty(key).GetArrayLength(), decoded.Count);
            foreach (var frame in decoded) Assert.Equal(payload, frame);
        }
    }

    [Fact]
    public void IdleExpiryAndMalformedInputHaveBoundedRecovery()
    {
        var decoder = new HdlcFrameDecoder(564, validatePackets: false, sharedFlags: false);
        var decoded = new List<byte[]>();
        var hdlc = Vector("hdlc");
        decoder.Feed(hdlc.AsSpan(0, 3), decoded);
        Assert.True(decoder.HasPartialData);
        decoder.ExpirePartial();
        decoder.Feed(hdlc.AsSpan(3), decoded);
        decoder.Feed([0x7e], decoded);
        decoder.Feed(hdlc, decoded);
        Assert.Equal(Vector("payload"), Assert.Single(decoded));
        Assert.Equal(Vectors.GetProperty("serial_idle_expired")[0].GetString(), Convert.ToHexString(decoded[0]).ToLowerInvariant());

        var kiss = new TcpKissFrameDecoder(564);
        decoded.Clear();
        kiss.Feed([0xc0, 0, 1, 2, 0xdb, 0x01, 3, 0xc0], decoded);
        kiss.Feed(new byte[] { 0xc0, 0 }.Concat(Enumerable.Repeat((byte)1, 565)).Append((byte)0xc0).ToArray(), decoded);
        kiss.Feed(Vector("kiss"), decoded);
        Assert.Equal(Vector("payload"), Assert.Single(decoded));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReconnectLimitCountsActualCallsNotAnExtraAttempt(int maximum)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var client = new TcpClientPacketInterface("127.0.0.1", port, reconnect: true,
            maxReconnectAttempts: maximum, reconnectDelay: TimeSpan.FromMilliseconds(5),
            connectTimeout: TimeSpan.FromMilliseconds(200));
        await client.StartAsync(default);
        await using var receiver = client.ReceiveAsync(default).GetAsyncEnumerator();
        Assert.False(await receiver.MoveNextAsync().AsTask().WaitAsync(Patience));
        var expected = Vectors.GetProperty("retry_limits").EnumerateArray().Single(
            item => item.GetProperty("maximum").GetInt32() == maximum).GetProperty("connect_calls").GetInt32();
        Assert.Equal(expected, client.GetDiagnostics()["reconnect_attempts"]);
        Assert.Contains("limit", Assert.IsType<string>(client.GetDiagnostics()["last_error"]));
        await client.SendAsync(MakePacket(1), default);
        Assert.Equal(1L, client.GetDiagnostics()["offline_sends"]);
    }

    [Fact]
    public async Task TcpKissUsesTcpMtuAndNoTunnelAndExchangesBothDirections()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var client = new TcpClientPacketInterface("127.0.0.1",
                ((IPEndPoint)listener.LocalEndpoint).Port, fixedMtu: 2048, kissFraming: true);
            var accept = listener.AcceptSocketAsync();
            await client.StartAsync(default);
            using var socket = await accept.WaitAsync(Patience);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            Assert.False(client.WantsTunnel);
            Assert.True(client.FixedMtu);
            Assert.Equal(2048, client.HardwareMtu);
            var packet = Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], new byte[1200], mtu: 2048).ToArray();
            var expected = TcpKissFrameDecoder.Encode(packet);
            await client.SendAsync(packet, default);
            var actual = new byte[expected.Length];
            await stream.ReadExactlyAsync(actual).AsTask().WaitAsync(Patience);
            Assert.Equal(expected, actual);
            // Port nibble is ignored, non-data commands are not packets.
            await stream.WriteAsync(new byte[] { 0xc0, 1, 1, 2, 3, 0xc0 });
            expected[1] = 0x70;
            await stream.WriteAsync(expected);
            await using var incoming = client.ReceiveAsync(default).GetAsyncEnumerator();
            Assert.Equal(packet, (await Next(incoming)).ToArray());
            await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(new byte[2049], default).AsTask());
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TcpSendingRemainsOperationalAfterReadinessEnumeratorIsDisposed(bool kissFraming)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var client = new TcpClientPacketInterface("127.0.0.1",
                ((IPEndPoint)listener.LocalEndpoint).Port, kissFraming: kissFraming);
            var accept = listener.AcceptSocketAsync();
            await client.StartAsync(default);
            using var socket = await accept.WaitAsync(Patience);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var ready = MakePacket(31);
            await stream.WriteAsync(kissFraming ? TcpKissFrameDecoder.Encode(ready) : HdlcPacketFraming.Encode(ready));
            await using (var readiness = client.ReceiveAsync(default).GetAsyncEnumerator())
                Assert.Equal(ready, (await Next(readiness)).ToArray());

            Assert.True(client.IsConnected);
            var outgoing = MakePacket(32);
            await client.SendAsync(outgoing, default);
            var expected = kissFraming ? TcpKissFrameDecoder.Encode(outgoing) : HdlcPacketFraming.Encode(outgoing);
            var actual = new byte[expected.Length];
            await stream.ReadExactlyAsync(actual).AsTask().WaitAsync(Patience);
            Assert.Equal(expected, actual);
            Assert.Equal(0L, client.GetDiagnostics()["offline_sends"]);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task TcpResolutionHonorsLiteralIpv6AndServerFamilyPreference()
    {
        Assert.Equal(AddressFamily.InterNetwork, (await TcpAddressResolver.ResolveListenEndpointAsync("127.0.0.1", 0)).AddressFamily);
        Assert.Equal(AddressFamily.InterNetworkV6, (await TcpAddressResolver.ResolveListenEndpointAsync("::1", 0)).AddressFamily);
        Assert.Equal(IPAddress.Any, (await TcpAddressResolver.ResolveListenEndpointAsync("0.0.0.0", 0, preferIpv6: true)).Address);
        Assert.Equal(IPAddress.IPv6Any, (await TcpAddressResolver.ResolveListenEndpointAsync("::", 0)).Address);
        var addresses = await Dns.GetHostAddressesAsync("localhost");
        var expected4 = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        var expected6 = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6) ?? addresses[0];
        Assert.Equal(expected4, (await TcpAddressResolver.ResolveListenEndpointAsync("localhost", 0)).Address);
        Assert.Equal(expected6, (await TcpAddressResolver.ResolveListenEndpointAsync("localhost", 0, preferIpv6: true)).Address);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TcpAddressResolver.ResolveListenEndpointAsync("localhost", 0, cancellationToken: cancelled.Token).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            TcpAddressResolver.ResolveListenEndpointAsync(null, 0, device: "DOTRETICULUM_NO_SUCH_DEVICE").AsTask());
    }

    [Fact]
    public async Task TcpLocalhostReachesIpv4OnlyListenerWithoutConsumingRetry()
    {
        await using var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0));
        await server.StartAsync(default);
        await using var client = new TcpClientPacketInterface("localhost", server.LocalEndpoint!.Port, reconnect: true,
            maxReconnectAttempts: 0);
        var addresses = await Dns.GetHostAddressesAsync("localhost");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await client.StartAsync(default);
        Assert.True(client.IsConnected,
            $"localhost addresses=[{string.Join(", ", addresses.Select(address => address.ToString()))}], " +
            $"listener={server.LocalEndpoint}, clients={server.ClientCount}, " +
            $"elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(started)}, " +
            $"client diagnostics: {string.Join(", ", client.GetDiagnostics().Select(entry => $"{entry.Key}={entry.Value}"))}; " +
            $"server diagnostics: {string.Join(", ", server.GetDiagnostics().Select(entry => $"{entry.Key}={entry.Value}"))}");
        Assert.Equal(0, client.GetDiagnostics()["reconnect_attempts"]);
        await using var incoming = server.ReceiveAsync(default).GetAsyncEnumerator();
        await client.SendAsync(MakePacket(33), default);
        Assert.Equal(33, TagOf(await Next(incoming)));
        Assert.Equal(1, server.ClientCount);
    }

    [Fact]
    public async Task TcpServerResolvesLazilyAndDiagnosesInvalidDeviceAtStart()
    {
        await using var server = new TcpServerPacketInterface("localhost", 0, nominalBitrate: 5_000_000);
        Assert.Null(server.LocalEndpoint);
        Assert.Equal(5_000_000L, server.Bitrate);
        await server.StartAsync(default);
        Assert.Equal((await TcpAddressResolver.ResolveListenEndpointAsync("localhost", 0)).Address, server.LocalEndpoint!.Address);

        await using var invalid = new TcpServerPacketInterface("ignored.invalid", 0, device: "DOTRETICULUM_NO_SUCH_DEVICE");
        Assert.Null(invalid.LocalEndpoint);
        await Assert.ThrowsAsync<ArgumentException>(() => invalid.StartAsync(default).AsTask());
        Assert.False(Assert.IsType<bool>(invalid.GetDiagnostics()["status"]));
        Assert.Contains("device", Assert.IsType<string>(invalid.GetDiagnostics()["last_error"]));

        await using var wildcard = new TcpServerPacketInterface(listenHost: null, port: 0, preferIpv6: true);
        await wildcard.StartAsync(default);
        Assert.Equal(IPAddress.Any, wildcard.LocalEndpoint!.Address);
    }

    [Parity6StreamIpv6Fact]
    public async Task TcpIpv6LoopbackUsesHdlcAndPreservesFixedMtu()
    {
        await using var server = new TcpServerPacketInterface("::1", 0, preferIpv6: true, fixedMtu: 1024);
        await server.StartAsync(default);
        Assert.Equal(AddressFamily.InterNetworkV6, server.LocalEndpoint!.AddressFamily);
        await using var client = new TcpClientPacketInterface("::1", server.LocalEndpoint.Port, fixedMtu: 1024);
        await client.StartAsync(default);
        Assert.True(client.WantsTunnel);
        await using var incoming = server.ReceiveAsync(default).GetAsyncEnumerator();
        var packet = Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], new byte[1005], mtu: 1024).ToArray();
        Assert.Equal(1024, packet.Length);
        await client.SendAsync(packet, default);
        Assert.Equal(packet, (await Next(incoming)).ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(new byte[1025], default).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TcpSocketOptionsMatchReferenceOnExecutingOperatingSystem(bool i2pTunneled)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        TcpSocketSettings.Apply(socket, i2pTunneled);
        Assert.True(socket.NoDelay);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Assert.NotEqual(0, (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
            Assert.Equal(i2pTunneled ? 10 : 5, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!);
        }

        else Assert.Equal(0, (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(i2pTunneled ? 9 : 2, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!);
            Assert.Equal(i2pTunneled ? 5 : 12, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!);
        }
    }

    [Fact]
    public async Task SerialFactoryRetriesInitialFailureAndUnplugWithoutReusingOldStream()
    {
        var (first, firstPeer) = CreateDuplexPair();
        var (second, secondPeer) = CreateDuplexPair();
        await using var peer1 = firstPeer;
        await using var peer2 = secondPeer;
        var opens = 0;
        await using var serial = new SerialPacketInterface(_ =>
        {
            var attempt = Interlocked.Increment(ref opens);
            if (attempt == 1) throw new IOException("simulated initial open failure");
            return ValueTask.FromResult(attempt == 2 ? first : second);
        }, reconnectDelay: TimeSpan.FromMilliseconds(20), openDelay: TimeSpan.Zero);
        var connected = 0;
        var disconnected = 0;
        serial.Connected += () => Interlocked.Increment(ref connected);
        serial.Connected += () => throw new InvalidOperationException("optional observer");
        serial.Disconnected += () => Interlocked.Increment(ref disconnected);
        await serial.StartAsync(default);
        await using var incoming = serial.ReceiveAsync(default).GetAsyncEnumerator();
        await WaitFor(() => serial.IsConnected);
        await firstPeer.WriteAsync(HdlcPacketFraming.Encode(MakePacket(11)));
        Assert.Equal(11, TagOf(await Next(incoming)));
        await firstPeer.DisposeAsync();
        await WaitFor(() => Volatile.Read(ref connected) == 2);
        await secondPeer.WriteAsync(HdlcPacketFraming.Encode(MakePacket(12)));
        Assert.Equal(12, TagOf(await Next(incoming)));
        Assert.Equal(1, Volatile.Read(ref disconnected));
        Assert.Equal(3, Volatile.Read(ref opens));
        Assert.Equal(2, serial.GetDiagnostics()["reconnect_attempts"]);
        Assert.Contains("optional observer", Assert.IsType<string>(serial.GetDiagnostics()["last_error"]));
    }

    [Fact]
    public async Task SerialIdlePartialFrameExpiresWhileReadRemainsPending()
    {
        var (device, peer) = CreateDuplexPair();
        await using var remote = peer;
        await using var serial = new SerialPacketInterface(device);
        await serial.StartAsync(default);
        await using var incoming = serial.ReceiveAsync(default).GetAsyncEnumerator();
        var pending = incoming.MoveNextAsync().AsTask();
        var frame = HdlcPacketFraming.Encode(MakePacket(21));
        await peer.WriteAsync(frame.AsMemory(0, 3));
        await Task.Delay(300);
        await peer.WriteAsync(frame.AsMemory(3));
        await peer.WriteAsync(new byte[] { 0x7e });
        await peer.WriteAsync(HdlcPacketFraming.Encode(MakePacket(22)));
        Assert.True(await pending.WaitAsync(Patience));
        Assert.Equal(22, TagOf(incoming.Current));
    }

    [Fact]
    public async Task SerialRetryAndBoundedReceiveBackpressureAreInterruptedByDispose()
    {
        var opens = 0;
        var retrying = new SerialPacketInterface(_ =>
        {
            Interlocked.Increment(ref opens);
            throw new IOException("offline");
        }, reconnectDelay: TimeSpan.FromHours(1), openDelay: TimeSpan.Zero);
        await retrying.StartAsync(default);
        await retrying.SendAsync(MakePacket(1), default);
        Assert.Equal(1L, retrying.GetDiagnostics()["offline_sends"]);
        await Task.WhenAll(retrying.DisposeAsync().AsTask(), retrying.DisposeAsync().AsTask()).WaitAsync(Patience);
        Assert.Equal(1, Volatile.Read(ref opens));

        var (stream, peer) = CreateDuplexPair();
        await using var remote = peer;
        var serial = new SerialPacketInterface(_ => ValueTask.FromResult(stream), openDelay: TimeSpan.Zero);
        await serial.StartAsync(default);
        var frame = HdlcPacketFraming.Encode(MakePacket(1));
        for (var i = 0; i < 300; i++) await peer.WriteAsync(frame);
        await serial.DisposeAsync().AsTask().WaitAsync(Patience);
        Assert.False(serial.IsConnected);
    }

    [Fact]
    public async Task SerialOpenDisposeRaceDisposesLateOwnedStream()
    {
        var opened = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new BlockingDuplexStream();
        var serial = new SerialPacketInterface(async _ =>
        {
            entered.SetResult();
            return await opened.Task;
        }, openDelay: TimeSpan.Zero);
        var start = serial.StartAsync(default).AsTask();
        await entered.Task.WaitAsync(Patience);
        await serial.DisposeAsync();
        opened.SetResult(stream);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task SerialStalledWriteAndQueuedSendCancelAndDisposeWithoutInterleaving()
    {
        var stream = new BlockingDuplexStream();
        var serial = new SerialPacketInterface(stream);
        await serial.StartAsync(default);
        var first = serial.SendAsync(MakePacket(1), default).AsTask();
        await stream.WriteEntered.Task.WaitAsync(Patience);
        using var cancelled = new CancellationTokenSource();
        var second = serial.SendAsync(MakePacket(2), cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, stream.Writes);
        await serial.DisposeAsync().AsTask().WaitAsync(Patience);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first);
    }

    [Fact]
    public async Task SerialFailedWriteIsReportedToCallerAndReopensOwnedStream()
    {
        var failed = new BlockingDuplexStream(failWrites: true);
        var (next, peer) = CreateDuplexPair();
        await using var remote = peer;
        var opens = 0;
        await using var serial = new SerialPacketInterface(_ =>
            ValueTask.FromResult(Interlocked.Increment(ref opens) == 1 ? (Stream)failed : next),
            reconnectDelay: TimeSpan.FromMilliseconds(10), openDelay: TimeSpan.Zero);
        await serial.StartAsync(default);
        await Assert.ThrowsAsync<IOException>(() => serial.SendAsync(MakePacket(1), default).AsTask());
        await WaitFor(() => Volatile.Read(ref opens) == 2 && serial.IsConnected);
        Assert.True(failed.Disposed);
        Assert.Contains("simulated write failure", Assert.IsType<string>(serial.GetDiagnostics()["last_error"]));
        await serial.SendAsync(MakePacket(2), default);
        var expected = HdlcPacketFraming.Encode(MakePacket(2));
        var actual = new byte[expected.Length];
        await peer.ReadExactlyAsync(actual).AsTask().WaitAsync(Patience);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SerialReceiveCancellationClosesOwnedReadThatIgnoresCancellation()
    {
        var stream = new BlockingDuplexStream(ignoreReadCancellation: true);
        await using var serial = new SerialPacketInterface(stream);
        await serial.StartAsync(default);
        using var cancelled = new CancellationTokenSource();
        await using var incoming = serial.ReceiveAsync(cancelled.Token).GetAsyncEnumerator();
        var pending = incoming.MoveNextAsync().AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Patience));
        Assert.True(stream.Disposed);
        Assert.False(serial.IsConnected);
    }

    [Fact]
    public async Task SerialActiveWriteCancellationClosesOwnedWriteThatIgnoresCancellation()
    {
        var stream = new BlockingDuplexStream(ignoreWriteCancellation: true);
        await using var serial = new SerialPacketInterface(stream);
        await serial.StartAsync(default);
        using var cancelled = new CancellationTokenSource();
        var pending = serial.SendAsync(MakePacket(1), cancelled.Token).AsTask();
        await stream.WriteEntered.Task.WaitAsync(Patience);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Patience));
        Assert.True(stream.Disposed);
        Assert.False(serial.IsConnected);
    }

    [Fact]
    public async Task SerialConfigurationValidatesWithoutOpeningHardware()
    {
        var serial = new SerialPacketInterface("NOT_OPENED", 19200, Parity.Even, 7, StopBits.Two, reconnect: true);
        Assert.Equal(7, serial.DataBits);
        Assert.Equal(Parity.Even, serial.Parity);
        Assert.Equal(StopBits.Two, serial.StopBits);
        Assert.Equal(19200L, serial.Bitrate);
        await serial.DisposeAsync();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SerialPacketInterface("NOT_OPENED", dataBits: 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SerialPacketInterface("NOT_OPENED", parity: Parity.Mark));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SerialPacketInterface("NOT_OPENED", stopBits: StopBits.None));
    }

    private sealed class BlockingDuplexStream(bool failWrites = false, bool ignoreReadCancellation = false,
        bool ignoreWriteCancellation = false) : Stream
    {
        private readonly TaskCompletionSource<int> _readClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public int Writes;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (ignoreReadCancellation) return await _readClosed.Task;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Writes);
            WriteEntered.TrySetResult();
            if (failWrites) throw new IOException("simulated write failure");
            if (ignoreWriteCancellation) { await _readClosed.Task; return; }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _readClosed.TrySetResult(0);
            base.Dispose(disposing);
        }
    }
}

public sealed class Parity6StreamIpv6FactAttribute : FactAttribute
{
    public Parity6StreamIpv6FactAttribute()
    {
        if (!Socket.OSSupportsIPv6) Skip = "The OS does not support IPv6 loopback sockets.";
    }
}
