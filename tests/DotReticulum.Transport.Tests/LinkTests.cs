using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// In-process link tests: routers connected by in-memory interfaces (direct A-B, and A-T-B through a transport node). Wire formats are checked
// against upstream vectors in LinkVectorTests; live interop is in LiveLinkTests.
public class LinkTests
{
    internal sealed class Loopback : IPacketInterface
    {
        private readonly Channel<byte[]> _inbox = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        public Loopback? Peer { get; set; }
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var p in _inbox.Reader.ReadAllAsync(cancellationToken)) yield return p;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            Peer?._inbox.Writer.TryWrite(packet.ToArray());
            return ValueTask.CompletedTask;
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
