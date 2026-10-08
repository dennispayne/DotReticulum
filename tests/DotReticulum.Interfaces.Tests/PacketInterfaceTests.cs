using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DotReticulum.Core;
using DotReticulum.Interfaces;

namespace DotReticulum.Interfaces.Tests;

public sealed class PacketInterfaceTests
{
    private static readonly byte[] WirePacket =
        Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], [0x7d, 0x7e]).Raw.ToArray();

    [Fact]
    public async Task ManagerRequiresStartAndForwardsPacketsThroughBoundedQueue()
    {
        var fake = new TestPacketInterface();
        await using var manager = new PacketInterfaceManager([fake], queueCapacity: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.ReceiveAsync().GetAsyncEnumerator().MoveNextAsync());
        await manager.StartAsync();
        await fake.PublishAsync(WirePacket);

        await fake.WaitUntilConsumedAsync(1);
        await using var received = manager.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await received.MoveNextAsync());
        Assert.Equal(WirePacket, received.Current.ToArray());

        await manager.SendAsync(WirePacket);
        Assert.Equal(WirePacket, fake.SentPacket);
    }

    [Fact]
    public async Task ManagerBackpressuresWhenBoundedQueueIsFull()
    {
        var fake = new TestPacketInterface();
        await using var manager = new PacketInterfaceManager([fake], queueCapacity: 1);
        await manager.StartAsync();

        await fake.PublishAsync(WirePacket);
        await fake.PublishAsync(WirePacket);
        await fake.WaitUntilConsumedAsync(2);
        await fake.PublishAsync(WirePacket);
        await Task.Delay(100);
        Assert.Equal(2, fake.Consumed);

        await using var received = manager.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await received.MoveNextAsync());
        await fake.WaitUntilConsumedAsync(3);
    }

    [Fact]
    public async Task UdpExchangesCompletePacketsBetweenLoopbackPeers()
    {
        var receiverPort = ReserveUdpPort();
        var senderPort = ReserveUdpPort();
        await using var receiver = new UdpPacketInterface(
            new IPEndPoint(IPAddress.Loopback, receiverPort),
            new IPEndPoint(IPAddress.Loopback, senderPort));
        await using var sender = new UdpPacketInterface(
            new IPEndPoint(IPAddress.Loopback, senderPort),
            new IPEndPoint(IPAddress.Loopback, receiverPort));
        await receiver.StartAsync();
        await sender.StartAsync();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var packets = receiver.ReceiveAsync(cancellation.Token).GetAsyncEnumerator();
        await sender.SendAsync(WirePacket, cancellation.Token);

        Assert.True(await packets.MoveNextAsync());
        Assert.Equal(WirePacket, packets.Current.ToArray());
    }

    [Fact]
    public async Task TcpClientAndServerExchangeHdlcFramedPacketsBothWays()
    {
        await using var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0));
        await server.StartAsync();
        var endpoint = Assert.IsType<IPEndPoint>(server.LocalEndPoint);
        await using var client = new TcpClientPacketInterface(endpoint);
        await client.StartAsync();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var serverPackets = server.ReceiveAsync(cancellation.Token).GetAsyncEnumerator();
        await using var clientPackets = client.ReceiveAsync(cancellation.Token).GetAsyncEnumerator();

        await client.SendAsync(WirePacket, cancellation.Token);
        Assert.True(await serverPackets.MoveNextAsync());
        Assert.Equal(WirePacket, serverPackets.Current.ToArray());

        await server.SendAsync(WirePacket, cancellation.Token);
        Assert.True(await clientPackets.MoveNextAsync());
        Assert.Equal(WirePacket, clientPackets.Current.ToArray());
    }

    private static int ReserveUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return Assert.IsType<IPEndPoint>(socket.Client.LocalEndPoint).Port;
    }

    private sealed class TestPacketInterface : IPacketInterface
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private int _consumed;

        public int Consumed => Volatile.Read(ref _consumed);
        public byte[]? SentPacket { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var packet in _incoming.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Increment(ref _consumed);
                yield return packet;
            }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            SentPacket = packet.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync(byte[] packet) => _incoming.Writer.WriteAsync(packet);

        public async Task WaitUntilConsumedAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Consumed < count)
                await Task.Delay(10, timeout.Token);
        }

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
