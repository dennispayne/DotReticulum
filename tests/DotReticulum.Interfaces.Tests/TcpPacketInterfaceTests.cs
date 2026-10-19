using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;
using static DotReticulum.Interfaces.Tests.TestSupport;

namespace DotReticulum.Interfaces.Tests;

public class TcpPacketInterfaceTests
{
    private static async Task<TcpServerPacketInterface> StartServer(int maxClients = 64)
    {
        var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0), maxClients);
        await server.StartAsync(default);
        return server;
    }

    private static async Task<TcpClientPacketInterface> StartClient(TcpServerPacketInterface server)
    {
        var client = new TcpClientPacketInterface("127.0.0.1", server.LocalEndpoint!.Port);
        await client.StartAsync(default);
        return client;
    }

    [Fact]
    public async Task ClientAndServerExchangePacketsInBothDirections()
    {
        await using var server = await StartServer();
        await using var client = await StartClient(server);
        await using var serverIn = server.ReceiveAsync(default).GetAsyncEnumerator();
        await using var clientIn = client.ReceiveAsync(default).GetAsyncEnumerator();
        await WaitFor(() => server.ClientCount == 1);

        for (var i = 0; i < 20; i++)
        {
            await client.SendAsync(MakePacket(i, i * 20), default);
        }

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(MakePacket(i, i * 20), (await Next(serverIn)).ToArray());
        }

        await server.SendAsync(MakePacket(500), default);
        Assert.Equal(500, TagOf(await Next(clientIn)));
    }

    [Fact]
    public async Task PerClientServer_SpawnsOneInterfacePerConnection_WithNoCrossTalk()
    {
        await using var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0), perClientInterfaces: true);
        var children = new System.Collections.Concurrent.ConcurrentQueue<IPacketInterface>();
        server.ChildConnected += children.Enqueue;
        await server.StartAsync(default);
        await using var c1 = await StartClient(server);
        await using var c2 = await StartClient(server);
        await WaitFor(() => children.Count == 2);
        var child1 = children.ElementAt(0);
        var child2 = children.ElementAt(1);
        await using var in1 = child1.ReceiveAsync(default).GetAsyncEnumerator();
        await using var c1In = c1.ReceiveAsync(default).GetAsyncEnumerator();
        await using var c2In = c2.ReceiveAsync(default).GetAsyncEnumerator();

        await c1.SendAsync(MakePacket(7), default);
        Assert.Equal(7, TagOf(await Next(in1)));

        // A server-level send is a no-op in this mode; a child send reaches only its own client.
        await server.SendAsync(MakePacket(99), default);
        await child1.SendAsync(MakePacket(11), default);
        Assert.Equal(11, TagOf(await Next(c1In)));
        await child2.SendAsync(MakePacket(22), default);
        Assert.Equal(22, TagOf(await Next(c2In)));

        await child1.DisposeAsync();
        await WaitFor(() => server.ClientCount == 1);
    }

    [Fact]
    public async Task PerClientServer_WithoutSubscriberClosesConnections()
    {
        await using var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0), perClientInterfaces: true);
        await server.StartAsync(default);
        await using var client = await StartClient(server);
        await using var clientIn = client.ReceiveAsync(default).GetAsyncEnumerator();
        Assert.False(await clientIn.MoveNextAsync().AsTask().WaitAsync(Patience));
    }

    [Fact]
    public async Task ServerReceivesFromAndSendsToManyClients()
    {
        const int Clients = 12;
        await using var server = await StartServer();
        var clients = new List<TcpClientPacketInterface>();
        try
        {
            for (var i = 0; i < Clients; i++)
            {
                clients.Add(await StartClient(server));
            }

            await WaitFor(() => server.ClientCount == Clients);
            await using var serverIn = server.ReceiveAsync(default).GetAsyncEnumerator();
            var inbound = clients.Select(c => c.ReceiveAsync(default).GetAsyncEnumerator()).ToList();

            await Task.WhenAll(clients.Select((c, i) => c.SendAsync(MakePacket(i), default).AsTask()));
            var tags = (await Take(serverIn, Clients)).Select(p => TagOf(p)).Order().ToList();
            Assert.Equal(Enumerable.Range(0, Clients), tags);

            await server.SendAsync(MakePacket(4242), default);
            foreach (var e in inbound)
            {
                Assert.Equal(4242, TagOf(await Next(e)));
            }

            foreach (var e in inbound)
            {
                await e.DisposeAsync();
            }
        }
        finally
        {
            foreach (var c in clients)
            {
                await c.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ServerEnforcesMaxClients()
    {
        await using var server = await StartServer(maxClients: 2);
        await using var c1 = await StartClient(server);
        await using var c2 = await StartClient(server);
        await WaitFor(() => server.ClientCount == 2);

        await using var c3 = await StartClient(server);
        await using var e3 = c3.ReceiveAsync(default).GetAsyncEnumerator();
        Assert.False(await e3.MoveNextAsync().AsTask().WaitAsync(Patience), "rejected client should see its connection close");
        Assert.Equal(2, server.ClientCount);

        await c1.DisposeAsync();
        await WaitFor(() => server.ClientCount == 1);
        await using var c4 = await StartClient(server);
        await WaitFor(() => server.ClientCount == 2);
        await using var serverIn = server.ReceiveAsync(default).GetAsyncEnumerator();
        await c4.SendAsync(MakePacket(4), default);
        Assert.Equal(4, TagOf(await Next(serverIn)));
    }

    [Fact]
    public async Task ServerDropsGarbageAndRecoversOnRawSocket()
    {
        await using var server = await StartServer();
        await using var serverIn = server.ReceiveAsync(default).GetAsyncEnumerator();
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, server.LocalEndpoint!.Port);
        var stream = raw.GetStream();

        var garbage = new byte[] { 0x7E, 0x7D, 0x41, 0x01, 0x7E, 0x7E, 0x7E, 0x00, 0x11 };
        var good = HdlcPacketFraming.Encode(MakePacket(31));
        await stream.WriteAsync(garbage.Concat(good).ToArray());
        Assert.Equal(31, TagOf(await Next(serverIn)));

        // A frame arriving one byte at a time is reassembled.
        foreach (var b in HdlcPacketFraming.Encode(MakePacket(32)))
        {
            await stream.WriteAsync(new[] { b });
            await stream.FlushAsync();
        }

        Assert.Equal(32, TagOf(await Next(serverIn)));
    }

    [Fact]
    public async Task ClientStreamCompletesWhenServerDisposes()
    {
        var server = await StartServer();
        await using var client = await StartClient(server);
        await using var clientIn = client.ReceiveAsync(default).GetAsyncEnumerator();
        await WaitFor(() => server.ClientCount == 1);

        await server.DisposeAsync();
        Assert.False(await clientIn.MoveNextAsync().AsTask().WaitAsync(Patience));
    }

    [Fact]
    public async Task ServerReceiveEndsOnDisposeAndIsDisposeIdempotent()
    {
        var server = await StartServer();
        var receive = server.ReceiveAsync(default);
        Assert.Throws<InvalidOperationException>(() => server.ReceiveAsync(default));
        var task = Task.Run(async () =>
        {
            await foreach (var _ in receive)
            {
            }
        });

        await Task.Delay(50);
        await Task.WhenAll(server.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
        await task.WaitAsync(Patience);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await server.SendAsync(MakePacket(1), default));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await server.StartAsync(default));
    }

    [Fact]
    public async Task ClientConnectFailureThrowsAndInterfaceStaysDisposable()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var client = new TcpClientPacketInterface("127.0.0.1", port);
        await Assert.ThrowsAsync<SocketException>(async () => await client.StartAsync(default));
        Assert.Throws<InvalidOperationException>(() => client.ReceiveAsync(default));
    }

    [Fact]
    public async Task ClientAbruptResetFaultsOrCompletesCleanly()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var client = new TcpClientPacketInterface("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
            var accept = listener.AcceptTcpClientAsync();
            await client.StartAsync(default);
            using var peer = await accept;
            await using var e = client.ReceiveAsync(default).GetAsyncEnumerator();
            peer.Client.LingerState = new LingerOption(true, 0);
            peer.Close();

            try
            {
                Assert.False(await e.MoveNextAsync().AsTask().WaitAsync(Patience));
            }
            catch (IOException)
            {
                // A reset surfaces as an I/O fault, which is the documented behaviour.
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ClientHdlcFramingOnTheWireMatchesFrameEncoder()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var client = new TcpClientPacketInterface("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
            var accept = listener.AcceptTcpClientAsync();
            await client.StartAsync(default);
            using var peer = await accept;
            var packet = MakePacket(0x7E7D, 100);
            await client.SendAsync(packet, default);

            var expected = HdlcPacketFraming.Encode(packet);
            var actual = new byte[expected.Length];
            await peer.GetStream().ReadExactlyAsync(actual).AsTask().WaitAsync(Patience);
            Assert.Equal(expected, actual);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ManagerRunsTcpServerAndClientTogether()
    {
        var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0));
        await using var serverManager = new PacketInterfaceManager([server]);
        await serverManager.StartAsync();
        var client = new TcpClientPacketInterface("127.0.0.1", server.LocalEndpoint!.Port);
        await using var clientManager = new PacketInterfaceManager([client]);
        await clientManager.StartAsync();
        await WaitFor(() => server.ClientCount == 1);

        await using var e = serverManager.ReceiveWithSourceAsync().GetAsyncEnumerator();
        await clientManager.SendAsync(Packet.Parse(MakePacket(8)));
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Same(server, e.Current.Source);
        Assert.Equal(8, e.Current.Packet.Payload.Span[0]);
    }
}
