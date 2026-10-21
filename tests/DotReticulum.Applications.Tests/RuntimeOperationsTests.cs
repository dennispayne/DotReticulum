using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;

namespace DotReticulum.Applications.Tests;

public sealed class RuntimeOperationsTests
{
    private sealed class MemoryInterface : IPacketInterface
    {
        private readonly Channel<ReadOnlyMemory<byte>> _received = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
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
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _received.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
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
        Assert.Equal(20L, entry["dotreticulum_decoded_txb"]);
        Assert.Null(entry["txb"]);
        Assert.False(entry.ContainsKey("rssi"));
        Assert.False(entry.ContainsKey("snr"));
        Assert.Null(entry["ifac_violations"]);
        Assert.Null(stats["rss"]);
        Assert.Equal(0.0, stats["tqpressure"]);
        var profiled = RuntimeStatistics.Collect(manager, [("Memory", iface)], profiling: true);
        Assert.True(Assert.IsType<long>(profiled["rss"]) > 0);
        Assert.IsType<Dictionary<object, object?>>(profiled["dotreticulum_profile"]);
        Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(MsgPack.Pack(profiled)));
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
