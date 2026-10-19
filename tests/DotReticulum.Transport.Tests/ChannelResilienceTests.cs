using System.Collections.Concurrent;
using System.Text;
using DotReticulum.Core;
using static DotReticulum.Transport.Tests.LinkTests;

namespace DotReticulum.Transport.Tests;

// Retry/teardown semantics checked against upstream Channel.py at e40191b3d193b46b7f2d8a44424a594cd758839b.
public sealed class ChannelResilienceTests
{
    public sealed class Text : MessageBase
    {
        public string Value { get; set; } = "";
        public override ushort MsgType => 0x0101;
        public override byte[] Pack() => Encoding.UTF8.GetBytes(Value);
        public override void Unpack(ReadOnlySpan<byte> raw) => Value = Encoding.UTF8.GetString(raw);
    }

    private sealed class Wire : IPacketInterface
    {
        private readonly System.Threading.Channels.Channel<byte[]> _inbox = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        public Wire Peer { get; set; } = null!;
        public Func<Packet, bool>? Drop { get; set; }
        public Task? ProofGate { get; set; }
        public ConcurrentQueue<Packet> Sent { get; } = new();
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var raw in _inbox.Reader.ReadAllAsync(cancellationToken)) yield return raw;
        }
        public async ValueTask SendAsync(ReadOnlyMemory<byte> raw, CancellationToken cancellationToken)
        {
            var packet = Packet.Parse(raw);
            Sent.Enqueue(packet);
            if (Drop?.Invoke(packet) == true) return;
            if (packet.PacketType == PacketType.Proof && ProofGate is { } gate)
                await gate.WaitAsync(cancellationToken);
            await Task.Delay(20, cancellationToken);
            await Peer._inbox.Writer.WriteAsync(raw.ToArray(), cancellationToken);
        }
        public ValueTask DisposeAsync()
        {
            _inbox.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Endpoint : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _run = Task.CompletedTask;
        public PacketInterfaceManager Manager { get; }
        public TransportRouter Router { get; }
        public Endpoint(Wire wire)
        {
            Manager = new PacketInterfaceManager([wire]);
            Router = new TransportRouter(Manager);
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
            await Manager.DisposeAsync();
            _stop.Dispose();
        }
    }

    private static async Task<(Link Tx, Link Rx)> Connect(Endpoint a, Endpoint b, LocalDestination destination)
    {
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        destination.LinkEstablished = l => accepted.TrySetResult(l);
        var tx = a.Router.Links.Connect(destination.Identity, destination.Destination.Hash, l => active.TrySetResult(l));
        await active.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rx = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => rx.Status == LinkStatus.Active);
        return (tx, rx);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostDataOrProof_RetriesWithoutDuplicates_ReleasesFullWindow(bool loseProof)
    {
        var ab = new Wire();
        var ba = new Wire { Peer = ab };
        ab.Peer = ba;
        await using var a = new Endpoint(ab);
        await using var b = new Endpoint(ba);
        await a.Start();
        await b.Start();
        var identity = Identity.Generate();
        var destination = new LocalDestination(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["resilience"], identity)) { AcceptLinks = true };
        b.Router.RegisterLocalDestination(destination);
        await b.Router.AnnounceAsync(destination);
        await Until(() => a.Router.HasPath(destination.Destination.Hash));
        var (tx, rx) = await Connect(a, b, destination);
        var received = new ConcurrentQueue<string>();
        var receiver = rx.GetChannel();
        receiver.RegisterMessageType<Text>();
        receiver.AddMessageHandler(m => { received.Enqueue(((Text)m).Value); return true; });
        var channel = tx.GetChannel();
        channel.RegisterMessageType<Text>();
        var proofs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ba.ProofGate = proofs.Task;

        var dropped = 0;
        if (loseProof)
            ba.Drop = p => p.PacketType == PacketType.Proof && Interlocked.CompareExchange(ref dropped, 1, 0) == 0;
        else
            ab.Drop = p => p.Context == LinkContext.Channel && Interlocked.CompareExchange(ref dropped, 1, 0) == 0;

        var first = channel.Send(new Text { Value = "first" });
        var second = channel.Send(new Text { Value = "second" });
        Assert.False(channel.IsReadyToSend);
        Assert.Equal(ChannelExceptionType.LinkNotReady, Assert.Throws<ChannelException>(() => channel.Send(new Text { Value = "must not send" })).Type);
        proofs.SetResult();
        await Until(() => first.State == MessageState.Delivered && second.State == MessageState.Delivered);
        Assert.Equal(1, dropped);
        Assert.True(first.Tries >= 2, $"Expected retry after controlled loss; tries={first.Tries}");
        Assert.Equal(new[] { "first", "second" }, received.ToArray());
        Assert.True(channel.IsReadyToSend);
        var third = channel.Send(new Text { Value = "recovered" });
        Assert.Equal(2, third.Sequence);
        await Until(() => third.State == MessageState.Delivered);
        await Task.Delay(300);
        Assert.Equal(new[] { "first", "second", "recovered" }, received.ToArray());
        Assert.Equal(LinkStatus.Active, tx.Status);
        tx.Teardown();
        await Until(() => a.Router.Links.Links.Count == 0 && b.Router.Links.Links.Count == 0);
    }

    [Fact]
    public async Task Writer_WaitsForExhaustedWindow_ThenResumesAfterProofLossEnds()
    {
        var ab = new Wire();
        var ba = new Wire { Peer = ab };
        ab.Peer = ba;
        await using var a = new Endpoint(ab);
        await using var b = new Endpoint(ba);
        await a.Start();
        await b.Start();
        var identity = Identity.Generate();
        var destination = new LocalDestination(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["resilience"], identity)) { AcceptLinks = true };
        b.Router.RegisterLocalDestination(destination);
        await b.Router.AnnounceAsync(destination);
        await Until(() => a.Router.HasPath(destination.Destination.Hash));
        var (tx, rx) = await Connect(a, b, destination);
        rx.GetChannel().RegisterMessageType<Text>();
        using var reader = new RawChannelReader(7, rx.GetChannel());
        using var writer = new RawChannelWriter(7, tx.GetChannel());
        var loseProofs = 1;
        ba.Drop = p => p.PacketType == PacketType.Proof && Volatile.Read(ref loseProofs) == 1;
        tx.GetChannel().Send(new Text { Value = "occupy slot 1" });
        tx.GetChannel().Send(new Text { Value = "occupy slot 2" });
        Assert.False(tx.GetChannel().IsReadyToSend);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var writing = writer.WriteAsync("after backpressure"u8.ToArray(), timeout.Token).AsTask();
        await Task.Delay(100, timeout.Token);
        Assert.False(writing.IsCompleted, "Stream writer must wait, not silently discard data when its channel window is full.");
        Volatile.Write(ref loseProofs, 0);
        await writing.WaitAsync(timeout.Token);
        var bytes = new byte["after backpressure"u8.Length];
        await reader.ReadExactlyAsync(bytes, timeout.Token);
        Assert.Equal("after backpressure"u8.ToArray(), bytes);
        Assert.Equal(LinkStatus.Active, tx.Status);
        tx.Teardown();
        await Until(() => a.Router.Links.Links.Count == 0 && b.Router.Links.Links.Count == 0);
    }

    [Fact]
    public async Task PersistentLoss_ExhaustsRetries_ClosesPeers_CleansRegistries_AndNewLinkRecovers()
    {
        var ab = new Wire();
        var ba = new Wire { Peer = ab };
        ab.Peer = ba;
        await using var a = new Endpoint(ab);
        await using var b = new Endpoint(ba);
        await a.Start();
        await b.Start();
        var identity = Identity.Generate();
        var destination = new LocalDestination(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["resilience"], identity)) { AcceptLinks = true };
        b.Router.RegisterLocalDestination(destination);
        await b.Router.AnnounceAsync(destination);
        await Until(() => a.Router.HasPath(destination.Destination.Hash));
        var (tx, rx) = await Connect(a, b, destination);
        var receiver = rx.GetChannel();
        receiver.RegisterMessageType<Text>();
        var received = new ConcurrentQueue<string>();
        receiver.AddMessageHandler(m => { received.Enqueue(((Text)m).Value); return true; });
        var channel = tx.GetChannel();
        ab.Drop = p => p.Context == LinkContext.Channel;
        var envelope = channel.Send(new Text { Value = "never delivered" });
        await Until(() => tx.Status == LinkStatus.Closed && rx.Status == LinkStatus.Closed, 30);
        Assert.Equal(5, envelope.Tries);
        Assert.Equal(MessageState.Failed, envelope.State);
        Assert.Equal(5, ab.Sent.Count(p => p.Context == LinkContext.Channel));
        Assert.Empty(received);
        Assert.Empty(a.Router.Links.Links);
        Assert.Empty(b.Router.Links.Links);
        Assert.Equal(ChannelExceptionType.LinkNotReady, Assert.Throws<ChannelException>(() => channel.Send(new Text { Value = "closed" })).Type);
        await Task.Delay(400);
        Assert.Equal(5, ab.Sent.Count(p => p.Context == LinkContext.Channel));

        ab.Drop = null;
        var (newTx, newRx) = await Connect(a, b, destination);
        Assert.NotEqual(tx.LinkIdHex, newTx.LinkIdHex);
        var recovered = new ConcurrentQueue<string>();
        newRx.GetChannel().RegisterMessageType<Text>();
        newRx.GetChannel().AddMessageHandler(m => { recovered.Enqueue(((Text)m).Value); return true; });
        var delivered = newTx.GetChannel().Send(new Text { Value = "new session" });
        Assert.Equal(0, delivered.Sequence);
        await Until(() => delivered.State == MessageState.Delivered);
        Assert.Equal(new[] { "new session" }, recovered.ToArray());
        Assert.Empty(received);
        newTx.Teardown();
        await Until(() => a.Router.Links.Links.Count == 0 && b.Router.Links.Links.Count == 0);
    }
}
