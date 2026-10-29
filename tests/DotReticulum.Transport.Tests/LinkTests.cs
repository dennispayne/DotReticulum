using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// In-process link tests: routers connected by in-memory interfaces (direct A-B, and A-T-B through a transport node). Wire formats are checked
// against upstream vectors in LinkVectorTests; live interop is in LiveLinkTests.
[Trait("Category", "Unit")]
public class LinkTests
{
    internal sealed class Loopback : IPacketInterface
    {
        private readonly Channel<byte[]> _inbox = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        public Loopback? Peer { get; set; }
        public Task? SendBlock { get; set; }
        public TaskCompletionSource? SendStarted { get; set; }
        public Exception? SendFailure { get; set; }
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var p in _inbox.Reader.ReadAllAsync(cancellationToken)) yield return p;
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            SendStarted?.TrySetResult();
            if (SendBlock is { } blocked) await blocked.WaitAsync(cancellationToken);
            if (SendFailure is { } failure) throw failure;
            Peer?._inbox.Writer.TryWrite(packet.ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class Node : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task _run = Task.CompletedTask;
        public PacketInterfaceManager Manager { get; }
        public TransportRouter Router { get; }

        public Node(IEnumerable<Loopback> interfaces, bool transport = false, TimeProvider? timeProvider = null)
        {
            Manager = new PacketInterfaceManager(interfaces);
            Router = new TransportRouter(Manager, new TransportRouterOptions { TransportEnabled = transport, PathRequestGrace = TimeSpan.Zero, TimeProvider = timeProvider });
        }

        public async Task StartAsync()
        {
            await Manager.StartAsync();
            _run = Router.RunAsync(_cts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _run; } catch (OperationCanceledException) { }
            await Manager.DisposeAsync();
        }
    }

    internal static (Loopback, Loopback) Pair()
    {
        var a = new Loopback();
        var b = new Loopback { Peer = a };
        a.Peer = b;
        return (a, b);
    }

    internal static async Task<T> Within<T>(Task<T> task, int seconds = 10)
    {
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.Same(task, done);
        return await task;
    }

    internal static async Task Until(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(10);
        }
    }

    private static LocalDestination Serve(Node node, out Identity identity)
    {
        identity = Identity.Generate();
        var local = new LocalDestination(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["tests", "link"], identity)) { AcceptLinks = true };
        node.Router.RegisterLocalDestination(local);
        return local;
    }

    internal sealed record Topology(Node Initiator, Node Responder, LocalDestination Destination, Node[] Others) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Initiator.DisposeAsync();
            await Responder.DisposeAsync();
            foreach (var o in Others) await o.DisposeAsync();
        }
    }

    internal static async Task<Topology> BuildAsync(bool viaTransport, TimeProvider? timeProvider = null)
    {
        Node a, b;
        Node[] others = [];
        if (viaTransport)
        {
            var (a1, t1) = Pair();
            var (t2, b1) = Pair();
            a = new Node([a1], timeProvider: timeProvider);
            var t = new Node([t1, t2], transport: true, timeProvider: timeProvider);
            b = new Node([b1], timeProvider: timeProvider);
            others = [t];
        }
        else
        {
            var (a1, b1) = Pair();
            a = new Node([a1], timeProvider: timeProvider);
            b = new Node([b1], timeProvider: timeProvider);
        }

        await a.StartAsync();
        foreach (var node in others) await node.StartAsync();
        await b.StartAsync();
        var dest = Serve(b, out _);
        await b.Router.AnnounceAsync(dest);
        await Until(() => a.Router.HasPath(dest.Destination.Hash));
        return new Topology(a, b, dest, others);
    }

    public static IEnumerable<object[]> Topologies() => [[false], [true]];

    [Theory]
    [MemberData(nameof(Topologies))]
    public async Task Link_EstablishesAndExchangesDataBothWays(bool viaTransport)
    {
        await using var topo = await BuildAsync(viaTransport);
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responderGot = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initiatorGot = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        topo.Destination.LinkEstablished = l =>
        {
            l.PacketReceived = (data, _) => responderGot.TrySetResult(data);
            accepted.TrySetResult(l);
        };

        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => established.TrySetResult(l));
        link.PacketReceived = (data, _) => initiatorGot.TrySetResult(data);
        await Within(established.Task);
        var remote = await Within(accepted.Task);

        Assert.Equal(LinkStatus.Active, link.Status);
        Assert.Equal(LinkStatus.Active, remote.Status);
        Assert.NotNull(link.Rtt);
        Assert.Equal(Packet.Mtu, link.Mtu);
        Assert.Equal(431, link.Mdu);
        if (viaTransport) Assert.Equal(1, topo.Others[0].Router.LinkTableCount);

        var receipt = link.Send("ping"u8.ToArray());
        Assert.Equal("ping"u8.ToArray(), await Within(responderGot.Task));
        Assert.NotNull(receipt);
        remote.Send("pong"u8.ToArray());
        Assert.Equal("pong"u8.ToArray(), await Within(initiatorGot.Task));
    }

    [Theory]
    [MemberData(nameof(Topologies))]
    public async Task Link_DataPacketIsProvenAndReceiptDelivered(bool viaTransport)
    {
        await using var topo = await BuildAsync(viaTransport);
        topo.Destination.ProofStrategy = ProofStrategy.All;
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => established.TrySetResult(l));
        await Within(established.Task);

        var receipt = link.Send("prove me"u8.ToArray());
        Assert.NotNull(receipt);
        Assert.True(await Within(receipt.Completion));
        Assert.Equal(PacketReceiptStatus.Delivered, receipt.Status);
    }

    [Theory]
    [MemberData(nameof(Topologies))]
    public async Task Link_TeardownByEitherSide_ClosesBoth(bool initiatorCloses)
    {
        await using var topo = await BuildAsync(false);
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        topo.Destination.LinkEstablished = l => accepted.TrySetResult(l);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => established.TrySetResult(l));
        await Within(established.Task);
        var remote = await Within(accepted.Task);

        (initiatorCloses ? link : remote).Teardown();
        await Until(() => link.Status == LinkStatus.Closed && remote.Status == LinkStatus.Closed);
        Assert.Equal(initiatorCloses ? LinkTeardownReason.InitiatorClosed : LinkTeardownReason.DestinationClosed,
            initiatorCloses ? remote.TeardownReason : link.TeardownReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AsyncTeardownWaitsForBearerWriteBeforeOwnerCanDispose(bool initiatorCloses)
    {
        await using var topo = await BuildAsync(false);
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        topo.Destination.LinkEstablished = link => accepted.TrySetResult(link);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initiator = topo.Initiator.Router.Links.Connect(topo.Destination.Identity,
            topo.Destination.Destination.Hash, link => established.TrySetResult(link));
        await Within(established.Task);
        var responder = await Within(accepted.Task);
        var closing = initiatorCloses ? initiator : responder;
        var remote = initiatorCloses ? responder : initiator;
        var bearer = Assert.IsType<Loopback>(closing.AttachedInterface);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bearer.SendBlock = release.Task;
        bearer.SendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var stopped = closing.TeardownAsync();
            var repeated = closing.TeardownAsync();
            await bearer.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopped.IsCompleted);
            Assert.False(repeated.IsCompleted);
            Assert.Equal(LinkStatus.Active, remote.Status);
            release.TrySetResult();
            await Task.WhenAll(stopped, repeated).WaitAsync(TimeSpan.FromSeconds(5));
            await (initiatorCloses ? topo.Initiator : topo.Responder).DisposeAsync();
            await Until(() => remote.Status == LinkStatus.Closed);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncTeardownSettlesOnCancellationOrManagerShutdown(bool stopManager)
    {
        await using var topo = await BuildAsync(false);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity,
            topo.Destination.Destination.Hash, accepted => established.TrySetResult(accepted));
        await Within(established.Task);
        var bearer = Assert.IsType<Loopback>(link.AttachedInterface);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bearer.SendBlock = release.Task;
        bearer.SendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var stopped = link.TeardownAsync(cancellation.Token);
            await bearer.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (stopManager) await topo.Initiator.Router.Links.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            else await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => stopped.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task AsyncTeardownSurfacesBearerWriteFailure()
    {
        await using var topo = await BuildAsync(false);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity,
            topo.Destination.Destination.Hash, accepted => established.TrySetResult(accepted));
        await Within(established.Task);
        var bearer = Assert.IsType<Loopback>(link.AttachedInterface);
        bearer.SendFailure = new IOException("Owned close write failed.");
        var failure = await Assert.ThrowsAsync<IOException>(
            () => link.TeardownAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Owned close write failed.", failure.Message);
        Assert.Equal(LinkStatus.Closed, link.Status);
    }

    [Fact]
    public async Task Link_Identify_RevealsInitiatorToResponder()
    {
        await using var topo = await BuildAsync(false);
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identified = new TaskCompletionSource<Identity>(TaskCreationOptions.RunContinuationsAsynchronously);
        topo.Destination.LinkEstablished = l =>
        {
            l.RemoteIdentified = (_, id) => identified.TrySetResult(id);
            accepted.TrySetResult(l);
        };

        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => established.TrySetResult(l));
        await Within(established.Task);
        await Within(accepted.Task);

        var me = Identity.Generate();
        link.Identify(me);
        var seen = await Within(identified.Task);
        Assert.Equal(me.Hash, seen.Hash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Link_ResponderInitializesBeforeFirstDataEvenWhenObserversAreBusy(bool initializerThrows)
    {
        // RNS a29bfb5 Link.rtt_packet invokes the owner's setup callback before receiving more data.
        // Freeze protocol time: this tests observer ordering, not the RTT-derived receipt deadline.
        await using var topo = await BuildAsync(false, new Parity4LinkMtuTests.Clock());
        using var releaseObservers = new ManualResetEventSlim();
        var observersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identified = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        topo.Responder.Router.Log = errors.Enqueue;
        topo.Destination.ProofStrategy = ProofStrategy.All;
        topo.Destination.LinkEstablished = link =>
        {
            link.PacketReceived = (data, _) => received.TrySetResult(data);
            link.RemoteIdentified = (_, identity) => identified.TrySetResult(identity.Hash);
            if (initializerThrows) throw new InvalidOperationException("setup observer failed");
        };
        for (var index = 0; index < LinkManager.ObserverConcurrency; index++)
        {
            topo.Responder.Router.Links.Dispatch(() =>
            {
                if (Interlocked.Increment(ref started) == LinkManager.ObserverConcurrency)
                    observersStarted.TrySetResult();
                releaseObservers.Wait();
            });
        }
        using var sender = Identity.Generate();
        try
        {
            await observersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity,
                topo.Destination.Destination.Hash, value => established.TrySetResult(value));
            await Within(established.Task);
            link.Identify(sender);
            var receipt = link.Send("first packet"u8.ToArray());
            Assert.NotNull(receipt);
            Assert.True(await Within(receipt.Completion));
        }
        finally
        {
            releaseObservers.Set();
        }
        Assert.Equal("first packet"u8.ToArray(), await Within(received.Task));
        Assert.Equal(sender.Hash, await Within(identified.Task));
        Assert.Equal(initializerThrows, errors.Any(message => message.Contains("setup observer failed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Link_OrderedReceiverRunsWithoutObserversAndSurvivesItsOwnFailure()
    {
        await using var topo = await BuildAsync(false);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new List<byte>();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        topo.Responder.Router.Log = errors.Enqueue;
        topo.Destination.LinkEstablished = link => link.SetPacketReceiver((data, _) =>
        {
            values.Add(data[0]);
            if (data[0] == 0) throw new InvalidOperationException("receiver failure");
            if (data[0] == 31) delivered.TrySetResult();
        });
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = topo.Initiator.Router.Links.Connect(topo.Destination.Identity,
            topo.Destination.Destination.Hash, value => established.TrySetResult(value));
        await Within(established.Task);
        for (byte value = 0; value < 32; value++) Assert.True(sender.TrySend([value]));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0, 32).Select(value => (byte)value), values);
        Assert.Contains(errors, message => message.Contains("receiver failure", StringComparison.Ordinal));
        sender.Teardown();
        Assert.False(sender.TrySend([99]));
    }

    [Fact]
    public async Task Link_PacketReceiverLeaseProtectsConcurrentOwnersAndLaterRegistrations()
    {
        await using var topo = await BuildAsync(false);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = topo.Initiator.Router.Links.Connect(topo.Destination.Identity,
            topo.Destination.Destination.Hash, value => established.TrySetResult(value));
        await Within(established.Task);
        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try { return sender.RegisterPacketReceiver((_, _) => { }); }
            catch (InvalidOperationException) { return null; }
        })));
        var lease = Assert.Single(leases.OfType<IDisposable>());
        Assert.Throws<InvalidOperationException>(() => sender.SetPacketReceiver(null));
        lease.Dispose();
        using var replacement = sender.RegisterPacketReceiver((_, _) => { });
        lease.Dispose();
        Assert.Throws<InvalidOperationException>(() => sender.RegisterPacketReceiver((_, _) => { }));
        replacement.Dispose();
        sender.SetPacketReceiver((_, _) => { });
        Assert.Throws<InvalidOperationException>(() => sender.RegisterPacketReceiver((_, _) => { }));
        sender.SetPacketReceiver(null);
        sender.Teardown();
        Assert.Throws<InvalidOperationException>(() => sender.RegisterPacketReceiver((_, _) => { }));
    }

    [Fact]
    public async Task Link_ReceiptlessTrySendReportsBoundedQueueFailure()
    {
        await using var topo = await BuildAsync(false);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = topo.Initiator.Router.Links.Connect(topo.Destination.Identity,
            topo.Destination.Destination.Hash, value => established.TrySetResult(value));
        await Within(established.Task);
        var bearer = Assert.IsType<Loopback>(sender.AttachedInterface);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        topo.Initiator.Router.Log = errors.Enqueue;
        bearer.SendBlock = blocked.Task;
        try
        {
            var accepted = 0;
            while (sender.TrySend([1])) Assert.True(++accepted <= LinkManager.WorkQueueCapacity + 1);
            Assert.InRange(accepted, 1, LinkManager.WorkQueueCapacity + 1);
            Assert.Contains(errors, message => message.Contains("outbound queue is full", StringComparison.Ordinal));
        }
        finally
        {
            blocked.TrySetResult();
        }
    }

    [Fact]
    public async Task Link_TeardownDoesNotSpinWaitingForCompletedResourceBookkeeping()
    {
        await using var topo = await BuildAsync(false);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = topo.Initiator.Router.Links.Connect(topo.Destination.Identity,
            topo.Destination.Destination.Hash, value => established.TrySetResult(value));
        await Within(established.Task);
        Task? completion = null;
        var teardown = Task.Run(() => sender.SendWithContext([1], LinkContext.None, false, true,
            beforeSend: _ =>
            {
                var work = new TaskCompletionSource();
                sender.TrackResourceWork(work.Task);
                completion = Task.Run(() => work.SetResult());
                Assert.True(SpinWait.SpinUntil(() => work.Task.IsCompleted, TimeSpan.FromSeconds(5)));
                sender.Teardown();
            }));
        await teardown.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.IsAssignableFrom<Task>(completion).WaitAsync(TimeSpan.FromSeconds(5));
        await sender.DrainResourceWorkAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LinkStatus.Closed, sender.Status);
    }

    [Theory]
    [MemberData(nameof(Topologies))]
    public async Task Link_RequestResponse_RoundTrips(bool viaTransport)
    {
        await using var topo = await BuildAsync(viaTransport);
        topo.Destination.RegisterRequestHandler("/echo", ctx => $"echo:{ctx.Path}:{ctx.Data}", RequestPolicy.All);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => established.TrySetResult(l));
        await Within(established.Task);

        var receipt = link.Request("/echo", "hi");
        Assert.NotNull(receipt);
        Assert.Equal("echo:/echo:hi", await Within(receipt.ResponseTask));
    }

    [Fact]
    public async Task Link_KeepaliveParameters_FollowRttClamp()
    {
        await using var topo = await BuildAsync(false);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => established.TrySetResult(l));
        await Within(established.Task);
        Assert.InRange(link.KeepaliveInterval, Link.KeepaliveMin, Link.KeepaliveMax);
        Assert.Equal(link.KeepaliveInterval * Link.StaleFactor, link.StaleTime);
    }

    [Fact]
    public async Task Connect_ToUnreachableDestination_TimesOutAndCloses()
    {
        var (a1, _) = Pair();
        await using var a = new Node([a1]);
        await a.StartAsync();
        var closed = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = Identity.Generate();
        var dest = Destination.Create(DestinationType.Single, "dotreticulum", ["tests", "link"], identity);
        var link = a.Router.Links.Connect(identity, dest.Hash, closed: l => closed.TrySetResult(l), establishmentTimeout: 0.3);
        await Within(closed.Task);
        Assert.Equal(LinkStatus.Closed, link.Status);
        Assert.Equal(LinkTeardownReason.Timeout, link.TeardownReason);
    }
}
