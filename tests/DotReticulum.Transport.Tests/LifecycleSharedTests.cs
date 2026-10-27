using DotReticulum.Core;
using static DotReticulum.Transport.Tests.LinkTests;

namespace DotReticulum.Transport.Tests;

// Pinned a29bfb51c468945c96a1ee22cae0114e993e2391 Transport.py:
// 1937 local/shared hop adjustment; 1981 plain relay; 1997 local-client
// transport despite enable_transport=false; 2399 immediate announce relay;
// 3420 path requests. Four actual native routers, in-process bearers, not live Python evidence.
[Trait("Category", "Unit")]
public class LifecycleSharedTests
{
    private sealed class RuntimeNode : IAsyncDisposable
    {
        private Task _run = Task.CompletedTask;
        private readonly List<Identity> _identities = [];
        public PacketInterfaceManager Manager { get; }
        public TransportRouter Router { get; }

        public RuntimeNode(params Loopback[] interfaces)
        {
            Manager = new PacketInterfaceManager(interfaces);
            Router = new TransportRouter(Manager, new TransportRouterOptions { PathRequestGrace = TimeSpan.Zero });
        }

        public async Task StartAsync()
        {
            await Manager.StartAsync();
            _run = Router.RunAsync(CancellationToken.None);
        }

        public LocalDestination Serve(string name)
        {
            var identity = Identity.Generate();
            _identities.Add(identity);
            var destination = new LocalDestination(identity, Destination.Create(DestinationType.Single, "lifecycle", [name], identity))
            {
                AcceptLinks = true,
            };
            Router.RegisterLocalDestination(destination);
            return destination;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Router.DisposeAsync();
                await _run;
            }
            finally
            {
                try { await Manager.DisposeAsync(); }
                finally { foreach (var identity in _identities) identity.Dispose(); }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedOwnerRoutesClientLinksAndResourcesWithoutPublicTransport(bool destinationOnOtherClient)
    {
        var (one, ownerOne) = Pair();
        var (two, ownerTwo) = Pair();
        var (external, ownerExternal) = Pair();
        await using var owner = new RuntimeNode(ownerOne, ownerTwo, ownerExternal);
        await using var clientOne = new RuntimeNode(one);
        await using var clientTwo = new RuntimeNode(two);
        await using var physical = new RuntimeNode(external);
        owner.Router.RegisterLocalClientInterface(ownerOne);
        owner.Router.RegisterLocalClientInterface(ownerTwo);
        clientOne.Router.ConfigureSharedClient(true);
        clientTwo.Router.ConfigureSharedClient(true);
        foreach (var node in new[] { owner, clientOne, clientTwo, physical }) await node.StartAsync();

        var targetNode = destinationOnOtherClient ? clientTwo : physical;
        var destination = targetNode.Serve("target");
        var source = clientOne.Serve("source");
        await targetNode.Router.AnnounceAsync(destination);
        await clientOne.Router.AnnounceAsync(source);
        await Until(() => clientOne.Router.HasPath(destination.Destination.Hash) && physical.Router.HasPath(source.Destination.Hash));
        Assert.Equal(destinationOnOtherClient ? 0 : 1, clientOne.Router.HopsTo(destination.Destination.Hash));
        Assert.Equal(0, owner.Router.HopsTo(source.Destination.Hash));
        Assert.False(owner.Router.TransportEnabled);

        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        destination.LinkEstablished = link =>
        {
            link.ResourceStrategy = ResourceStrategy.AcceptAll;
            accepted.TrySetResult(link);
        };
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = clientOne.Router.Links.Connect(destination.Identity, destination.Destination.Hash, link => established.TrySetResult(link));
        await Within(established.Task);
        var remote = await Within(accepted.Task);
        var received = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.ResourceReceived = resource => received.TrySetResult(resource);
        var payload = Enumerable.Range(0, 65_536).Select(i => (byte)i).ToArray();
        local.SendResource(payload, autoCompress: false, concluded: resource => sent.TrySetResult(resource));
        Assert.Equal(payload, (await Within(received.Task)).Data);
        Assert.Equal(ResourceStatus.Complete, (await Within(sent.Task)).Status);
        Assert.Equal(1, owner.Router.ActiveLinkTableCount);

        clientOne.Router.DetachInterface(one);
        owner.Router.DetachInterface(ownerOne);
        Assert.Equal(LinkStatus.Closed, local.Status);
        Assert.Empty(clientOne.Router.Paths);
        Assert.False(owner.Router.HasPath(source.Destination.Hash));
        Assert.True(owner.Router.IsLocalClientInterface(ownerTwo));
        Assert.True(owner.Router.HasPath(destination.Destination.Hash));
    }

    [Fact]
    public async Task SharedOwnerRelaysPlainTrafficAndFindsClientDestinationByPathRequest()
    {
        var (one, ownerOne) = Pair();
        var (two, ownerTwo) = Pair();
        var (external, ownerExternal) = Pair();
        await using var owner = new RuntimeNode(ownerOne, ownerTwo, ownerExternal);
        await using var clientOne = new RuntimeNode(one);
        await using var clientTwo = new RuntimeNode(two);
        await using var physical = new RuntimeNode(external);
        owner.Router.RegisterLocalClientInterface(ownerOne);
        owner.Router.RegisterLocalClientInterface(ownerTwo);
        clientOne.Router.ConfigureSharedClient(true);
        clientTwo.Router.ConfigureSharedClient(true);
        foreach (var node in new[] { owner, clientOne, clientTwo, physical }) await node.StartAsync();
        var destination = clientTwo.Serve("path");
        await physical.Router.RequestPathAsync(destination.Destination.Hash);
        await Until(() => physical.Router.HasPath(destination.Destination.Hash));
        Assert.Equal(1, physical.Router.HopsTo(destination.Destination.Hash));
        Assert.Equal(0, owner.Router.HopsTo(destination.Destination.Hash));

        var plain = Destination.Create(DestinationType.Plain, "lifecycle", ["plain"]);
        var gotOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var identity = Identity.Generate();
        clientOne.Router.RegisterLocalDestination(new LocalDestination(identity, plain)
        {
            PacketReceived = _ => { gotOne.TrySetResult(); return ValueTask.CompletedTask; },
        });
        clientTwo.Router.RegisterLocalDestination(new LocalDestination(identity, plain)
        {
            PacketReceived = _ => { gotTwo.TrySetResult(); return ValueTask.CompletedTask; },
        });
        await physical.Router.SendAsync(Packet.Create(PacketType.Data, DestinationType.Plain, plain.Hash, new byte[] { 1 }));
        await Task.WhenAll(gotOne.Task, gotTwo.Task).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
