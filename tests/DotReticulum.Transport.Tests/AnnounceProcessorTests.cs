using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

public class AnnounceProcessorTests
{
    private sealed class ScriptedInterface : IPacketInterface
    {
        public Channel<ReadOnlyMemory<byte>> Inbound { get; } = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) => Inbound.Reader.ReadAllAsync(cancellationToken);
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FaultingObserver_IsReported_AndOtherObserversAndTheNextPacketStillRun()
    {
        using var identity = Identity.Generate();
        var first = Destination.Create(DestinationType.Single, "dotreticulum", ["tests", "first"], identity);
        var second = Destination.Create(DestinationType.Single, "dotreticulum", ["tests", "second"], identity);
        var iface = new ScriptedInterface();
        await using var manager = new PacketInterfaceManager([iface]);
        await manager.StartAsync();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var engine = new AnnounceEngine { Log = errors.Enqueue };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        var processor = new AnnounceProcessor(manager, engine)
        {
            AnnounceProcessed = (received, _) =>
            {
                if (received.Packet.DestinationHash.Span.SequenceEqual(first.Hash))
                    throw new InvalidOperationException("observer failed");
            },
        };
        processor.AnnounceProcessed += (received, _) =>
        {
            Interlocked.Increment(ref notifications);
            if (received.Packet.DestinationHash.Span.SequenceEqual(second.Hash))
                delivered.TrySetResult();
        };
        var run = processor.RunAsync(cts.Token);
        try
        {
            await iface.Inbound.Writer.WriteAsync(Announce.Create(identity, first).ToArray(), cts.Token);
            await iface.Inbound.Writer.WriteAsync(Announce.Create(identity, second).ToArray(), cts.Token);
            await delivered.Task.WaitAsync(cts.Token);
            Assert.Equal(2, Volatile.Read(ref notifications));
            Assert.NotNull(engine.Known.Recall(second.Hash));
            Assert.Contains("observer failed", Assert.Single(errors));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ReceivedAnnounce_IsProcessed_EvenWithoutAnAnnounceProcessedCallback()
    {
        using var identity = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "dotreticulum", ["tests", "processor"], identity);
        var iface = new ScriptedInterface();
        await using var manager = new PacketInterfaceManager([iface]);
        await manager.StartAsync();
        var engine = new AnnounceEngine();
        using var cts = new CancellationTokenSource();
        var run = new AnnounceProcessor(manager, engine).RunAsync(cts.Token);

        await iface.Inbound.Writer.WriteAsync(Announce.Create(identity, destination, "hi"u8.ToArray()).ToArray());
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (engine.Known.Recall(destination.Hash) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "announce was never processed");
            await Task.Delay(10);
        }

        await cts.CancelAsync();
        await run;
    }
}
