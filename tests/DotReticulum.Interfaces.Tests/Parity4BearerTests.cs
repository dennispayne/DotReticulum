using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;
using static DotReticulum.Interfaces.Tests.TestSupport;

namespace DotReticulum.Interfaces.Tests;

/// <summary>
/// RNS a29bfb51c468945c96a1ee22cae0114e993e2391 TCPInterface.py initial_connect/reconnect/read_loop
/// and Interface.py optimise_mtu. These are native bearer lifecycle tests, not upgraded resource/channel claims.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Parity4BearerTests
{
    [Theory]
    [InlineData(62499L, 500)]
    [InlineData(62500L, 1024)]
    [InlineData(10_000_000L, 16384)]
    [InlineData(750_000_000L, 262144)]
    [InlineData(1_000_000_000L, 524288)]
    public async Task TcpCapabilitiesFollowConfiguredBitrate(long bitrate, int mtu)
    {
        await using var client = new TcpClientPacketInterface("127.0.0.1", 1, bitrate);
        await using var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0), nominalBitrate: bitrate);
        Assert.Equal(bitrate, client.Bitrate);
        Assert.Equal(mtu, client.HardwareMtu);
        Assert.Equal(mtu, server.HardwareMtu);
        Assert.True(client.SupportsMtuDiscovery);
        Assert.True(server.SupportsMtuDiscovery);
        await using var fixedClient = new TcpClientPacketInterface("127.0.0.1", 1, bitrate, fixedMtu: 4096);
        Assert.Equal(4096, fixedClient.HardwareMtu);
    }

    [Fact]
    public async Task NonDiscoveringBearersExposeActualHardwareLimits()
    {
        await using var udp = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, 0), new IPEndPoint(IPAddress.Loopback, 1), 64000);
        Assert.Equal(1064, udp.HardwareMtu);
        Assert.Equal(64000, udp.Bitrate);
        Assert.False(((IPacketInterface)udp).SupportsMtuDiscovery);
        var (a, b) = CreateDuplexPair();
        await using var peer = b;
        await using var serial = new SerialPacketInterface(a, nominalBitrate: 1200);
        Assert.Equal(564, serial.HardwareMtu);
        Assert.Equal(1200, serial.Bitrate);
        Assert.False(((IPacketInterface)serial).SupportsMtuDiscovery);
    }

    [Fact]
    public async Task InitialUnavailablePeerRecoversWithoutReplacingInterface()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var client = new TcpClientPacketInterface("127.0.0.1", port, reconnect: true,
            reconnectDelay: TimeSpan.FromMilliseconds(20), connectTimeout: TimeSpan.FromMilliseconds(200));
        var connected = 0;
        client.Connected += () => Interlocked.Increment(ref connected);
        await client.StartAsync(default);
        Assert.False(client.IsConnected);
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(Patience);
            await WaitFor(() => client.IsConnected && Volatile.Read(ref connected) == 1);
            await using var inbound = client.ReceiveAsync(default).GetAsyncEnumerator();
            await accepted.GetStream().WriteAsync(HdlcPacketFraming.Encode(MakePacket(3)));
            Assert.Equal(3, TagOf(await Next(inbound)));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task CleanDisconnectReconnectsWithOneReceiveEnumerationAndNotifications()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var client = new TcpClientPacketInterface("127.0.0.1", port, reconnect: true,
                reconnectDelay: TimeSpan.FromMilliseconds(20));
            var connected = 0;
            var disconnected = 0;
            client.Connected += () => throw new InvalidOperationException("Optional connection observer");
            client.Connected += () => Interlocked.Increment(ref connected);
            client.Disconnected += () => Interlocked.Increment(ref disconnected);
            await client.StartAsync(default);
            var first = await listener.AcceptTcpClientAsync().WaitAsync(Patience);
            await using var inbound = client.ReceiveAsync(default).GetAsyncEnumerator();
            await first.GetStream().WriteAsync(HdlcPacketFraming.Encode(MakePacket(1)));
            Assert.Equal(1, TagOf(await Next(inbound)));
            first.Dispose();
            using var second = await listener.AcceptTcpClientAsync().WaitAsync(Patience);
            await WaitFor(() => Volatile.Read(ref connected) == 2 && Volatile.Read(ref disconnected) == 1);
            await second.GetStream().WriteAsync(HdlcPacketFraming.Encode(MakePacket(2)));
            Assert.Equal(2, TagOf(await Next(inbound)));
            await client.SendAsync(MakePacket(4), default);
            var buffer = new byte[HdlcPacketFraming.MaxEncodedLength(MakePacket(4).Length)];
            var count = await second.GetStream().ReadAsync(buffer).AsTask().WaitAsync(Patience);
            var frames = new List<byte[]>();
            new HdlcFrameDecoder().Feed(buffer.AsSpan(0, count), frames);
            Assert.Equal(4, TagOf(Assert.Single(frames)));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RetryLimitIsBoundedAndOfflineSendsAreNotQueued()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var client = new TcpClientPacketInterface("127.0.0.1", port, reconnect: true,
            maxReconnectAttempts: 3, reconnectDelay: TimeSpan.FromMilliseconds(5),
            connectTimeout: TimeSpan.FromMilliseconds(200));
        await client.StartAsync(default);
        await client.SendAsync(MakePacket(1), default);
        await using var inbound = client.ReceiveAsync(default).GetAsyncEnumerator();
        Assert.False(await inbound.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Equal(3, client.GetDiagnostics()["reconnect_attempts"]);
        Assert.False(client.IsConnected);
        Assert.Equal(0L, client.GetDiagnostics()["txb"]);
    }

    [Fact]
    public async Task DisposalCancelsLongReconnectDelayAndCancelledReceiver()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var client = new TcpClientPacketInterface("127.0.0.1", port, reconnect: true,
            reconnectDelay: TimeSpan.FromHours(1), connectTimeout: TimeSpan.FromMilliseconds(200));
        await client.StartAsync(default);
        using var cancelled = new CancellationTokenSource();
        await using var inbound = client.ReceiveAsync(cancelled.Token).GetAsyncEnumerator();
        var waiting = inbound.MoveNextAsync().AsTask();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask()).WaitAsync(Patience);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task DisposalInterruptsBoundedReceiveBackpressure()
    {
        await using var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0));
        await server.StartAsync(default);
        var client = new TcpClientPacketInterface("127.0.0.1", server.LocalEndpoint!.Port, reconnect: true);
        await client.StartAsync(default);
        await WaitFor(() => server.ClientCount == 1);
        var packet = MakePacket(7);
        for (var i = 0; i < 270; i++) await server.SendAsync(packet, default);
        await WaitFor(() => Convert.ToInt64(client.GetDiagnostics()["rxb"]) >= 257 * packet.Length);
        await client.DisposeAsync().AsTask().WaitAsync(Patience);
    }

    [Fact]
    public async Task DisposingReconnectingBearerCompletesPendingReceiveWithoutCallerCancellation()
    {
        await using var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0));
        await server.StartAsync(default);
        var client = new TcpClientPacketInterface("127.0.0.1", server.LocalEndpoint!.Port, reconnect: true);
        await client.StartAsync(default);
        await using var incoming = client.ReceiveAsync(default).GetAsyncEnumerator();
        var pending = incoming.MoveNextAsync().AsTask();
        await client.DisposeAsync().AsTask().WaitAsync(Patience);
        Assert.False(await pending.WaitAsync(Patience));
    }

    [Fact]
    public async Task ManagerForwardsConnectionEventsWithoutLettingObserverBreakProcessing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var client = new TcpClientPacketInterface("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                reconnect: true, reconnectDelay: TimeSpan.FromMilliseconds(10));
            await using var manager = new PacketInterfaceManager([client], keepAlive: true);
            var connected = 0;
            var disconnected = 0;
            manager.InterfaceConnected += _ => throw new InvalidOperationException("Optional observer");
            manager.InterfaceConnected += iface => { Assert.Same(client, iface); Interlocked.Increment(ref connected); };
            manager.InterfaceDisconnected += iface => { Assert.Same(client, iface); Interlocked.Increment(ref disconnected); };
            await manager.StartAsync();
            var first = await listener.AcceptTcpClientAsync().WaitAsync(Patience);
            first.Dispose();
            using var second = await listener.AcceptTcpClientAsync().WaitAsync(Patience);
            await WaitFor(() => Volatile.Read(ref connected) == 2 && Volatile.Read(ref disconnected) == 1);
            await second.GetStream().WriteAsync(HdlcPacketFraming.Encode(MakePacket(9)));
            await using var inbound = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();
            Assert.True(await inbound.MoveNextAsync().AsTask().WaitAsync(Patience));
            Assert.Same(client, inbound.Current.Source);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task AcceptedResetCannotKillListenerOrOtherPeer()
    {
        var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0), perClientInterfaces: true);
        await using var manager = new PacketInterfaceManager([server], keepAlive: true);
        await manager.StartAsync();
        using var bad = new TcpClient();
        await bad.ConnectAsync(IPAddress.Loopback, server.LocalEndpoint!.Port);
        await using var good = new TcpClientPacketInterface("127.0.0.1", server.LocalEndpoint.Port);
        await good.StartAsync(default);
        await WaitFor(() => manager.Interfaces.Count == 3);
        bad.Client.LingerState = new LingerOption(true, 0);
        bad.Dispose();
        await WaitFor(() => manager.Interfaces.Count == 2);
        await good.SendAsync(MakePacket(22), default);
        await using var inbound = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();
        Assert.True(await inbound.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Equal(22, TagOf(inbound.Current.Raw));
        Assert.Equal(server.HardwareMtu, inbound.Current.Source.HardwareMtu);
        Assert.True(inbound.Current.Source.SupportsMtuDiscovery);
    }

    [Fact]
    public async Task IfacFramesAtStandardAndUpgradedBearerBoundsExchangeBothDirections()
    {
        var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0), perClientInterfaces: true);
        await using var serverManager = new PacketInterfaceManager([server], keepAlive: true);
        serverManager.SetInterfaceAccessCode(server, InterfaceAccessCode.Derive("parity4", "synthetic"));
        await serverManager.StartAsync();
        var client = new TcpClientPacketInterface("127.0.0.1", server.LocalEndpoint!.Port);
        await using var clientManager = new PacketInterfaceManager([client], keepAlive: true);
        clientManager.SetInterfaceAccessCode(client, InterfaceAccessCode.Derive("parity4", "synthetic"));
        await clientManager.StartAsync();
        await using var serverIn = serverManager.ReceiveWithSourceAsync().GetAsyncEnumerator();
        await using var clientIn = clientManager.ReceiveWithSourceAsync().GetAsyncEnumerator();
        foreach (var length in new[] { 500, 8161, 16384 })
        {
            var raw = Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16],
                Enumerable.Range(0, length - Packet.Header1Size).Select(i => unchecked((byte)i)).ToArray(),
                mtu: length);
            await clientManager.SendToAsync(raw, client);
            Assert.True(await serverIn.MoveNextAsync().AsTask().WaitAsync(Patience));
            Assert.Equal(raw.ToArray(), serverIn.Current.Raw.ToArray());
            await serverManager.SendToAsync(raw, serverIn.Current.Source);
            Assert.True(await clientIn.MoveNextAsync().AsTask().WaitAsync(Patience));
            Assert.Equal(raw.ToArray(), clientIn.Current.Raw.ToArray());
        }
        Assert.Equal(0, serverManager.GetStatistics().Sum(s => s.IfacViolations));
    }

    [Fact]
    public async Task SerialAndUdpCarryAuthenticatedMaximumStandardPackets()
    {
        var (a, b) = CreateDuplexPair();
        await using var first = new SerialPacketInterface(a);
        await using var second = new SerialPacketInterface(b);
        await first.StartAsync(default);
        await second.StartAsync(default);
        using var code = InterfaceAccessCode.Derive("parity4", "synthetic");
        var packet = Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[481]);
        var frame = code.Protect(packet.ToArray());
        await first.SendAsync(frame, default);
        await using var incoming = second.ReceiveAsync(default).GetAsyncEnumerator();
        Assert.Equal(frame, (await Next(incoming)).ToArray());

        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        while (portA == portB) portB = FreeUdpPort();
        await using var udpA = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, portA), new IPEndPoint(IPAddress.Loopback, portB));
        await using var udpB = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, portB), new IPEndPoint(IPAddress.Loopback, portA));
        await udpA.StartAsync(default);
        await udpB.StartAsync(default);
        await udpA.SendAsync(frame, default);
        await using var udpIn = udpB.ReceiveAsync(default).GetAsyncEnumerator();
        Assert.Equal(frame, (await Next(udpIn)).ToArray());
    }

    [Fact]
    public async Task OversizedUdpDatagramDoesNotFaultReceiverOrPreventNextValidPacket()
    {
        var bearer = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, 0), new IPEndPoint(IPAddress.Loopback, 1));
        await using var manager = new PacketInterfaceManager([bearer]);
        await manager.StartAsync();
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await sender.SendToAsync(new byte[4096], SocketFlags.None, bearer.LocalEndpoint!);
        await sender.SendToAsync(MakePacket(33), SocketFlags.None, bearer.LocalEndpoint!);
        await using var incoming = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();
        Assert.True(await incoming.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Equal(33, TagOf(incoming.Current.Raw));
    }

    [Fact]
    public async Task UdpRawIfacFrameBoundIncludesFullTagWithoutUpgradingDecodedPacketMtu()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        while (portA == portB) portB = FreeUdpPort();
        await using var sender = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, portA), new IPEndPoint(IPAddress.Loopback, portB));
        await using var receiver = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, portB), new IPEndPoint(IPAddress.Loopback, portA));
        await sender.StartAsync(default);
        await receiver.StartAsync(default);
        using var code = InterfaceAccessCode.Derive("parity4", "synthetic", size: 64);
        var upgraded = Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16],
            new byte[sender.HardwareMtu - Packet.Header1Size], mtu: sender.HardwareMtu);
        var frame = code.Protect(upgraded.ToArray());
        Assert.Equal(sender.HardwareMtu + 64, frame.Length);
        await sender.SendAsync(frame, default);
        await using var rawIncoming = receiver.ReceiveAsync(default).GetAsyncEnumerator();
        Assert.Equal(frame, (await Next(rawIncoming)).ToArray());
        Assert.True(code.TryUnprotect(frame, out var decoded));
        Assert.False(Packet.TryParse(decoded!, out _));

        var managedBearer = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, 0), new IPEndPoint(IPAddress.Loopback, 1));
        await using var manager = new PacketInterfaceManager([managedBearer]);
        manager.SetInterfaceAccessCode(managedBearer, InterfaceAccessCode.Derive("parity4", "synthetic", size: 64));
        await manager.StartAsync();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await socket.SendToAsync(frame, SocketFlags.None, managedBearer.LocalEndpoint!);
        var standard = Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[481]);
        var standardFrame = code.Protect(standard.ToArray());
        Assert.Equal(Packet.Mtu + 64, standardFrame.Length);
        await socket.SendToAsync(standardFrame, SocketFlags.None, managedBearer.LocalEndpoint!);
        await using var incoming = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();
        Assert.True(await incoming.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Equal(standard.ToArray(), incoming.Current.Raw.ToArray());
        Assert.Equal(1, manager.GetStatistics().Single().MalformedPackets);
        await Assert.ThrowsAsync<ArgumentException>(async () => await manager.SendToAsync(upgraded, managedBearer));
    }
}
