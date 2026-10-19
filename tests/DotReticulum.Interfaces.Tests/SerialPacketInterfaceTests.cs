using DotReticulum.Core;
using static DotReticulum.Interfaces.Tests.TestSupport;

namespace DotReticulum.Interfaces.Tests;

public class SerialPacketInterfaceTests
{
    [Fact]
    public async Task ExchangesPacketsOverDuplexStreamPair()
    {
        var (sa, sb) = CreateDuplexPair();
        await using var a = new SerialPacketInterface(sa);
        await using var b = new SerialPacketInterface(sb);
        await a.StartAsync(default);
        await b.StartAsync(default);
        await using var ea = a.ReceiveAsync(default).GetAsyncEnumerator();
        await using var eb = b.ReceiveAsync(default).GetAsyncEnumerator();

        for (var i = 0; i < 10; i++)
        {
            await a.SendAsync(MakePacket(i, i * 40), default);
        }

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(MakePacket(i, i * 40), (await Next(eb)).ToArray());
        }

        await b.SendAsync(MakePacket(99), default);
        Assert.Equal(99, TagOf(await Next(ea)));
    }

    [Fact]
    public async Task ConcurrentSendsAreNotInterleaved()
    {
        var (sa, sb) = CreateDuplexPair();
        await using var a = new SerialPacketInterface(sa);
        await using var b = new SerialPacketInterface(sb);
        await a.StartAsync(default);
        await b.StartAsync(default);
        await using var eb = b.ReceiveAsync(default).GetAsyncEnumerator();

        const int Count = 50;
        await Task.WhenAll(Enumerable.Range(0, Count).Select(i => Task.Run(() => a.SendAsync(MakePacket(i, 300), default).AsTask())));
        var tags = (await Take(eb, Count)).Select(p => TagOf(p)).Order().ToList();
        Assert.Equal(Enumerable.Range(0, Count), tags);
    }

    [Fact]
    public async Task RemoteDisposeCompletesReceiveStream()
    {
        var (sa, sb) = CreateDuplexPair();
        await using var a = new SerialPacketInterface(sa);
        var b = new SerialPacketInterface(sb);
        await a.StartAsync(default);
        await b.StartAsync(default);
        await using var ea = a.ReceiveAsync(default).GetAsyncEnumerator();

        await b.DisposeAsync();
        Assert.False(await ea.MoveNextAsync().AsTask().WaitAsync(Patience));
    }

    [Fact]
    public async Task LocalDisposeEndsActiveReceive()
    {
        var (sa, sb) = CreateDuplexPair();
        await using var _b = new SerialPacketInterface(sb);
        var a = new SerialPacketInterface(sa);
        await a.StartAsync(default);
        var task = Task.Run(async () =>
        {
            await foreach (var _ in a.ReceiveAsync(default))
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
    public async Task RejectsInvalidPacketsAndMisuse()
    {
        var (sa, sb) = CreateDuplexPair();
        await using var _b = new SerialPacketInterface(sb);
        await using var a = new SerialPacketInterface(sa);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await a.SendAsync(MakePacket(1), default));
        await a.StartAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await a.StartAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await a.SendAsync(new byte[5], default));
        _ = a.ReceiveAsync(default);
        Assert.Throws<InvalidOperationException>(() => a.ReceiveAsync(default));
    }

    [Fact]
    public async Task ManagerBridgesTwoSerialEnds()
    {
        var (sa, sb) = CreateDuplexPair();
        await using var ma = new PacketInterfaceManager([new SerialPacketInterface(sa)]);
        await using var mb = new PacketInterfaceManager([new SerialPacketInterface(sb)]);
        await ma.StartAsync();
        await mb.StartAsync();
        await using var e = mb.ReceiveAsync().GetAsyncEnumerator();

        await ma.SendAsync(Packet.Parse(MakePacket(3)));
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Equal(3, e.Current.Payload.Span[0]);
    }

    [Fact]
    public async Task OpeningAMissingSerialPortFails()
    {
        await using var port = new SerialPacketInterface("DOTRETICULUM_NO_SUCH_PORT");
        var ex = await Record.ExceptionAsync(async () => await port.StartAsync(default));
        Assert.NotNull(ex);
    }

    [Fact]
    public void ConstructorValidatesStream()
    {
        Assert.Throws<ArgumentNullException>(() => new SerialPacketInterface((Stream)null!));
        Assert.Throws<ArgumentException>(() => new SerialPacketInterface(new MemoryStream(Array.Empty<byte>(), writable: false)));
    }
}
