using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;
using static DotReticulum.Interfaces.Tests.TestSupport;

namespace DotReticulum.Interfaces.Tests;

public class UdpPacketInterfaceTests
{
    private static async Task<(UdpPacketInterface A, UdpPacketInterface B)> StartPair()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        var a = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, portA), new IPEndPoint(IPAddress.Loopback, portB));
        var b = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, portB), new IPEndPoint(IPAddress.Loopback, portA));
        await a.StartAsync(default);
        await b.StartAsync(default);
        return (a, b);
    }

    [Fact]
    public async Task ExchangesOnePacketPerDatagramInBothDirections()
    {
        var (a, b) = await StartPair();
        await using var _a = a;
        await using var _b = b;
        await using var ea = a.ReceiveAsync(default).GetAsyncEnumerator();
        await using var eb = b.ReceiveAsync(default).GetAsyncEnumerator();

        for (var i = 0; i < 5; i++)
        {
            await a.SendAsync(MakePacket(i), default);
        }

        var received = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            received.Add(TagOf(await Next(eb)));
        }
        Assert.Equal(Enumerable.Range(0, 5), received.Order());

        await b.SendAsync(MakePacket(77, 400), default);
        var back = await Next(ea);
        Assert.Equal(77, TagOf(back));
        Assert.Equal(MakePacket(77, 400), back.ToArray());
    }

    [Fact]
    public async Task SendRejectsEmptyAndOversizePackets()
    {
        var (a, b) = await StartPair();
        await using var _a = a;
        await using var _b = b;
        await Assert.ThrowsAsync<ArgumentException>(async () => await a.SendAsync(ReadOnlyMemory<byte>.Empty, default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await a.SendAsync(new byte[a.HardwareMtu + 1], default));
        var oversizedIfac = new byte[a.HardwareMtu + 65];
        oversizedIfac[0] = 0x80;
        await Assert.ThrowsAsync<ArgumentException>(async () => await a.SendAsync(oversizedIfac, default));
    }

    [Fact]
    public async Task SurvivesSendingToAPeerWithNoListener()
    {
        var deadPort = FreeUdpPort();
        var localPort = FreeUdpPort();
        await using var a = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, localPort), new IPEndPoint(IPAddress.Loopback, deadPort));
        await a.StartAsync(default);
        await using var e = a.ReceiveAsync(default).GetAsyncEnumerator();
        var pending = e.MoveNextAsync().AsTask();

        await a.SendAsync(MakePacket(1), default);
        await Task.Delay(100);
        await a.SendAsync(MakePacket(2), default);
        await Task.Delay(100);

        using var other = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await other.SendAsync(MakePacket(9), new IPEndPoint(IPAddress.Loopback, localPort));
        Assert.True(await pending.WaitAsync(Patience));
        Assert.Equal(9, TagOf(e.Current));
    }

    [Fact]
    public async Task DisposeEndsReceiveAndLifecycleErrorsAreReported()
    {
        var (a, b) = await StartPair();
        await using var _b = b;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await a.StartAsync(default));
        var receive = a.ReceiveAsync(default);
        Assert.Throws<InvalidOperationException>(() => a.ReceiveAsync(default));
        var task = Task.Run(async () =>
        {
            await foreach (var _ in receive)
            {
            }
        });

        await Task.Delay(50);
        await a.DisposeAsync();
        await a.DisposeAsync();
        await task.WaitAsync(Patience);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await a.SendAsync(MakePacket(1), default));
    }

    [Fact]
    public async Task UsedBeforeStartThrows()
    {
        await using var a = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, 0), new IPEndPoint(IPAddress.Loopback, 1));
        Assert.Throws<InvalidOperationException>(() => a.ReceiveAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await a.SendAsync(MakePacket(1), default));
    }

    [Fact]
    public async Task ReceiveHonoursCancellation()
    {
        var (a, b) = await StartPair();
        await using var _a = a;
        await using var _b = b;
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in a.ReceiveAsync(cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task WorksThroughTheManagerAndDropsMalformedDatagrams()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        var a = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, portA), new IPEndPoint(IPAddress.Loopback, portB));
        await using var b = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, portB), new IPEndPoint(IPAddress.Loopback, portA));
        await using var manager = new PacketInterfaceManager([a]);
        await manager.StartAsync();
        await b.StartAsync(default);

        await b.SendAsync(MakePacket(5), default);
        using var raw = new UdpClient();
        await raw.SendAsync(new byte[] { 1, 2, 3 }, new IPEndPoint(IPAddress.Loopback, portA));
        await b.SendAsync(MakePacket(6), default);

        await using var e = manager.ReceiveAsync().GetAsyncEnumerator();
        var received = new List<byte>();
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(Patience));
        received.Add(e.Current.Payload.Span[0]);
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(Patience));
        received.Add(e.Current.Payload.Span[0]);
        Assert.Equal(new byte[] { 5, 6 }, received.Order());
    }
}