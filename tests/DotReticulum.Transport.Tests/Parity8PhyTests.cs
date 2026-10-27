using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

[Trait("Category", "Unit")]
public sealed class Parity8PhyTests
{
    private sealed class Bearer : IPacketInterfacePhysicalLayer
    {
        private readonly System.Threading.Channels.Channel<ReceivedFrame> _received =
            System.Threading.Channels.Channel.CreateBounded<ReceivedFrame>(8);
        private readonly object _gate = new();
        private PacketPhysicalLayer _reading;
        public Bearer? Peer { get; set; }
        public void Reading(PacketPhysicalLayer value) { lock (_gate) _reading = value; }
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ReceivedFrame> ReceiveFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in _received.Reader.ReadAllAsync(cancellationToken)) yield return frame;
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in ReceiveFramesAsync(cancellationToken)) yield return frame.Data;
        }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            var peer = Peer!;
            PacketPhysicalLayer phy;
            lock (peer._gate) phy = peer._reading;
            return peer._received.Writer.WriteAsync(new(packet.ToArray(), phy), cancellationToken);
        }
        public ValueTask Inject(Packet packet, PacketPhysicalLayer phy) => _received.Writer.WriteAsync(new(packet.ToArray(), phy));
        public ValueTask DisposeAsync() { _received.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class Node : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _run = Task.CompletedTask;
        public PacketInterfaceManager Manager { get; }
        public TransportRouter Router { get; }
        public Node(bool transport, params IPacketInterface[] interfaces)
        {
            Manager = new(interfaces);
            Router = new(Manager, new() { TransportEnabled = transport, PathRequestGrace = TimeSpan.Zero });
        }
        public async Task Start()
        {
            await Manager.StartAsync();
            _run = Router.RunAsync(_stop.Token);
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try { await _run; } catch (OperationCanceledException) { }
            await Router.DisposeAsync();
            await Manager.DisposeAsync();
            _stop.Dispose();
        }
    }

    private static (Bearer A, Bearer B) Pair()
    {
        var a = new Bearer();
        var b = new Bearer { Peer = a };
        a.Peer = b;
        return (a, b);
    }

    private static LocalDestination Register(Node node, Identity identity)
    {
        var destination = new LocalDestination(identity,
            Destination.Create(DestinationType.Single, "dotreticulum", ["tests", "phy"], identity))
        { AcceptLinks = true, ProofStrategy = ProofStrategy.All };
        node.Router.RegisterLocalDestination(destination);
        return destination;
    }

    private static PacketPhysicalLayer ReferenceEnabled()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Parity8PhyVectors.json")));
        var values = doc.RootElement.GetProperty("link").GetProperty("enabled").EnumerateArray().ToArray();
        return new(values[0].GetDouble(), values[1].GetDouble(), values[2].GetDouble());
    }

    [Fact]
    public async Task OptInTrackingUsesAuthenticatedPacketsWithoutSubscribersAndReceiptRetainsProofSnapshot()
    {
        var (a, b) = Pair();
        await using var initiator = new Node(false, a);
        await using var responder = new Node(false, b);
        await initiator.Start();
        await responder.Start();
        using var identity = Identity.Generate();
        var destination = Register(responder, identity);
        await responder.Router.AnnounceAsync(destination);
        await LinkTests.Until(() => initiator.Router.HasPath(destination.Destination.Hash));
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        destination.LinkEstablished = link => accepted.TrySetResult(link);
        var local = initiator.Router.Links.Connect(identity, destination.Destination.Hash);
        var remote = await LinkTests.Within(accepted.Task);
        await LinkTests.Until(() => local.Status == LinkStatus.Active);
        var expected = ReferenceEnabled();
        b.Reading(expected);
        a.Reading(new(-91, 4.25, 88.3));
        // No PacketReceived/receipt callback is installed on either link.
        var receipt = local.Send("disabled"u8.ToArray())!;
        Assert.True(await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(default, remote.PhysicalLayer);
        Assert.Equal(new(-91, 4.25, 88.3), receipt.ProofPacket!.PhysicalLayer);
        remote.TrackPhysicalLayer(true);
        Assert.True(await local.Send("enabled"u8.ToArray())!.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(expected, remote.PhysicalLayer);
        b.Reading(default);
        Assert.True(await local.Send("absent"u8.ToArray())!.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(expected, remote.PhysicalLayer);
        remote.TrackPhysicalLayer(false);
        b.Reading(new(-70, 10, 100));
        Assert.True(await local.Send("disabled again"u8.ToArray())!.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(default, remote.PhysicalLayer);
        remote.TrackPhysicalLayer(true);
        Assert.Equal(default, remote.PhysicalLayer);
        // An unauthenticated ciphertext cannot overwrite the link's retained measurements.
        var before = remote.PacketsReceived;
        await b.Inject(Packet.Create(PacketType.Data, DestinationType.Link, remote.LinkId, new byte[64]), new(-5, 20, 100));
        await LinkTests.Until(() => remote.PacketsReceived > before);
        Assert.Equal(default, remote.PhysicalLayer);
        Assert.True(await local.Send("fresh"u8.ToArray())!.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new(-70, 10, 100), remote.PhysicalLayer);
        Assert.Equal(new(-91, 4.25, 88.3), receipt.ProofPacket.PhysicalLayer);
    }

    [Fact]
    public async Task TransitRfMeasurementsStayOnRouterAndDoNotBecomeEndpointTcpMeasurements()
    {
        var (a, t1) = Pair();
        var (t2, b) = Pair();
        t1.Reading(new(-90, 4, 80));
        t2.Reading(new(-110, -5, 20));
        await using var initiator = new Node(false, a);
        await using var transit = new Node(true, t1, t2);
        await using var responder = new Node(false, b);
        await initiator.Start();
        await transit.Start();
        await responder.Start();
        using var identity = Identity.Generate();
        var destination = Register(responder, identity);
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        destination.LinkEstablished = link => { link.TrackPhysicalLayer(true); accepted.TrySetResult(link); };
        await responder.Router.AnnounceAsync(destination);
        await LinkTests.Until(() => initiator.Router.HasPath(destination.Destination.Hash));
        var local = initiator.Router.Links.Connect(identity, destination.Destination.Hash);
        local.TrackPhysicalLayer(true);
        var remote = await LinkTests.Within(accepted.Task);
        await LinkTests.Until(() => local.Status == LinkStatus.Active);
        var receipt = local.Send("transit only"u8.ToArray())!;
        Assert.True(await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(default, local.PhysicalLayer);
        Assert.Equal(default, remote.PhysicalLayer);
        Assert.Equal(default, receipt.ProofPacket!.PhysicalLayer);
        Assert.Equal(new(-110, -5, 20), transit.Manager.GetPacketPhysicalLayer(receipt.ProofPacket.CalculateHash()));
        Assert.Equal(default, initiator.Manager.GetPacketPhysicalLayer(receipt.ProofPacket.CalculateHash()));
    }
}
