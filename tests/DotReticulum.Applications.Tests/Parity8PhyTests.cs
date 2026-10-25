using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Threading.Channels;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public sealed class Parity8PhyTests
{
    private sealed class Bearer : IPacketInterfacePhysicalLayer
    {
        private readonly System.Threading.Channels.Channel<ReceivedFrame> _frames =
            System.Threading.Channels.Channel.CreateBounded<ReceivedFrame>(8);
        public Bearer? Peer { get; set; }
        public PacketPhysicalLayer Reading { get; init; }
        public ValueTask Inject(Packet packet, PacketPhysicalLayer phy) => _frames.Writer.WriteAsync(new(packet.ToArray(), phy));
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ReceivedFrame> ReceiveFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken)) yield return frame;
        }
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Use the atomic receive boundary.");
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
            Peer is { } peer ? peer._frames.Writer.WriteAsync(new(packet.ToArray(), peer.Reading), cancellationToken) : ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { _frames.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class Running(RnsdHost host) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _run = Task.CompletedTask;
        public RnsdHost Host => host;
        public async Task Start() { await host.StartAsync(_stop.Token); _run = host.RunAsync(_stop.Token); }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try { await _run; } catch (OperationCanceledException) { }
            await host.DisposeAsync();
            _stop.Dispose();
        }
    }

    private static string DirectoryForTest() => Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "p8-phy-" + Guid.NewGuid().ToString("N"));
    private static Dictionary<object, object?> Query(string path, byte[] hash) => new() { ["get"] = path, ["packet_hash"] = hash };
    private static async Task Until(Func<bool> predicate)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, stop.Token);
    }

    [Fact]
    public async Task OwnerQueriesUseFullHashValidationExistingAuthenticationAndIndependentPacketSnapshots()
    {
        var root = DirectoryForTest();
        try
        {
            var bearer = new Bearer();
            var registry = new InterfaceFactoryRegistry();
            registry.Register("PhyTest", [], _ => bearer);
            var config = RnsdConfigParser.Parse("""
                [reticulum]
                share_instance = no
                [interfaces]
                [[Reporting]]
                type = PhyTest
                enabled = yes
                """, registry);
            await using var running = new Running(RnsdHost.Create(config, Path.Combine(root, "transport_identity"), (_, _) => { }, registry));
            await running.Start();
            var host = running.Host;
            var first = Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[] { 1 });
            var second = Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], new byte[] { 2 });
            await bearer.Inject(first, new(-91, 4.25, 88.3));
            await bearer.Inject(second, new(-113, -7.5, 10));
            object? observed = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (observed is null)
            {
                observed = await host.CallManagementAsync(Query("packet_rssi", second.CalculateHash()), timeout.Token);
                if (observed is null) await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(-113d, observed);
            Assert.Equal(-91d, await host.CallManagementAsync(Query("packet_rssi", first.CalculateHash())));
            Assert.Equal(4.25d, await host.CallManagementAsync(Query("packet_snr", first.CalculateHash())));
            Assert.Equal(88.3d, await host.CallManagementAsync(Query("packet_q", first.CalculateHash())));
            Assert.Null(await host.CallManagementAsync(Query("packet_q", new byte[32])));
            foreach (var path in new[] { "packet_rssi", "packet_snr", "packet_q" })
                await Assert.ThrowsAsync<InvalidDataException>(() => host.CallManagementAsync(Query(path, new byte[16])).AsTask());
            var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            await using var server = new ManagementRpcServer(new IPEndPoint(IPAddress.Loopback, 0), key,
                (request, token) => host.CallManagementAsync(new(request.Arguments), token));
            await server.StartAsync();
            using var wrong = new ManagementRpcClient(server.LocalEndpoint!, new byte[32]);
            await Assert.ThrowsAsync<AuthenticationException>(() => wrong.CallAsync(Query("packet_rssi", first.CalculateHash())).AsTask());
            using var client = new ManagementRpcClient(server.LocalEndpoint!, key);
            Assert.Equal(-91d, await client.CallAsync(Query("packet_rssi", first.CalculateHash())));
            Assert.Equal(new(-113, -7.5, 10), await host.GetPacketPhysicalLayerAsync(second));
            Assert.Equal(default, first.PhysicalLayer); // No mutable process-global singleton attached to a codec object.
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SharedClientLinkOptInResolvesOwnerRadioCacheWithoutInventingLocalTcpReadings()
    {
        var root = DirectoryForTest();
        try
        {
            var probes = new[] { new TcpListener(IPAddress.Loopback, 0), new TcpListener(IPAddress.Loopback, 0) };
            int dataPort, rpcPort;
            try
            {
                foreach (var probe in probes) probe.Start();
                dataPort = ((IPEndPoint)probes[0].LocalEndpoint).Port;
                rpcPort = ((IPEndPoint)probes[1].LocalEndpoint).Port;
            }
            finally { foreach (var probe in probes) probe.Stop(); }
            var settings = $"""
                [reticulum]
                share_instance = yes
                shared_instance_type = tcp
                shared_instance_port = {dataPort}
                instance_control_port = {rpcPort}
                rpc_key = 000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f
                [interfaces]
                [[Reporting]]
                type = PhyTest
                enabled = yes
                """;
            var ownerBearer = new Bearer { Reading = new(-113, -7.5, 10) };
            var externalBearer = new Bearer { Peer = ownerBearer };
            ownerBearer.Peer = externalBearer;
            var registry = new InterfaceFactoryRegistry();
            registry.Register("PhyTest", [], _ => ownerBearer);
            var config = RnsdConfigParser.Parse(settings, registry);
            await using var owner = new Running(RnsdHost.Create(config, Path.Combine(root, "owner", "transport_identity"), (_, _) => { }, registry));
            await owner.Start();
            await using var client = new Running(RnsdHost.Create(config, Path.Combine(root, "client", "transport_identity"), (_, _) => { }, registry));
            await client.Start();
            Assert.True(client.Host.IsConnectedToSharedInstance);
            await using var externalManager = new PacketInterfaceManager([externalBearer]);
            await using var externalRouter = new TransportRouter(externalManager);
            await externalManager.StartAsync();
            using var stop = new CancellationTokenSource();
            var externalRun = externalRouter.RunAsync(stop.Token);
            try
            {
                using var identity = Identity.Generate();
                using var destination = new LocalDestination(identity,
                    Destination.Create(DestinationType.Single, "phy", ["shared"], identity))
                { AcceptLinks = true, ProofStrategy = ProofStrategy.All };
                var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
                destination.LinkEstablished = link => { link.TrackPhysicalLayer(true); accepted.TrySetResult(link); };
                client.Host.Router.RegisterLocalDestination(destination);
                await client.Host.Router.AnnounceAsync(destination);
                await Until(() => externalRouter.HasPath(destination.Destination.Hash));
                var local = externalRouter.Links.Connect(identity, destination.Destination.Hash);
                var remote = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Until(() => local.Status == LinkStatus.Active);
                var received = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
                remote.PacketReceived = (_, packet) => received.TrySetResult(packet);
                var receipt = local.Send("shared radio metadata"u8.ToArray())!;
                Assert.True(await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
                var packet = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(new(-113, -7.5, 10), remote.PhysicalLayer);
                Assert.Equal(new(-113, -7.5, 10), await client.Host.GetPacketPhysicalLayerAsync(packet));
                Assert.Equal(-113d, await client.Host.CallManagementAsync(Query("packet_rssi", packet.CalculateHash())));
                Assert.Equal(default, receipt.ProofPacket!.PhysicalLayer);
            }
            finally
            {
                await stop.CancelAsync();
                try { await externalRun; } catch (OperationCanceledException) { }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
