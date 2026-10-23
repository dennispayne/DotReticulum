using DotReticulum.Core;
using static DotReticulum.Transport.Tests.LinkTests;

namespace DotReticulum.Transport.Tests;

// Lifecycle contracts: a29bfb51c468945c96a1ee22cae0114e993e2391
// Transport.py:3625 detach_interfaces; Link.py:662-705 teardown/link_closed;
// Link.py:981 authenticated blackhole rejection; Resource.py:1090 cancel;
// Buffer.py:188,269 close. Async read settlement and
// restartable Stop versus terminal Dispose are explicit .NET ownership contracts.
public class LifecycleTests
{
    [Fact]
    public async Task ManagerStopSettlesPendingLinksOnceAndAllowsRestartButDisposeIsTerminal()
    {
        await using var manager = new PacketInterfaceManager([new Loopback()]);
        await using var router = new TransportRouter(manager);
        var identity = Identity.Generate();
        var closed = 0;
        var link = router.Links.Connect(identity, new byte[16], closed: _ => Interlocked.Increment(ref closed));
        await Task.WhenAll(router.Links.StopAsync(), router.Links.StopAsync());
        Assert.Equal(LinkStatus.Closed, link.Status);
        Assert.Empty(router.Links.Links);
        Assert.Equal(1, closed);

        var restarted = router.Links.Connect(identity, new byte[16]);
        Assert.Equal(LinkStatus.Pending, restarted.Status);
        await router.Links.DisposeAsync();
        Assert.Equal(LinkStatus.Closed, restarted.Status);
        Assert.Throws<ObjectDisposedException>(() => router.Links.Connect(identity, new byte[16]));
    }

    [Fact]
    public async Task TimerFaultStillSettlesLinkAndRouterReceiptEvenWhenObserversThrow()
    {
        var iface = new Loopback();
        await using var manager = new PacketInterfaceManager([iface]);
        await manager.StartAsync();
        await using var router = new TransportRouter(manager, new TransportRouterOptions { CullInterval = TimeSpan.Zero });
        router.Log = _ => throw new InvalidOperationException("observer failed");
        var identity = Identity.Generate();
        var link = router.Links.Connect(identity, new byte[16], closed: _ => throw new InvalidOperationException("close observer failed"));
        var packet = Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], new byte[] { 1 });
        var receipt = await router.SendWithReceiptAsync(packet, identity);
        Assert.NotNull(receipt);
        receipt.TimedOut = _ => throw new InvalidOperationException("receipt observer failed");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => router.RunAsync(CancellationToken.None));
        Assert.Equal(LinkStatus.Closed, link.Status);
        Assert.False(await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(router.Links.Links);
        Assert.Equal(0, router.PendingReceiptCount);
        Assert.True(manager.Contains(iface)); // Caller, not router, owns the manager.
    }

    [Fact]
    public async Task TeardownSettlesRequestsReceiptsQueuedResourcesAndBlockedReaders()
    {
        await using var topo = await BuildAsync(false);
        var connected = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => connected.TrySetResult(l));
        await Within(connected.Task);
        using var reader = ChannelBuffer.CreateReader(1, link.GetChannel());
        var read = reader.ReadAsync(new byte[1]).AsTask();
        var failed = 0;
        var concluded = 0;
        RequestReceipt request;
        PacketReceipt packet;
        Resource first, queued;
        lock (link.Gate)
        {
            request = link.Request("/not-registered", failedCallback: _ => Interlocked.Increment(ref failed))!;
            packet = link.Send(new byte[] { 1 })!;
            first = link.SendResource(new byte[Resource.MaxEfficientSize + 100], autoCompress: false,
                concluded: _ => { Interlocked.Increment(ref concluded); throw new InvalidOperationException("observer failed"); });
            queued = link.SendResource(new byte[20_000], concluded: _ => Interlocked.Increment(ref concluded));
            Assert.Equal(ResourceStatus.Queued, queued.Status);
            link.Teardown();
            link.Teardown();
        }
        await topo.Initiator.Router.Links.StopAsync();
        Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(3)));
        await Assert.ThrowsAsync<TimeoutException>(() => request.ResponseTask);
        Assert.False(await packet.Completion);
        Assert.Equal(ResourceStatus.Failed, first.Status);
        Assert.Equal(ResourceStatus.Failed, queued.Status);
        Assert.Equal(1, failed);
        Assert.Equal(2, concluded);
        Assert.Throws<InvalidOperationException>(() => link.GetChannel());
    }

    [Fact]
    public async Task ReaderDisposeSettlesBlockedReadAndCancellationDoesNotCloseReader()
    {
        await using var manager = new PacketInterfaceManager([]);
        await using var router = new TransportRouter(manager);
        var link = Link.CreateInitiator(router.Links, Identity.Generate(), new byte[16], 1, 10);
        using var reader = ChannelBuffer.CreateReader(1, link.GetChannel());
        using var cancellation = new CancellationTokenSource();
        var canceledRead = reader.ReadAsync(new byte[1], cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRead);
        var blocked = reader.ReadAsync(new byte[1]).AsTask();
        reader.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => blocked);
        Assert.False(reader.CanRead);
        Assert.Throws<ObjectDisposedException>(() => reader.Read(new byte[1], 0, 1));
        link.Teardown();
    }

    [Fact]
    public async Task DetachRemovesOnlyAffectedPeerPathsAndLinks()
    {
        await using var topo = await BuildAsync(false);
        var connected = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => connected.TrySetResult(l));
        await Within(connected.Task);
        Assert.NotNull(link.AttachedInterface);
        topo.Initiator.Router.DetachInterface(new Loopback());
        Assert.Equal(LinkStatus.Active, link.Status);
        Assert.True(topo.Initiator.Router.HasPath(topo.Destination.Destination.Hash));
        var pending = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash);
        topo.Initiator.Router.DetachInterface(link.AttachedInterface!);
        Assert.Equal(LinkStatus.Closed, link.Status);
        Assert.Equal(LinkStatus.Closed, pending.Status);
        Assert.False(topo.Initiator.Router.HasPath(topo.Destination.Destination.Hash));
    }

    [Fact]
    public async Task BlockedIdentityCannotCreatePathOrOutboundLinkAndMalformedAnnounceIsIgnored()
    {
        var identity = Identity.Generate();
        var iface = new Loopback();
        await using var manager = new PacketInterfaceManager([iface]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions
        {
            IsIdentityBlocked = hash => hash.AsSpan().SequenceEqual(identity.Hash),
        });
        var destination = Destination.Create(DestinationType.Single, "lifecycle", ["blocked"], identity);
        var announce = Announce.Create(identity, destination);
        await router.HandleAsync(new ReceivedPacket(announce.ToArray(), announce, iface));
        Assert.False(router.HasPath(destination.Hash));
        Assert.Throws<InvalidOperationException>(() => router.Links.Connect(identity, destination.Hash));
        var malformed = Packet.Create(PacketType.Announce, DestinationType.Single, destination.Hash, new byte[] { 1 });
        await router.HandleAsync(new ReceivedPacket(malformed.ToArray(), malformed, iface));
        Assert.Equal(0, router.Engine.PathCount);
    }

    [Fact]
    public async Task RouterStopAllowsNewRunAndDisposeRejectsRunAndSend()
    {
        await using var manager = new PacketInterfaceManager([new Loopback()]);
        await manager.StartAsync();
        var router = new TransportRouter(manager);
        var first = router.RunAsync(CancellationToken.None);
        await router.StopAsync();
        await first;
        using var cancellation = new CancellationTokenSource();
        var restarted = router.RunAsync(cancellation.Token);
        Assert.False(restarted.IsCompleted);
        cancellation.Cancel();
        await restarted;
        await router.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => router.RunAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => router.SendAsync(Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], new byte[] { 1 })).AsTask());
    }

    [Fact]
    public async Task ConcurrentRequestResponseAndFailureHaveExactlyOneTerminalCallback()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var receipt = new RequestReceipt(new byte[16], 0, 10);
            var callbacks = 0;
            receipt.ResponseReceived = _ => Interlocked.Increment(ref callbacks);
            receipt.Failed = _ => Interlocked.Increment(ref callbacks);
            await Task.WhenAll(Task.Run(() => receipt.Complete("response")), Task.Run(() => receipt.Fail()));
            Assert.Equal(1, callbacks);
            var terminal = receipt.Status;
            receipt.Status = RequestStatus.Receiving;
            Assert.Equal(terminal, receipt.Status);
            if (receipt.Status == RequestStatus.Ready) Assert.Equal("response", await receipt.ResponseTask);
            else await Assert.ThrowsAsync<TimeoutException>(() => receipt.ResponseTask);
        }
    }

    [Fact]
    public async Task AuthenticatedBlockedInitiatorIsClosedBeforeApplicationNotification()
    {
        using var blocked = Identity.Generate();
        using var destinationIdentity = Identity.Generate();
        var (a, b) = Pair();
        await using var managerA = new PacketInterfaceManager([a]);
        await using var managerB = new PacketInterfaceManager([b]);
        await managerA.StartAsync();
        await managerB.StartAsync();
        await using var routerA = new TransportRouter(managerA);
        await using var routerB = new TransportRouter(managerB, new TransportRouterOptions { IsIdentityBlocked = hash => hash.AsSpan().SequenceEqual(blocked.Hash) });
        var destination = new LocalDestination(destinationIdentity, Destination.Create(DestinationType.Single, "lifecycle", ["identify"], destinationIdentity)) { AcceptLinks = true };
        var notifications = 0;
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        destination.LinkEstablished = link =>
        {
            link.RemoteIdentified = (_, _) => Interlocked.Increment(ref notifications);
            accepted.TrySetResult(link);
        };
        routerB.RegisterLocalDestination(destination);
        var runA = routerA.RunAsync(CancellationToken.None);
        var runB = routerB.RunAsync(CancellationToken.None);
        await routerB.AnnounceAsync(destination);
        await Until(() => routerA.HasPath(destination.Destination.Hash));
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = routerA.Links.Connect(destinationIdentity, destination.Destination.Hash, link => established.TrySetResult(link));
        await Within(established.Task);
        var remote = await Within(accepted.Task);
        local.Identify(blocked);
        await Until(() => remote.Status == LinkStatus.Closed && local.Status == LinkStatus.Closed);
        Assert.Equal(0, notifications);
        await routerA.StopAsync();
        await routerB.StopAsync();
        await Task.WhenAll(runA, runB);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task RawResponsePreservesWireMapKeyTypesForPacketAndResource(int payloadSize)
    {
        await using var topo = await BuildAsync(false);
        var response = new Dictionary<object, object?> { [new byte[16]] = new byte[payloadSize] };
        topo.Destination.RegisterRequestHandler("/binary-map", _ => response, RequestPolicy.All);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => established.TrySetResult(l));
        await Within(established.Task);
        var receipt = link.Request("/binary-map")!;
        await Within(receipt.ResponseTask);
        Assert.Equal(MsgPack.Pack(new object?[] { receipt.RequestId, response }), receipt.RawResponse);
    }
}
