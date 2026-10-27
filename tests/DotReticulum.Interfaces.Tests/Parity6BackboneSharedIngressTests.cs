using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces.Tests;

// Pinned source's actual periodic/immediate control methods execute in GenerateParity6BackbonePipeVectors.py.
[Trait("Category", "Integration")]
public sealed class Parity6BackboneSharedIngressTests
{
    [Fact]
    public void GlobalProducerSelectionThresholdsSamplingAndHoldMatchReference()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-backbone-pipe.json")));
        foreach (var item in vectors.RootElement.GetProperty("ingress").EnumerateArray())
        {
            var state = new BackboneSharedIngressState(new(), 0);
            var producers = item.GetProperty("producers").EnumerateArray().Select(producer => new Producer
            {
                Frames = producer.GetProperty("packets").GetInt64(),
                Bytes = producer.GetProperty("count").GetInt64(),
                IngressGated = producer.TryGetProperty("gated", out var gated) && gated.GetBoolean(),
                IngressHoldUntil = producer.TryGetProperty("hold", out var hold) ? hold.GetDouble() : 0,
            }).ToArray();
            foreach (var producer in producers) state.Register(producer);
            state.Evaluate(item.GetProperty("depth").GetInt32(), item.GetProperty("capacity").GetInt32(),
                item.GetProperty("now").GetDouble(), item.GetProperty("immediate").GetBoolean());
            var expected = item.GetProperty("states").EnumerateArray().ToArray();
            for (var index = 0; index < producers.Length; index++)
            {
                Assert.Equal(expected[index].GetProperty("gated").GetBoolean(), producers[index].IngressGated);
                Assert.Equal(expected[index].GetProperty("hold").GetDouble(), producers[index].IngressHoldUntil, 8);
                Assert.Equal(expected[index].GetProperty("throttles").GetInt32(), producers[index].Throttles);
                Assert.Equal(expected[index].GetProperty("packets").GetInt64(), producers[index].Frames);
                Assert.Equal(expected[index].GetProperty("bytes").GetInt64(), producers[index].Bytes);
            }
        }
    }

    [Fact]
    public void GlobalReleaseHonorsHoldAndReleasesOnlyOnePeerPerTick()
    {
        var state = new BackboneSharedIngressState(new(), 0);
        var firstListener = new Producer { Frames = 3, Bytes = 300 };
        var secondListener = new Producer { Frames = 9, Bytes = 900 };
        state.Register(firstListener);
        state.Register(secondListener);
        state.Evaluate(175, 256, 0.25);
        Assert.False(firstListener.IngressGated);
        Assert.True(secondListener.IngressGated);
        Assert.Equal(12.25, secondListener.IngressHoldUntil, 8);
        firstListener.SetIngressGate(true, 12.25);
        state.Evaluate(0, 256, 12.249);
        Assert.True(firstListener.IngressGated);
        Assert.True(secondListener.IngressGated);
        state.Evaluate(0, 256, 12.25);
        Assert.Equal(1, new[] { firstListener, secondListener }.Count(peer => peer.IngressGated));
        state.Evaluate(0, 256, 12.5);
        Assert.False(firstListener.IngressGated);
        Assert.False(secondListener.IngressGated);
        state.Unregister(firstListener);
        state.Unregister(secondListener);
        Assert.Equal(0, state.PeerCount);
    }

    [Fact]
    public void SharedFlapCountsDifferingListenerGraceAndExpiryMatchActualReferenceTeardown()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-backbone-pipe.json")));
        var options = new BackbonePacketInterfaceOptions { FastFlapGrace = 0 };
        var state = new BackboneSharedIngressState(options, 0);
        foreach (var step in vectors.RootElement.GetProperty("global_flaps").EnumerateArray())
        {
            var now = step.GetProperty("now").GetDouble();
            var duration = step.GetProperty("duration");
            if (duration.ValueKind != JsonValueKind.Null)
                state.Disconnected("127.0.0.1", duration.GetDouble(), now, options);
            Assert.Equal(step.GetProperty("grace_zero_count").GetInt32(), state.BlockedCount(now, options));
            Assert.Equal(step.GetProperty("grace_one_count").GetInt32(), state.BlockedCount(now, options with { FastFlapGrace = 1 }));
        }
    }

    [Fact]
    public async Task QueueIdentitySharesFlapsAcrossOwnersButNotManagersAndRetainsStateBetweenOwners()
    {
        var probe = new QueueProbe();
        var otherProbe = new QueueProbe();
        await using var manager = new PacketInterfaceManager([probe]);
        await using var otherManager = new PacketInterfaceManager([otherProbe]);
        var options = new BackbonePacketInterfaceOptions { FastFlapGrace = 0 };
        var first = BackboneSharedIngressState.Acquire(probe.Queue, options);
        var second = BackboneSharedIngressState.Acquire(probe.Queue, options);
        var separate = BackboneSharedIngressState.Acquire(otherProbe.Queue, options);
        try
        {
            Assert.Same(first, second);
            Assert.NotSame(first, separate);
            first.Disconnected("127.0.0.1", 1, BackbonePeerEndpoint.Now, options);
            Assert.True(second.IsBlocked("127.0.0.1", BackbonePeerEndpoint.Now, options));
            Assert.False(separate.IsBlocked("127.0.0.1", BackbonePeerEndpoint.Now, options));
            Assert.False(second.IsBlocked("127.0.0.1", BackbonePeerEndpoint.Now, options with { FastFlapGrace = 1 }));
        }
        finally
        {
            await first.ReleaseAsync();
            await second.ReleaseAsync();
            await separate.ReleaseAsync();
        }
        var resumed = BackboneSharedIngressState.Acquire(probe.Queue, options);
        try
        {
            Assert.Same(first, resumed);
            Assert.True(resumed.IsBlocked("127.0.0.1", BackbonePeerEndpoint.Now, options));
        }
        finally { await resumed.ReleaseAsync(); }
    }

    [Fact]
    public async Task ManagerHighWatermarkNotificationReachesSharedControllerWithoutPeriodicSampling()
    {
        var probe = new QueueProbe();
        await using var manager = new PacketInterfaceManager([probe], channelCapacity: 256);
        var depth = 0;
        manager.SetIngressQueueSource(() => Volatile.Read(ref depth), 256);
        probe.Queue.HighWatermarkReached += (_, _) => Volatile.Write(ref depth, 0);
        var state = BackboneSharedIngressState.Acquire(probe.Queue, new());
        try
        {
            Volatile.Write(ref depth, 229);
            manager.NotifyIngressQueuePressure();
            Assert.Equal(0, state.ImmediateEvaluations);
            Volatile.Write(ref depth, 230);
            manager.NotifyIngressQueuePressure();
            Assert.Equal(0, probe.Queue.Depth); // An earlier observer simulated a concurrent DATA-queue drain.
            Assert.Equal(1, state.ImmediateEvaluations); // Uses captured depth, including exact floor(.9*256).
            manager.SetIngressQueueSource(() => 58, 64);
            manager.NotifyIngressQueuePressure();
            Assert.Equal(1, state.ImmediateEvaluations); // Source's minimum immediate high watermark is 128.
        }
        finally { await state.ReleaseAsync(); }
    }

    [Fact]
    public async Task ProviderRebindingPreservesIdentityAndSamplesActualQueueInsteadOfMergeProxy()
    {
        var probe = new QueueProbe();
        await using var manager = new PacketInterfaceManager([probe], channelCapacity: 256);
        var queue = probe.Queue;
        var state = BackboneSharedIngressState.Acquire(queue, new());
        var producer = new Producer();
        state.Register(producer);
        try
        {
            manager.SetIngressQueueSource(() => 22, 32);
            Assert.Same(queue, probe.Queue);
            Assert.Equal(0, manager.QueuedPackets);
            Assert.Equal(22, queue.Depth);
            Assert.Equal(32, queue.Capacity);
            // Keep this producer active across polling windows until its actual DATA pressure gates it.
            await TestSupport.WaitFor(() =>
            {
                if (producer.IngressGated) return true;
                Interlocked.Add(ref producer.Frames, 1);
                Interlocked.Add(ref producer.Bytes, 100);
                return false;
            });
        }
        finally
        {
            state.Unregister(producer);
            await state.ReleaseAsync();
        }
    }

    [Fact]
    public async Task StandaloneStatesRemainPrivateWithoutManagerFeedback()
    {
        var options = new BackbonePacketInterfaceOptions { FastFlapGrace = 0 };
        var one = BackboneSharedIngressState.Acquire(null, options);
        var two = BackboneSharedIngressState.Acquire(null, options);
        try
        {
            Assert.NotSame(one, two);
            one.Disconnected("127.0.0.1", 1, 1, options);
            Assert.False(two.IsBlocked("127.0.0.1", 1, options));
        }
        finally { await one.ReleaseAsync(); await two.ReleaseAsync(); }
    }

    [LinuxBackboneFact]
    public async Task ListenersInOneManagerShareFlapBlocksButIndependentManagerStillAccepts()
    {
        var options = new BackbonePacketInterfaceOptions { FastFlapGrace = 0 };
        var first = new BackbonePacketInterface(new(IPAddress.Loopback, 0), options);
        var second = new BackbonePacketInterface(new(IPAddress.Loopback, 0), options);
        var independent = new BackbonePacketInterface(new(IPAddress.Loopback, 0), options);
        var firstChildren = Channel.CreateBounded<IPacketInterface>(2);
        var independentChildren = Channel.CreateBounded<IPacketInterface>(2);
        first.ChildConnected += child => firstChildren.Writer.TryWrite(child);
        independent.ChildConnected += child => independentChildren.Writer.TryWrite(child);
        await using var manager = new PacketInterfaceManager([first, second]);
        await using var otherManager = new PacketInterfaceManager([independent]);
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await manager.StartAsync(timeout.Token);
        await otherManager.StartAsync(timeout.Token);
        using (var remote = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await remote.ConnectAsync(first.LocalEndpoint!, timeout.Token);
            _ = await firstChildren.Reader.ReadAsync(timeout.Token);
        }
        await TestSupport.WaitFor(() => (int)second.GetDiagnostics()["blocked_ip_count"]! == 1);
        using var rejected = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await rejected.ConnectAsync(second.LocalEndpoint!, timeout.Token);
        await TestSupport.WaitFor(() => (long)second.GetDiagnostics()["rejected_clients"]! == 1);
        Assert.Equal(0, second.ClientCount);
        using var accepted = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await accepted.ConnectAsync(independent.LocalEndpoint!, timeout.Token);
        _ = await independentChildren.Reader.ReadAsync(timeout.Token);
        Assert.Equal(1, independent.ClientCount);
        Assert.Equal(0, independent.GetDiagnostics()["blocked_ip_count"]);
    }

    [LinuxBackboneFact]
    public async Task SharedSamplerUsesActualManagerQueueAndDisposalStopsGatedChildren()
    {
        var options = new BackbonePacketInterfaceOptions { HardwareMtu = 500 };
        var first = new BackbonePacketInterface(new(IPAddress.Loopback, 0), options);
        var second = new BackbonePacketInterface(new(IPAddress.Loopback, 0), options);
        var children = Channel.CreateBounded<IPacketInterface>(2);
        first.ChildConnected += child => children.Writer.TryWrite(child);
        second.ChildConnected += child => children.Writer.TryWrite(child);
        await using var manager = new PacketInterfaceManager([first, second], channelCapacity: 32);
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await manager.StartAsync(timeout.Token);
        using var one = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var two = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await one.ConnectAsync(first.LocalEndpoint!, timeout.Token);
        await two.ConnectAsync(second.LocalEndpoint!, timeout.Token);
        var a = await children.Reader.ReadAsync(timeout.Token);
        var b = await children.Reader.ReadAsync(timeout.Token);
        var wire = HdlcPacketFraming.Encode(TestSupport.MakePacket(1));
        await SendAllAsync(one, Enumerable.Repeat(wire, 10).SelectMany(frame => frame).ToArray(), timeout.Token);
        await SendAllAsync(two, Enumerable.Repeat(wire, 20).SelectMany(frame => frame).ToArray(), timeout.Token);
        await TestSupport.WaitFor(() => manager.QueuedPackets == 30);
        Assert.Equal(2, first.GetDiagnostics()["shared_ingress_peers"]);
        Assert.Equal(2, second.GetDiagnostics()["shared_ingress_peers"]);
        var diagnosticsA = (IPacketInterfaceDiagnostics)a;
        var diagnosticsB = (IPacketInterfaceDiagnostics)b;
        await TestSupport.WaitFor(() => (bool)diagnosticsA.GetDiagnostics()["ingress_throttled"]! ||
            (bool)diagnosticsB.GetDiagnostics()["ingress_throttled"]!);
        await manager.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
        Assert.Equal(0, first.GetDiagnostics()["shared_ingress_peers"]);
        Assert.Equal(0, second.GetDiagnostics()["shared_ingress_peers"]);
    }

    private static async Task SendAllAsync(Socket socket, byte[] bytes, CancellationToken token)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var written = await socket.SendAsync(bytes.AsMemory(offset), SocketFlags.None, token);
            if (written == 0) throw new IOException("Owned test socket made no send progress.");
            offset += written;
        }
    }

    private sealed class Producer : IBackboneIngressPeer
    {
        public long Bytes;
        public long Frames;
        public int Throttles;
        private volatile bool _gated;
        public bool IngressGated { get => _gated; set => _gated = value; }
        public double IngressHoldUntil { get; set; }
        public (long Bytes, long Frames) PeekIngressSample() => (Interlocked.Read(ref Bytes), Interlocked.Read(ref Frames));
        public (long Bytes, long Frames) TakeIngressSample() => (Interlocked.Exchange(ref Bytes, 0), Interlocked.Exchange(ref Frames, 0));
        public void SetIngressGate(bool gated, double holdUntil = 0)
        {
            if (gated && !IngressGated) Interlocked.Increment(ref Throttles);
            IngressHoldUntil = holdUntil;
            IngressGated = gated;
        }
    }

    private sealed class QueueProbe : IPacketInterface, IPacketInterfaceIngressFeedback
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateBounded<ReadOnlyMemory<byte>>(256);
        public PacketInterfaceIngressQueue Queue { get; private set; } = null!;
        public void SetIngressQueue(PacketInterfaceIngressQueue queue) => Queue = queue;
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) =>
            _incoming.Reader.ReadAllAsync(cancellationToken);
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask InjectAsync(byte[] packet) => _incoming.Writer.WriteAsync(packet);
        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
