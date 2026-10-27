using System.Runtime.CompilerServices;
using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// GenerateParity6OutgoingVectors.py executes Transport.py at hosted a29bfb51c468945c96a1ee22cae0114e993e2391.
[Trait("Category", "Unit")]
public sealed class Parity6OutgoingTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-outgoing.json")));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("branch").GetString()!, item.GetProperty("outgoing").GetBoolean(), item.GetProperty("frames").GetInt32()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RouterSelectionAndDirectTransmissionMatchActualReference(string branch, bool outgoing, int frames)
    {
        var bearer = new RecordingInterface();
        await using var manager = new PacketInterfaceManager([bearer]);
        manager.SetInterfaceOutgoing(bearer, outgoing);
        await manager.StartAsync();
        await using var router = new TransportRouter(manager);
        using var identity = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "dotreticulum", ["outgoing"], identity);
        var packet = Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], "selection-oracle"u8.ToArray());
        switch (branch)
        {
            case "broadcast":
                await router.SendAsync(packet);
                break;
            case "attached":
                await router.RequestPathAsync(destination.Hash, bearer);
                break;
            case "known-path":
                var announce = Announce.Create(identity, destination);
                await router.HandleAsync(new ReceivedPacket(announce.ToArray(), announce, bearer));
                Assert.NotNull(router.Engine.GetPath(destination.Hash));
                await router.SendAsync(Packet.Create(PacketType.Data, DestinationType.Single, destination.Hash, packet.Payload));
                break;
            case "direct":
                await manager.SendRawToAsync(packet.ToArray(), bearer);
                break;
            default:
                throw new InvalidDataException("Unknown oracle branch.");
        }
        Assert.Equal(frames, bearer.Sent.Count);
        var statistics = Assert.Single(manager.GetStatistics());
        Assert.Equal(frames, statistics.SentPackets);
        Assert.Equal(0, statistics.FailedSends);
        Assert.Equal(outgoing, statistics.Outgoing);
    }

    [Fact]
    public async Task BackboneFeedbackUsesActualDataQueueAndSignalsRejectedHighWatermarkAttemptsOnly()
    {
        var bearer = new RecordingInterface();
        await using var manager = new PacketInterfaceManager([bearer], channelCapacity: 1);
        await manager.StartAsync();
        await using var router = new TransportRouter(manager, new TransportRouterOptions { InboundDataCapacity = 256 });
        Assert.NotNull(bearer.Queue);
        Assert.Equal(256, bearer.Queue.Capacity);
        var signals = new List<(int Depth, int Capacity)>();
        bearer.Queue.HighWatermarkReached += (depth, capacity) => signals.Add((depth, capacity));
        bearer.Queue.HighWatermarkReached += (_, _) => throw new InvalidOperationException("Optional feedback observer failure.");
        var packet = Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[] { 1 });
        var received = new ReceivedPacket(packet.ToArray(), packet, bearer);
        for (var i = 0; i < 230; i++) Assert.True(router.TryQueueInbound(received));
        Assert.Equal(230, bearer.Queue.Depth);
        Assert.Equal(0, manager.QueuedPackets);
        Assert.Empty(signals);

        var request = Packet.Create(PacketType.Data, DestinationType.Plain, TransportRouter.PathRequestHash, new byte[32]);
        Assert.True(router.TryQueueInbound(new ReceivedPacket(request.ToArray(), request, bearer)));
        Assert.Empty(signals);
        Assert.True(router.TryQueueInbound(received));
        Assert.Equal((230, 256), Assert.Single(signals));
        for (var i = 231; i < 256; i++) Assert.True(router.TryQueueInbound(received));
        Assert.False(router.TryQueueInbound(received));
        Assert.Equal((256, 256), signals[^1]);
        Assert.Equal(256, bearer.Queue.Depth);
        Assert.Equal(1, router.InboundQueues.Heights[(int)TransportTrafficClass.PathRequest]);
    }

    private sealed class RecordingInterface : IPacketInterface, IPacketInterfaceIngressFeedback
    {
        public PacketInterfaceIngressQueue? Queue { get; private set; }
        public void SetIngressQueue(PacketInterfaceIngressQueue queue) => Queue = queue;
        public List<byte[]> Sent { get; } = [];
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            Sent.Add(packet.ToArray());
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
