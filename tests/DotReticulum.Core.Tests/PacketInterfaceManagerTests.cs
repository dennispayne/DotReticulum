using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Core.Tests;

public class PacketInterfaceManagerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class FakeInterface : IPacketInterface
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private int _disposed;

        public int StartCalls;
        public int DisposeCalls;
        public Exception? StartFailure;
        public Exception? SendFailure;
        public Exception? DisposeFailure;
        public List<byte[]> Sent { get; } = [];

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StartCalls);
            return StartFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(StartFailure);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in _incoming.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (SendFailure is not null)
            {
                return ValueTask.FromException(SendFailure);
            }

            lock (Sent)
            {
                Sent.Add(packet.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public void Inject(byte[] data) => Assert.True(_incoming.Writer.TryWrite(data));

        public void Fault(Exception ex) => _incoming.Writer.TryComplete(ex);

        public void EndStream() => _incoming.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _incoming.Writer.TryComplete();
            }

            return DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
        }
    }

    private static Packet MakePacket(byte tag) =>
        Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[] { tag, 1, 2, 3 });

    private static async Task<T> Next<T>(IAsyncEnumerator<T> e)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var move = e.MoveNextAsync().AsTask();
        Assert.True(await move.WaitAsync(Timeout), "stream ended unexpectedly");
        return e.Current;
    }

    [Fact]
    public async Task ReceivesParsedPacketsWithSource()
    {
        var a = new FakeInterface();
        var b = new FakeInterface();
        await using var manager = new PacketInterfaceManager([a, b]);
        await manager.StartAsync();
        await using var e = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();

        a.Inject(MakePacket(1).ToArray());
        var first = await Next(e);
        Assert.Same(a, first.Source);
        Assert.Equal(1, first.Packet.Payload.Span[0]);
        Assert.Equal(MakePacket(1).ToArray(), first.Raw.ToArray());

        b.Inject(MakePacket(2).ToArray());
        var second = await Next(e);
        Assert.Same(b, second.Source);
    }

    [Fact]
    public async Task StartTwiceThrowsAndStartsEachInterfaceOnce()
    {
        var a = new FakeInterface();
        await using var manager = new PacketInterfaceManager([a]);
        await manager.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.StartAsync());
        Assert.Equal(1, a.StartCalls);
    }

    [Fact]
    public async Task MalformedPacketsAreDroppedAndValidOnesStillFlow()
    {
        var a = new FakeInterface();
        await using var manager = new PacketInterfaceManager([a]);
        await manager.StartAsync();
        await using var e = manager.ReceiveAsync().GetAsyncEnumerator();

        a.Inject([]);
        a.Inject(new byte[10]);
        a.Inject(new byte[Packet.Mtu + 1]);
        var noPayload = new byte[Packet.Header1Size];
        a.Inject(noPayload);
        a.Inject(MakePacket(9).ToArray());

        var packet = await Next(e);
        Assert.Equal(9, packet.Payload.Span[0]);
    }

    [Fact]
    public async Task FailedStartDisposesEverythingAndRethrowsOriginal()
    {
        var a = new FakeInterface();
        var bad = new FakeInterface { StartFailure = new IOException("boom") };
        var never = new FakeInterface();
        var manager = new PacketInterfaceManager([a, bad, never]);

        var ex = await Assert.ThrowsAsync<IOException>(async () => await manager.StartAsync());
        Assert.Equal("boom", ex.Message);
        Assert.Equal(1, a.DisposeCalls);
        Assert.Equal(1, bad.DisposeCalls);
        Assert.Equal(1, never.DisposeCalls);
        Assert.Equal(0, never.StartCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await manager.StartAsync());
        await manager.DisposeAsync();
        Assert.Equal(1, a.DisposeCalls);
    }

    [Fact]
    public async Task CancelledStartDisposesInterfaces()
    {
        var a = new FakeInterface();
        var manager = new PacketInterfaceManager([a]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await manager.StartAsync(cts.Token));
        Assert.Equal(1, a.DisposeCalls);
    }

    [Fact]
    public async Task DisposeDuringReceiveEndsTheStreamAndDisposesInterfacesOnce()
    {
        var a = new FakeInterface();
        var b = new FakeInterface();
        var manager = new PacketInterfaceManager([a, b]);
        await manager.StartAsync();
        var reader = Task.Run(async () =>
        {
            await foreach (var _ in manager.ReceiveAsync())
            {
            }
        });

        await Task.Delay(50);
        await Task.WhenAll(manager.DisposeAsync().AsTask(), manager.DisposeAsync().AsTask());
        await manager.DisposeAsync();
        await reader.WaitAsync(Timeout);
        Assert.Equal(1, a.DisposeCalls);
        Assert.Equal(1, b.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await manager.SendAsync(MakePacket(0)));
    }

    [Fact]
    public async Task DisposeAggregatesInterfaceExceptionsAndStillDisposesAll()
    {
        var a = new FakeInterface { DisposeFailure = new IOException("a") };
        var b = new FakeInterface { DisposeFailure = new InvalidOperationException("b") };
        var c = new FakeInterface();
        var manager = new PacketInterfaceManager([a, b, c]);
        await manager.StartAsync();

        var ex = await Assert.ThrowsAsync<AggregateException>(async () => await manager.DisposeAsync());
        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Equal(1, c.DisposeCalls);

        var single = new FakeInterface { DisposeFailure = new IOException("only") };
        var m2 = new PacketInterfaceManager([single]);
        await m2.StartAsync();
        await Assert.ThrowsAsync<IOException>(async () => await m2.DisposeAsync());
    }

    [Fact]
    public async Task BackpressureBlocksReceiverWhenChannelFullWithoutLoss()
    {
        var a = new FakeInterface();
        await using var manager = new PacketInterfaceManager([a], channelCapacity: 2);
        await manager.StartAsync();

        const int Count = 20;
        for (var i = 0; i < Count; i++)
        {
            a.Inject(MakePacket((byte)i).ToArray());
        }

        await Task.Delay(200);
        await using var e = manager.ReceiveAsync().GetAsyncEnumerator();
        for (var i = 0; i < Count; i++)
        {
            var p = await Next(e);
            Assert.Equal(i, p.Payload.Span[0]);
        }
    }

    [Fact]
    public async Task DisposeWhileReceiverBlockedOnFullChannelCompletes()
    {
        var a = new FakeInterface();
        var manager = new PacketInterfaceManager([a], channelCapacity: 1);
        await manager.StartAsync();
        for (var i = 0; i < 5; i++)
        {
            a.Inject(MakePacket((byte)i).ToArray());
        }

        await Task.Delay(100);
        await manager.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task SendGoesToAllAndSendExceptSkipsExcluded()
    {
        var a = new FakeInterface();
        var b = new FakeInterface();
        var c = new FakeInterface();
        await using var manager = new PacketInterfaceManager([a, b, c]);
        await manager.StartAsync();

        var p = MakePacket(5);
        await manager.SendAsync(p);
        Assert.All(new[] { a, b, c }, i => Assert.Equal(p.ToArray(), Assert.Single(i.Sent)));

        await manager.SendExceptAsync(p, b);
        Assert.Equal(2, a.Sent.Count);
        Assert.Single(b.Sent);
        Assert.Equal(2, c.Sent.Count);
    }

    [Fact]
    public async Task SendExceptValidatesArguments()
    {
        var a = new FakeInterface();
        await using var manager = new PacketInterfaceManager([a]);
        await manager.StartAsync();
        var p = MakePacket(1);

        Assert.Throws<ArgumentNullException>(() => manager.SendExceptAsync(p, null!));
        Assert.Throws<ArgumentException>(() => manager.SendExceptAsync(p, new FakeInterface()));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await manager.SendAsync(null!));
    }

    [Fact]
    public async Task SendBeforeStartThrows()
    {
        await using var manager = new PacketInterfaceManager([new FakeInterface()]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.SendAsync(MakePacket(1)));
    }

    [Fact]
    public async Task SendFailureOnOneInterfaceStillDeliversToOthers()
    {
        var a = new FakeInterface { SendFailure = new IOException("down") };
        var b = new FakeInterface();
        await using var manager = new PacketInterfaceManager([a, b]);
        await manager.StartAsync();

        var ex = await Assert.ThrowsAsync<IOException>(async () => await manager.SendAsync(MakePacket(1)));
        Assert.Equal("down", ex.Message);
        Assert.Single(b.Sent);

        a.SendFailure = new IOException("x");
        b.SendFailure = new IOException("y");
        await Assert.ThrowsAsync<AggregateException>(async () => await manager.SendAsync(MakePacket(1)));
    }

    [Fact]
    public async Task ReceiverFaultCompletesChannelWithThatException()
    {
        var a = new FakeInterface();
        var b = new FakeInterface();
        await using var manager = new PacketInterfaceManager([a, b]);
        await manager.StartAsync();
        await using var e = manager.ReceiveAsync().GetAsyncEnumerator();

        a.Inject(MakePacket(1).ToArray());
        Assert.Equal(1, (await Next(e)).Payload.Span[0]);

        a.Fault(new IOException("link lost"));
        var ex = await Assert.ThrowsAsync<IOException>(async () => await e.MoveNextAsync().AsTask().WaitAsync(Timeout));
        Assert.Equal("link lost", ex.Message);
    }

    [Fact]
    public async Task CleanEndOfOneInterfaceDoesNotEndOthersButAllEndedCompletesChannel()
    {
        var a = new FakeInterface();
        var b = new FakeInterface();
        await using var manager = new PacketInterfaceManager([a, b]);
        await manager.StartAsync();
        await using var e = manager.ReceiveAsync().GetAsyncEnumerator();

        a.EndStream();
        await Task.Delay(50);
        b.Inject(MakePacket(3).ToArray());
        Assert.Equal(3, (await Next(e)).Payload.Span[0]);

        b.EndStream();
        Assert.False(await e.MoveNextAsync().AsTask().WaitAsync(Timeout));
    }

    [Fact]
    public async Task ReceiveHonoursConsumerCancellation()
    {
        var a = new FakeInterface();
        await using var manager = new PacketInterfaceManager([a]);
        await manager.StartAsync();
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in manager.ReceiveAsync(cts.Token))
            {
            }
        });
    }

    [Fact]
    public void ConstructorValidatesArguments()
    {
        var a = new FakeInterface();
        Assert.Throws<ArgumentNullException>(() => new PacketInterfaceManager(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketInterfaceManager([a], 0));
        Assert.Throws<ArgumentException>(() => new PacketInterfaceManager([a, a]));
    }

    private sealed class FakeSpawner : ISpawningPacketInterface
    {
        private readonly Channel<ReadOnlyMemory<byte>> _never = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public event Action<IPacketInterface>? ChildConnected;
        public void Spawn(IPacketInterface child) => ChildConnected?.Invoke(child);
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) => _never.Reader.ReadAllAsync(cancellationToken);
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _never.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task SpawnedChildrenAreAdoptedReceivedFromAndRemovedWhenTheirStreamEnds()
    {
        var spawner = new FakeSpawner();
        await using var manager = new PacketInterfaceManager([spawner]);
        await manager.StartAsync();
        await using var e = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();

        var child1 = new FakeInterface();
        var child2 = new FakeInterface();
        spawner.Spawn(child1);
        spawner.Spawn(child2);
        Assert.True(manager.Contains(child1));
        Assert.Equal(3, manager.Interfaces.Count);

        child2.Inject(MakePacket(9).ToArray());
        Assert.Same(child2, (await Next(e)).Source);

        // A broadcast reaches every child; a unicast and SendExcept reach exactly the intended ones.
        await manager.SendAsync(MakePacket(1));
        await manager.SendExceptAsync(MakePacket(2), child1);
        await manager.SendToAsync(MakePacket(3), child1);
        Assert.Equal([1, 3], child1.Sent.Select(p => Packet.Parse(p).Payload.Span[0]).Select(b => (int)b));
        Assert.Equal([1, 2], child2.Sent.Select(p => Packet.Parse(p).Payload.Span[0]).Select(b => (int)b));

        child1.EndStream();
        await WaitUntil(() => !manager.Contains(child1));
        Assert.Equal(1, child1.DisposeCalls);
        await Assert.ThrowsAsync<ArgumentException>(async () => await manager.SendToAsync(MakePacket(4), child1));
        Assert.True(manager.Contains(child2));
    }

    [Fact]
    public async Task FaultingChildIsRemovedWithoutFaultingTheManager()
    {
        var spawner = new FakeSpawner();
        await using var manager = new PacketInterfaceManager([spawner]);
        await manager.StartAsync();
        await using var e = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();

        var bad = new FakeInterface();
        var good = new FakeInterface();
        spawner.Spawn(bad);
        spawner.Spawn(good);
        bad.Fault(new IOException("connection reset"));
        await WaitUntil(() => !manager.Contains(bad));

        good.Inject(MakePacket(5).ToArray());
        Assert.Same(good, (await Next(e)).Source);
    }

    [Fact]
    public async Task ChildSpawnedAfterDisposalIsDisposedNotAdopted()
    {
        var spawner = new FakeSpawner();
        var manager = new PacketInterfaceManager([spawner]);
        await manager.StartAsync();
        await manager.DisposeAsync();

        var late = new FakeInterface();
        spawner.Spawn(late); // the manager already unsubscribed, so nothing adopts it
        Assert.False(manager.Contains(late));
    }

    [Fact]
    public async Task SendToRequiresStartedManagerAndKnownInterface()
    {
        var a = new FakeInterface();
        await using var manager = new PacketInterfaceManager([a]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.SendToAsync(MakePacket(1), a));
        await manager.StartAsync();
        await Assert.ThrowsAsync<ArgumentException>(async () => await manager.SendToAsync(MakePacket(1), new FakeInterface()));
        await manager.SendToAsync(MakePacket(1), a);
        Assert.Single(a.Sent);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(10);
        }
    }
}
