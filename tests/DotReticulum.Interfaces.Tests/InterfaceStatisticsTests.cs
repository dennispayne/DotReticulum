using System.Net;
using DotReticulum.Core;

namespace DotReticulum.Interfaces.Tests;

public sealed class InterfaceStatisticsTests
{
    [Fact]
    public async Task TcpCountsDecodedReceiveAndFramedTransmitForPeerAndParentAfterDisconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0), perClientInterfaces: true);
        var accepted = new TaskCompletionSource<IPacketInterface>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ChildConnected += child => accepted.TrySetResult(child);
        await server.StartAsync(timeout.Token);
        await using var client = new TcpClientPacketInterface("127.0.0.1", server.LocalEndpoint!.Port);
        await client.StartAsync(timeout.Token);
        await using var child = await accepted.Task.WaitAsync(timeout.Token);
        var raw = Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[] { 0x7e, 0x7d, 1 }).ToArray();
        await client.SendAsync(raw, timeout.Token);
        await using var received = child.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await received.MoveNextAsync());
        Assert.Equal(raw, received.Current.ToArray());
        Assert.Equal((long)raw.Length, ((IPacketInterfaceDiagnostics)child).GetDiagnostics()["rxb"]);
        Assert.Equal((long)HdlcPacketFraming.Encode(raw).Length, client.GetDiagnostics()["txb"]);
        await child.SendAsync(raw, timeout.Token);
        await using var returning = client.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await returning.MoveNextAsync());
        Assert.Equal((long)HdlcPacketFraming.Encode(raw).Length, server.GetDiagnostics()["txb"]);
        await child.DisposeAsync();
        Assert.Equal((long)raw.Length, server.GetDiagnostics()["rxb"]);
        Assert.Equal((long)HdlcPacketFraming.Encode(raw).Length, server.GetDiagnostics()["txb"]);
        Assert.Equal(0, server.ClientCount);
    }
}
