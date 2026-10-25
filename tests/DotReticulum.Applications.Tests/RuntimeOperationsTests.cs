using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using TransportRouter = DotReticulum.Transport.TransportRouter;
using TransportRouterOptions = DotReticulum.Transport.TransportRouterOptions;

namespace DotReticulum.Applications.Tests;

public sealed class RuntimeOperationsTests
{
    private sealed class MemoryInterface : IPacketInterface, ISpawningPacketInterface
    {
        private readonly Channel<ReadOnlyMemory<byte>> _received = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public List<byte[]> SentPackets { get; } = [];
        public bool SendsThroughChildren => false;
        public event Action<IPacketInterface>? ChildConnected;
        public void AddChild(IPacketInterface child) => ChildConnected?.Invoke(child);
        public void Inject(ReadOnlyMemory<byte> packet) => _received.Writer.TryWrite(packet);
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public void Fail() => _received.Writer.TryComplete(new IOException("owned root failed"));
        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Started = true;
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var packet in _received.Reader.ReadAllAsync(cancellationToken)) yield return packet;
        }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentPackets.Add(packet.ToArray());
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _received.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StatisticsClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }

    [Fact]
    public async Task StaticallyRegisteredExtensionValidatesConfigurationAndParticipatesInOwnedReload()
    {
        var registry = new InterfaceFactoryRegistry();
        var created = new List<MemoryInterface>();
        registry.Register("TestMemory", ["channel"], settings =>
        {
            Assert.Equal("mesh", settings.Get("channel"));
            var iface = new MemoryInterface();
            created.Add(iface);
            return iface;
        });
        Assert.Throws<ArgumentException>(() => registry.Register("TestMemory", [], _ => new MemoryInterface()));
        const string text = """
            [reticulum]
            share_instance = no
            [interfaces]
            [[Test extension]]
            type = TestMemory
            enabled = yes
            channel = mesh
            """;
        var configuration = RnsdConfigParser.Parse(text, registry);
        var root = Path.Combine(Path.GetTempPath(), "dotret-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var host = RnsdHost.Create(configuration, Path.Combine(root, "transport_identity"), (_, _) => { }, registry))
            {
                await host.StartAsync(CancellationToken.None);
                Assert.True(created[0].Started);
                Assert.False(await host.AttachInterfaceAsync("Test extension"));
                Assert.True(await host.ReloadInterfaceAsync("Test extension"));
                Assert.True(created[0].Disposed);
                Assert.True(created[1].Started);
                Assert.Single(host.Interfaces);
                Assert.Null(await host.DetachInterfaceAsync("absent"));
                Assert.True(await host.DetachInterfaceAsync("Test extension"));
                Assert.True(created[1].Disposed);
                Assert.True(await host.AttachInterfaceAsync("Test extension"));
                Assert.True(created[2].Started);
            }
            Assert.All(created, iface => Assert.True(iface.Disposed));
            Assert.Throws<FormatException>(() => RnsdConfigParser.Parse(text));
            Assert.Throws<FormatException>(() => RnsdConfigParser.Parse(text.Replace("channel = mesh", "unsupported = yes"), registry));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StatisticsExposeMeasuredBoundariesAndLeaveUnknownDeviceFieldsAbsent()
    {
        var iface = new MemoryInterface();
        await using var manager = new PacketInterfaceManager([iface], channelCapacity: 3);
        await manager.StartAsync();
        await manager.SendAsync(Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[] { 4 }));
        var stats = RuntimeStatistics.Collect(manager, [("Memory", iface)]);
        var entries = Assert.IsType<object?[]>(stats["interfaces"]);
        var entry = Assert.IsType<Dictionary<object, object?>>(Assert.Single(entries));
        Assert.Null(entry["txb"]);
        Assert.Null(stats["txb"]);
        Assert.Equal(20L, entry["dotreticulum_decoded_txb"]);
        Assert.False(entry.ContainsKey("rssi"));
        Assert.False(entry.ContainsKey("snr"));
        Assert.False(entry.ContainsKey("txbuffered"));
        Assert.False(entry.ContainsKey("txdrp"));
        Assert.Equal(0L, entry["ifac_violations"]);
        Assert.True(Assert.IsType<long>(stats["rss"]) > 0);
        Assert.Equal(0.0, stats["tqpressure"]);
        var profiled = RuntimeStatistics.Collect(manager, [("Memory", iface)], profiling: true);
        Assert.True(Assert.IsType<long>(profiled["rss"]) > 0);
        Assert.IsType<Dictionary<object, object?>>(profiled["dotreticulum_profile"]);
        Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(MsgPack.Pack(profiled)));
    }

    [Fact]
    public async Task StatisticsSamplePacketRatesAndTransportPacketFilterHitsWithoutInventingExtensionBitRates()
    {
        var clock = new StatisticsClock();
        var iface = new MemoryInterface();
        await using var manager = new PacketInterfaceManager([iface]);
        await manager.StartAsync();
        await using var router = new TransportRouter(manager, new TransportRouterOptions { TimeProvider = clock });
        _ = RuntimeStatistics.Collect(manager, [("Memory", iface)], router: router, timeProvider: clock);

        manager.RecordAcceptedPacket(iface);
        manager.RecordAcceptedPacket(iface);
        manager.RecordPacketFilterHit(iface);
        manager.RecordProtocolViolation(iface);
        await manager.SendAsync(Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[] { 1 }));
        await manager.SendAsync(Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[] { 2 }));
        clock.Advance(TimeSpan.FromSeconds(2));

        var stats = RuntimeStatistics.Collect(manager, [("Memory", iface)], router: router, timeProvider: clock);
        var entry = Assert.IsType<Dictionary<object, object?>>(Assert.Single(Assert.IsType<object?[]>(stats["interfaces"])));

        Assert.Null(entry["txs"]);
        Assert.Equal(1L, entry["packet_filter_hits"]);
        Assert.Equal(1L, entry["protocol_violations"]);
        Assert.Null(stats["txs"]);
        Assert.Equal(1d, stats["rxpps"]);
        Assert.Equal(1d, stats["txpps"]);
        Assert.Equal(2L, stats["rx_packets"]);
        Assert.Equal(2L, stats["tx_packets"]);
        Assert.False(entry.ContainsKey("txbuffered"));
    }

    [Fact]
    public async Task StatisticsPreserveActualFramedTcpBytesAndSampleBearerRatherThanDecodedBitRates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var clock = new StatisticsClock();
            var iface = new TcpClientPacketInterface("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
            await using var manager = new PacketInterfaceManager([iface]);
            await manager.StartAsync(timeout.Token);
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            _ = RuntimeStatistics.Collect(manager, [("TCP", iface)], timeProvider: clock);
            var packet = Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[] { 0x7e, 0x7d });
            await manager.SendAsync(packet, cancellationToken: timeout.Token);
            var frame = new List<byte>();
            var octet = new byte[1];
            do
            {
                await peer.GetStream().ReadExactlyAsync(octet, timeout.Token);
                frame.Add(octet[0]);
                Assert.True(frame.Count <= Packet.Mtu * 2 + 2);
            } while (frame.Count == 1 || octet[0] != 0x7e);
            Assert.Equal(0x7e, frame[0]);
            Assert.True(frame.Count > packet.ToArray().Length);
            clock.Advance(TimeSpan.FromSeconds(2));

            var stats = RuntimeStatistics.Collect(manager, [("TCP", iface)], timeProvider: clock);
            var row = Assert.IsType<Dictionary<object, object?>>(Assert.Single(Assert.IsType<object?[]>(stats["interfaces"])));
            Assert.Equal((long)frame.Count, row["txb"]);
            Assert.Equal((long)frame.Count, stats["txb"]);
            Assert.Equal((long)packet.ToArray().Length, stats["dotreticulum_decoded_txb"]);
            Assert.Equal(frame.Count * 4d, row["txs"]);
            Assert.Equal(frame.Count * 4d, stats["txs"]);
            Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(MsgPack.Pack(stats)));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task StatisticsReportObservedPathRequestsAndActualInboundQueueDepthAndPressure()
    {
        var iface = new MemoryInterface();
        await using var manager = new PacketInterfaceManager([iface]);
        await manager.StartAsync();
        await using var router = new TransportRouter(manager, new TransportRouterOptions
        {
            InboundDataCapacity = 1,
            InboundAnnounceCapacity = 1,
            InboundPathRequestCapacity = 1,
            InboundIngressLimitedCapacity = 1,
        });

        Assert.True(router.TryQueueInbound(PathRequest(iface, 1)));
        var sentDestination = Enumerable.Repeat((byte)2, 16).ToArray();
        await router.RequestPathAsync(sentDestination, iface);

        var stats = RuntimeStatistics.Collect(manager, [("Memory", iface)], router: router);
        var interfaces = Assert.IsType<object?[]>(stats["interfaces"]);
        var entry = Assert.IsType<Dictionary<object, object?>>(Assert.Single(interfaces));
        Assert.Equal(1L, entry["prxc"]);
        Assert.True(Assert.IsType<long>(entry["prxb"]) > 0);
        Assert.Equal(1L, entry["ptxc"]);
        Assert.True(Assert.IsType<long>(entry["ptxb"]) > 0);
        Assert.Single(iface.SentPackets);
        Assert.Equal(1L, stats["prxc"]);
        Assert.True(Assert.IsType<long>(stats["prxb"]) > 0);
        Assert.Equal(1L, stats["ptxc"]);
        Assert.True(Assert.IsType<long>(stats["ptxb"]) > 0);
        Assert.Equal(1, Assert.IsType<int>(stats["rxqp"]));
        Assert.Equal(1, Assert.IsType<int>(stats["rxqt"]));
        Assert.Equal(1.0, stats["pqpressure"]);
        Assert.Equal(0L, stats["rxqtd"]);
    }

    [Fact]
    public async Task StatisticsAggregateChildPathRequestTrafficWithoutRepeatingParentByteTotals()
    {
        var root = new MemoryInterface();
        var child = new MemoryInterface();
        await using var manager = new PacketInterfaceManager([root]);
        await manager.StartAsync();
        root.AddChild(child);
        await using var router = new TransportRouter(manager);

        var incomingPacket = PathRequest(child, 3);
        child.Inject(incomingPacket.Raw);
        await using var received = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();
        Assert.True(await received.MoveNextAsync());
        Assert.True(router.TryQueueInbound(received.Current));
        await router.RequestPathAsync(Enumerable.Repeat((byte)4, Destination.HashLength).ToArray(), child);

        var stats = RuntimeStatistics.Collect(manager, [("Root", root), ("Child", child)], router: router);
        var rows = Assert.IsType<object?[]>(stats["interfaces"]).OfType<Dictionary<object, object?>>().ToArray();
        var rootRow = Assert.Single(rows, row => Equals(row["name"], "Root"));
        var childRow = Assert.Single(rows, row => Equals(row["name"], "Child"));

        Assert.Equal(1L, childRow["prxc"]);
        Assert.Equal(1L, childRow["ptxc"]);
        Assert.Equal(1L, stats["prxc"]);
        Assert.Equal(1L, stats["ptxc"]);
        Assert.Equal(rootRow["rxb"], stats["rxb"]);
        Assert.Equal(rootRow["txb"], stats["txb"]);
        Assert.Equal(rootRow["rxb"], childRow["rxb"]);
        Assert.Null(stats["rxb"]);
        Assert.Null(stats["txb"]);
        Assert.Equal(rootRow["dotreticulum_decoded_rxb"], childRow["dotreticulum_decoded_rxb"]);
        Assert.Equal(rootRow["dotreticulum_decoded_rxb"], stats["dotreticulum_decoded_rxb"]);
        Assert.True(Assert.IsType<long>(stats["dotreticulum_decoded_rxb"]) > 0);
    }

    private static ReceivedPacket PathRequest(IPacketInterface source, byte marker)
    {
        var destination = Enumerable.Repeat(marker, 16).ToArray();
        var tag = Enumerable.Repeat((byte)(marker + 1), 16).ToArray();
        var payload = new byte[Destination.HashLength * 2];
        destination.CopyTo(payload, 0);
        tag.CopyTo(payload, Destination.HashLength);
        var packet = Packet.Create(PacketType.Data, DestinationType.Plain, TransportRouter.PathRequestHash, payload);
        return new ReceivedPacket(packet.ToArray(), packet, source);
    }

    [Fact]
    public void HousekeepingExpiresOnlyOwnedHashNamedFilesWithSeparateCacheLifetimes()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotret-housekeeping-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        try
        {
            string Make(string directory, string name, TimeSpan age)
            {
                var path = Path.Combine(root, directory, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "owned fixture");
                File.SetLastWriteTimeUtc(path, (now - age).UtcDateTime);
                return path;
            }
            var oldResource = Make("resources", new string('a', 64), TimeSpan.FromDays(2));
            var newResource = Make("resources", new string('b', 64), TimeSpan.FromHours(12));
            var oldPacket = Make("cache", new string('c', 64), TimeSpan.FromDays(8));
            var newPacket = Make("cache", new string('d', 64), TimeSpan.FromDays(6));
            var unrelated = Make("resources", "user-file.txt", TimeSpan.FromDays(9));
            var unknown = Make("cache", new string('z', 64), TimeSpan.FromDays(9));
            RuntimeHousekeeping.CleanCaches(root, now, message => throw new Xunit.Sdk.XunitException(message));
            Assert.False(File.Exists(oldResource));
            Assert.False(File.Exists(oldPacket));
            Assert.All(new[] { newResource, newPacket, unrelated, unknown }, path => Assert.True(File.Exists(path)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FaultedNodeAutomaticallyReleasesOwnedBearerAndRemainsTerminal()
    {
        var root = ReticulumNodeTests.TempDir();
        var iface = new MemoryInterface();
        var registry = new InterfaceFactoryRegistry();
        registry.Register("Faultable", [], _ => iface);
        var configuration = RnsdConfigParser.Parse("""
            [reticulum]
            share_instance=no
            [interfaces]
            [[Owned root]]
            type=Faultable
            enabled=yes
            """, registry);
        var node = ReticulumNode.Create(configuration, root, extensions: registry);
        try
        {
            await node.StartAsync();
            iface.Fail();
            var error = await Assert.ThrowsAnyAsync<Exception>(() => node.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(error is IOException or AggregateException, error.ToString());
            Assert.Contains("owned root failed", error.ToString());
            Assert.True(iface.Disposed);
            Assert.True(node.Completion.IsFaulted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => node.StartAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => node.PersistAsync());
        }
        finally
        {
            var error = await Record.ExceptionAsync(async () => await node.DisposeAsync());
            Assert.NotNull(error);
            Assert.True(error is IOException or AggregateException, error.ToString());
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoveryLeaseFailureDoesNotTerminateManagerAndOwnerStillControlsDetach()
    {
        var iface = new MemoryInterface();
        await using var manager = new PacketInterfaceManager([], keepAlive: true);
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Diagnostic += (_, error) => fault.TrySetResult(error);
        await manager.StartAsync();
        await manager.AttachAsync(iface, isolateFailure: true);
        iface.Fail();
        Assert.IsType<IOException>(await fault.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(manager.Contains(iface));
        Assert.False(iface.Disposed);
        Assert.True(await manager.DetachAsync(iface));
        Assert.True(iface.Disposed);
        var replacement = new MemoryInterface();
        await manager.AttachAsync(replacement, isolateFailure: true);
        Assert.True(replacement.Started);
        Assert.True(manager.Contains(replacement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupFailureReleasesPhysicalAndSharedListenersWithoutReplacingCorruptState(bool corruptState)
    {
        var root = ReticulumNodeTests.TempDir();
        var sharedPort = ReticulumNodeTests.FreePort();
        var physicalPort = ReticulumNodeTests.FreePort();
        using var control = new TcpListener(IPAddress.Loopback, 0);
        control.Start();
        var controlPort = ((IPEndPoint)control.LocalEndpoint).Port;
        if (corruptState) control.Stop();
        var configuration = RnsdConfigParser.Parse($"""
            [reticulum]
            share_instance=yes
            shared_instance_type=tcp
            shared_instance_port={sharedPort}
            instance_control_port={controlPort}
            [interfaces]
            [[Physical]]
            type=TCPServerInterface
            enabled=yes
            listen_ip=127.0.0.1
            listen_port={physicalPort}
            """);
        var host = RnsdHost.Create(configuration, Path.Combine(root, "transport_identity"), (_, _) => { });
        var statePath = Path.Combine(root, "runtime-state-v1.msgpack");
        try
        {
            if (corruptState) File.WriteAllBytes(statePath, [0xc1]);
            var error = await Record.ExceptionAsync(() => host.StartAsync(CancellationToken.None).AsTask());
            Assert.NotNull(error);
            Assert.True(corruptState ? error is InvalidDataException : error is SocketException, error.ToString());
            foreach (var port in new[] { sharedPort, physicalPort })
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
            if (corruptState) Assert.Equal(new byte[] { 0xc1 }, File.ReadAllBytes(statePath));
        }
        finally
        {
            var shutdown = await Record.ExceptionAsync(async () => await host.DisposeAsync());
            Assert.IsType<AggregateException>(shutdown);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
