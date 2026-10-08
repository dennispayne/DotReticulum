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
        var mutablePacket = WirePacket.ToArray();
        await fake.PublishAsync(mutablePacket);

        await fake.WaitUntilConsumedAsync(1);
        Array.Fill(mutablePacket, (byte)0);
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

    [Fact]
    public async Task TcpClientMatchesPinnedUpstreamHdlcFramingVector()
    {
        // Extracted HDLC.escape from Reticulum e40191b3d193b46b7f2d8a44424a594cd758839b.
        // Reproduce with GenerateHdlcVector.py; upstream TCPInterface.py SHA256:
        // 0e397dbdd9ce47db533a7181a4b924ef351fb0dee8d8e43c0cc1c64be173668b.
        var expectedFrame = Convert.FromHexString(
            "7e000000000000000000000000000000000000007d5d7d5e7e");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var framingPacket = Packet.Create(
                PacketType.Data, DestinationType.Single, new byte[16], [0x7d, 0x7e]).Raw;
            var endpoint = Assert.IsType<IPEndPoint>(listener.LocalEndpoint);
            await using var client = new TcpClientPacketInterface(endpoint);
            await client.StartAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var peer = await listener.AcceptTcpClientAsync(cancellation.Token);

            await client.SendAsync(framingPacket, cancellation.Token);
            var actualFrame = new byte[expectedFrame.Length];
            await peer.GetStream().ReadExactlyAsync(actualFrame, cancellation.Token);

            Assert.Equal(expectedFrame, actualFrame);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SerialInterfaceFramesPacketsOverDuplexStream()
    {
        var stream = new TestDuplexStream();
        await using var serial = new SerialPacketInterface(stream);
        await serial.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var packets = serial.ReceiveAsync(cancellation.Token).GetAsyncEnumerator();

        var packet = Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], [0x7d, 0x7e]).Raw;
        var expectedFrame = Convert.FromHexString("7e000000000000000000000000000000000000007d5d7d5e7e");
        var receive = packets.MoveNextAsync().AsTask();
        stream.Feed(expectedFrame.AsMemory(0, 7));
        stream.Feed(expectedFrame.AsMemory(7));

        Assert.True(await receive);
        Assert.Equal(packet, packets.Current.ToArray());

        await serial.SendAsync(packet, cancellation.Token);
        Assert.Equal(expectedFrame, await stream.ReadWriteAsync(cancellation.Token));
    }

    [Fact]
    public async Task SerialInterfaceRequiresStartAndAllowsOnlyOneReceiver()
    {
        var stream = new TestDuplexStream();
        await using var serial = new SerialPacketInterface(stream);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await serial.SendAsync(WirePacket));
        await serial.StartAsync();

        using var cancellation = new CancellationTokenSource();
        await using var first = serial.ReceiveAsync(cancellation.Token).GetAsyncEnumerator();
        await using var second = serial.ReceiveAsync(cancellation.Token).GetAsyncEnumerator();
        var pendingReceive = first.MoveNextAsync().AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
        cancellation.Cancel();
        Assert.False(await pendingReceive);
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

            private sealed class TestDuplexStream : Stream
            {
                private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
                private readonly TaskCompletionSource<byte[]> _written = new(TaskCreationOptions.RunContinuationsAsynchronously);
                private byte[]? _current;
                private int _offset;

                public override bool CanRead => true;
                public override bool CanSeek => false;
                public override bool CanWrite => true;
                public override long Length => throw new NotSupportedException();
                public override long Position
                {
                    get => throw new NotSupportedException();
                    set => throw new NotSupportedException();
                }

                public void Feed(ReadOnlyMemory<byte> bytes) => _incoming.Writer.TryWrite(bytes.ToArray());

                public Task<byte[]> ReadWriteAsync(CancellationToken cancellationToken) =>
                    _written.Task.WaitAsync(cancellationToken);

                public override async ValueTask<int> ReadAsync(
                    Memory<byte> buffer,
                    CancellationToken cancellationToken = default)
                {
                    while (_current is null || _offset == _current.Length)
                    {
                        _current = await _incoming.Reader.ReadAsync(cancellationToken);
                        _offset = 0;
                    }

                    var count = Math.Min(buffer.Length, _current.Length - _offset);
                    _current.AsMemory(_offset, count).CopyTo(buffer);
                    _offset += count;
                    return count;
                }

                public override ValueTask WriteAsync(
                    ReadOnlyMemory<byte> buffer,
                    CancellationToken cancellationToken = default)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _written.TrySetResult(buffer.ToArray());
                    return ValueTask.CompletedTask;
                }

                public override void Flush()
                {
                }

                public override int Read(byte[] buffer, int offset, int count) =>
                    throw new NotSupportedException();

                public override void Write(byte[] buffer, int offset, int count) =>
                    throw new NotSupportedException();

                public override long Seek(long offset, SeekOrigin origin) =>
                    throw new NotSupportedException();

                public override void SetLength(long value) =>
                    throw new NotSupportedException();

                protected override void Dispose(bool disposing)
                {
                    if (disposing)
                        _incoming.Writer.TryComplete();
                    base.Dispose(disposing);
                }
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
