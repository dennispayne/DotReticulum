using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

/// <summary>Composed runtime contracts: real host, managers, routers, RPC, physical TCP and local shared clients.</summary>
public sealed class SharedHostIntegrationTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task TransportDisabledOwnerRoutesBetweenTwoSharedClientsAndAnUnrelatedStandalonePeer()
    {
        await using var mesh = await Mesh.StartAsync(includeExternal: true);
        try
        {
            Assert.Equal(SharedInstanceRole.Host, mesh.Owner.Host.SharedRole);
            Assert.False(mesh.Owner.Host.Router.TransportEnabled);
            Assert.Equal(SharedInstanceRole.Client, mesh.A.SharedRole);
            Assert.Equal(SharedInstanceRole.Client, mesh.B.SharedRole);
            Assert.Equal(SharedInstanceRole.Standalone, mesh.External!.SharedRole);
            Assert.Equal(0, mesh.ForbiddenFactoryCalls);
            Assert.Single(mesh.A.Interfaces);
            Assert.Single(mesh.B.Interfaces);
            Assert.IsType<LocalSharedPacketInterface>(mesh.A.Interfaces[0].Interface);
            Assert.IsType<LocalSharedPacketInterface>(mesh.B.Interfaces[0].Interface);
            Assert.Contains(mesh.Owner.Host.Interfaces, entry => entry.Interface is TcpServerPacketInterface);

            using var aIdentity = Identity.Generate();
            using var bIdentity = Identity.Generate();
            using var externalIdentity = Identity.Generate();
            var a = Register(mesh.A, aIdentity, "client-a");
            var b = Register(mesh.B, bIdentity, "client-b");
            var external = Register(mesh.External, externalIdentity, "external");
            await mesh.A.Router.AnnounceAsync(a);
            await mesh.B.Router.AnnounceAsync(b);
            await mesh.External.Router.AnnounceAsync(external);
            await WaitAsync(() => mesh.A.Router.HasPath(b.Destination.Hash)
                && mesh.A.Router.HasPath(external.Destination.Hash)
                && mesh.B.Router.HasPath(a.Destination.Hash)
                && mesh.B.Router.HasPath(external.Destination.Hash)
                && mesh.External.Router.HasPath(a.Destination.Hash)
                && mesh.External.Router.HasPath(b.Destination.Hash), "Bidirectional local/external announcements did not propagate.");

            var local = await ExchangeAsync(mesh.A, b, "local A to B");
            local.Teardown();
            var outgoing = await ExchangeAsync(mesh.A, external, "local A to external");
            outgoing.Teardown();
            var incoming = await ExchangeAsync(mesh.External, b, "external to local B");
            incoming.Teardown();

            var stats = await mesh.A.CallManagementAsync(new() { ["get"] = "interface_stats" });
            Assert.Equal(2, LocalClients(stats));
            var statistics = Assert.IsType<Dictionary<object, object?>>(stats);
            var localRows = Assert.IsType<object?[]>(statistics["interfaces"])
                .OfType<Dictionary<object, object?>>()
                .Where(row => Equals(row.GetValueOrDefault("type"), nameof(LocalSharedPacketInterface)))
                .ToArray();
            Assert.Equal(2, localRows.Length);
            Assert.All(localRows, row =>
            {
                Assert.IsType<long>(row["mode"]);
                Assert.IsType<long>(row["tx_write_pending_bytes"]);
                Assert.False(row.ContainsKey("txbuffered"));
            });
            Assert.False(mesh.A.Completion.IsCompleted);
            Assert.False(mesh.B.Completion.IsCompleted);
            Assert.False(mesh.External.Completion.IsCompleted);
            Assert.False(mesh.Owner.Completion.IsCompleted);
            Assert.Equal(0, mesh.ForbiddenFactoryCalls);
        }
        catch (Exception error) { throw new Xunit.Sdk.XunitException($"{error}\n--- composed runtime log ---\n{mesh.Log}"); }
    }

    [Fact]
    public async Task SharedRpcMutatesOwnerStateAndOwnerRestartPersistsRetentionsWhileClientsReconnect()
    {
        await using var mesh = await Mesh.StartAsync(includeExternal: false);
        try
        {
            using var aIdentity = Identity.Generate();
            using var bIdentity = Identity.Generate();
            var a = Register(mesh.A, aIdentity, "restart-a");
            var b = Register(mesh.B, bIdentity, "restart-b");
            await mesh.A.Router.AnnounceAsync(a);
            await mesh.B.Router.AnnounceAsync(b);
            await WaitAsync(() => mesh.Owner.Host.Router.HasPath(a.Destination.Hash)
                && mesh.Owner.Host.Router.HasPath(b.Destination.Hash)
                && mesh.A.Router.HasPath(b.Destination.Hash), "Initial shared announcements did not reach the owner.");

            async Task<bool> DestinationOperation(string operation) => Assert.IsType<bool>(
                await mesh.A.CallManagementAsync(new() { ["destination_data"] = operation, ["destination_hash"] = a.Destination.Hash }));
            Assert.True(await DestinationOperation("used"));
            Assert.True(await DestinationOperation("retain"));
            Assert.True(OwnerEntry(mesh.Owner.Host, a.Destination.Hash).IsRetained);
            Assert.False(await DestinationOperation("used"));
            Assert.Equal(-1d, OwnerEntry(mesh.Owner.Host, a.Destination.Hash).LastUsedUnix);
            Assert.True(await DestinationOperation("unretain"));
            Assert.False(OwnerEntry(mesh.Owner.Host, a.Destination.Hash).IsRetained);
            Assert.True(OwnerEntry(mesh.Owner.Host, a.Destination.Hash).LastUsedUnix > 0);
            Assert.True(Assert.IsType<bool>(await mesh.B.CallManagementAsync(new()
            {
                ["identity_data"] = "retain", ["identity_hash"] = aIdentity.Hash,
            })));
            Assert.True(OwnerEntry(mesh.Owner.Host, a.Destination.Hash).IsRetained);
            Assert.True(Assert.IsType<bool>(await mesh.B.CallManagementAsync(new()
            {
                ["destination_data"] = "retain", ["destination_hash"] = b.Destination.Hash,
            })));
            Assert.True(OwnerEntry(mesh.Owner.Host, b.Destination.Hash).IsRetained);

            var link = await ExchangeAsync(mesh.A, b, "before restart");
            var identityBytes = File.ReadAllBytes(mesh.OwnerIdentityPath);
            await mesh.Owner.Host.PersistAsync();
            var state = await new RuntimeStateStore(Path.GetDirectoryName(mesh.OwnerIdentityPath)!).LoadAsync();
            Assert.NotNull(state);
            Assert.Contains(state!.KnownDestinations, item => item.DestinationHash.AsSpan().SequenceEqual(a.Destination.Hash) && item.Entry.IsRetained);

            await mesh.RestartOwnerAsync();
            Assert.Equal(identityBytes, File.ReadAllBytes(mesh.OwnerIdentityPath));
            Assert.True(OwnerEntry(mesh.Owner.Host, a.Destination.Hash).IsRetained);
            Assert.True(OwnerEntry(mesh.Owner.Host, b.Destination.Hash).IsRetained);
            await WaitAsync(() => link.Status == LinkStatus.Closed, "A link through the vanished owner was not torn down.");
            Assert.False(mesh.A.Completion.IsCompleted);
            Assert.False(mesh.B.Completion.IsCompleted);
            await WaitAsync(() => LocalClients(mesh.Owner.Host.GetStatistics()) == 2, "Existing clients did not reconnect to the restarted owner.");

            // No explicit announce here: shared reconnection must reannounce registered SINGLE destinations.
            await WaitAsync(() => mesh.Owner.Host.Router.HasPath(a.Destination.Hash)
                && mesh.Owner.Host.Router.HasPath(b.Destination.Hash), "Reconnected clients did not automatically reannounce their destinations.");
            var after = await ExchangeAsync(mesh.B, a, "after restart");
            after.Teardown();
            Assert.Equal(2, LocalClients(await mesh.A.CallManagementAsync(new() { ["get"] = "interface_stats" })));
            Assert.Equal(0, mesh.ForbiddenFactoryCalls);
            Assert.False(mesh.Owner.Completion.IsCompleted);
        }
        catch (Exception error) { throw new Xunit.Sdk.XunitException($"{error}\n--- composed runtime log ---\n{mesh.Log}"); }
    }

    private static KnownDestination OwnerEntry(RnsdHost owner, byte[] hash) =>
        Assert.Single(owner.Router.Engine.Known.ExportSnapshot(), item => item.DestinationHash.AsSpan().SequenceEqual(hash)).Entry;

    private static int LocalClients(object? statistics)
    {
        var map = Assert.IsType<Dictionary<object, object?>>(statistics);
        var interfaces = Assert.IsType<object?[]>(map["interfaces"]);
        return interfaces.OfType<Dictionary<object, object?>>()
            .Count(row => row.GetValueOrDefault("type") as string == nameof(LocalSharedPacketInterface));
    }

    private static LocalDestination Register(ReticulumNode node, Identity identity, string aspect)
    {
        var destination = new LocalDestination(identity,
            Destination.Create(DestinationType.Single, "shared-integration", [aspect], identity)) { AcceptLinks = true };
        destination.RegisterRequestHandler("/echo", context =>
            context.Data is byte[] data ? data.Reverse().ToArray() : null, RequestPolicy.All);
        node.Router.RegisterLocalDestination(destination);
        return destination;
    }

    private static async Task<Link> ExchangeAsync(ReticulumNode source, LocalDestination destination, string text)
    {
        using var timeout = new CancellationTokenSource(Patience);
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atDestination = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        destination.LinkEstablished = link =>
        {
            link.PacketReceived = (data, _) => atDestination.TrySetResult(data);
            accepted.TrySetResult(link);
        };
        var result = await ClientLinks.ConnectAsync(source, destination.Destination.Hash, Patience, timeout.Token);
        Assert.True(result.Ok, result.Error);
        var initiator = result.Link!;
        try
        {
            var responder = await accepted.Task.WaitAsync(timeout.Token);
            var atSource = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            initiator.PacketReceived = (data, _) => atSource.TrySetResult(data);
            var request = Encoding.UTF8.GetBytes(text + " \u007e\u007d");
            Assert.NotNull(initiator.Send(request));
            Assert.Equal(request, await atDestination.Task.WaitAsync(timeout.Token));
            var reverse = request.Reverse().ToArray();
            Assert.NotNull(responder.Send(reverse));
            Assert.Equal(reverse, await atSource.Task.WaitAsync(timeout.Token));
            var receipt = initiator.Request("/echo", request, timeout: 10);
            Assert.NotNull(receipt);
            Assert.Equal(reverse, Assert.IsType<byte[]>(await receipt!.ResponseTask.WaitAsync(timeout.Token)));
            return initiator;
        }
        catch { initiator.Teardown(); throw; }
    }

    private static async Task WaitAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(failure);
            await Task.Delay(25);
        }
    }

    private sealed class RunningHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private bool disposed;
        public RnsdHost Host { get; }
        public Task Completion { get; private set; } = Task.CompletedTask;
        public RunningHost(RnsdHost host) => Host = host;
        public async Task StartAsync()
        {
            await Host.StartAsync(stop.Token);
            Completion = Host.RunAsync(stop.Token);
        }
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await stop.CancelAsync();
            try { await Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                try { await Host.DisposeAsync(); }
                finally { stop.Dispose(); }
            }
        }
    }

    private sealed class Mesh : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(AppContext.BaseDirectory, "shared-host-integration-" + Guid.NewGuid().ToString("N"));
        private readonly ConcurrentQueue<string> log = new();
        private readonly List<ReticulumNode> clients = [];
        private readonly InterfaceFactoryRegistry registry = new();
        private RnsdConfig ownerConfiguration = null!;
        private int forbiddenFactoryCalls;
        public RunningHost Owner { get; private set; } = null!;
        public ReticulumNode A => clients[0];
        public ReticulumNode B => clients[1];
        public ReticulumNode? External { get; private set; }
        public int ForbiddenFactoryCalls => Volatile.Read(ref forbiddenFactoryCalls);
        public string OwnerIdentityPath => Path.Combine(directory, "owner", "storage", "transport_identity");
        public string Log => string.Join(Environment.NewLine, log);

        public static async Task<Mesh> StartAsync(bool includeExternal)
        {
            var mesh = new Mesh();
            try { await mesh.StartCoreAsync(includeExternal); return mesh; }
            catch { await mesh.DisposeAsync(); throw; }
        }
        private async Task StartCoreAsync(bool includeExternal)
        {
            var probes = Enumerable.Range(0, 3).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
            int[] ports;
            try
            {
                foreach (var probe in probes) probe.Start();
                ports = probes.Select(probe => ((IPEndPoint)probe.LocalEndpoint).Port).ToArray();
            }
            finally { foreach (var probe in probes) probe.Stop(); }
            var sharedSettings = $"""
                [reticulum]
                enable_transport = no
                static_transport_identity = yes
                share_instance = yes
                shared_instance_type = tcp
                shared_instance_port = {ports[0]}
                instance_control_port = {ports[1]}
                rpc_key = 000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f
                """;
            var physicalSettings = $"""
                [interfaces]
                [[Physical]]
                type = TCPServerInterface
                enabled = yes
                listen_ip = 127.0.0.1
                listen_port = {ports[2]}
                """;
            ownerConfiguration = FastReconnect(RnsdConfigParser.Parse(sharedSettings + "\n" + physicalSettings));
            Owner = await NewOwnerAsync();
            registry.Register("MustNotConstruct", [], _ =>
            {
                Interlocked.Increment(ref forbiddenFactoryCalls);
                throw new InvalidOperationException("A shared client constructed a physical interface factory.");
            });
            var clientConfiguration = FastReconnect(RnsdConfigParser.Parse(sharedSettings + "\n" + physicalSettings + """

                [[ForbiddenFactory]]
                type = MustNotConstruct
                enabled = yes
                """, registry));
            foreach (var name in new[] { "a", "b" })
            {
                var node = ReticulumNode.Create(clientConfiguration, Path.Combine(directory, name), Logger(name), registry);
                clients.Add(node);
                await node.StartAsync();
            }
            if (includeExternal)
            {
                External = ReticulumNode.Create(RnsdConfigParser.Parse($"""
                    [reticulum]
                    share_instance = no
                    enable_transport = no
                    [interfaces]
                    [[Owner]]
                    type = TCPClientInterface
                    enabled = yes
                    target_host = 127.0.0.1
                    target_port = {ports[2]}
                    """), Path.Combine(directory, "external"), Logger("external"));
                await External.StartAsync();
            }
            await WaitAsync(() => LocalClients(Owner.Host.GetStatistics()) == 2, "Owner did not adopt both local clients.");
        }
        private static RnsdConfig FastReconnect(RnsdConfig config) => config with
        {
            SharedInstance = config.SharedInstance with
            {
                ReconnectDelay = TimeSpan.FromMilliseconds(50),
                OperationTimeout = TimeSpan.FromSeconds(5),
            },
        };
        private Action<int, string> Logger(string name) => (level, message) => log.Enqueue($"{name}[{level}]: {message}");
        private async Task<RunningHost> NewOwnerAsync()
        {
            var host = await RnsdHost.CreateAsync(ownerConfiguration, OwnerIdentityPath, Logger("owner"));
            var running = new RunningHost(host);
            try { await running.StartAsync(); return running; }
            catch { await running.DisposeAsync(); throw; }
        }
        public async Task RestartOwnerAsync()
        {
            await Owner.DisposeAsync();
            Owner = await NewOwnerAsync();
        }
        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            async Task Release(Func<Task> action)
            {
                try { await action(); } catch (Exception error) { failures.Add(error); }
            }
            if (External is not null) await Release(() => External.DisposeAsync().AsTask());
            foreach (var client in clients.AsEnumerable().Reverse()) await Release(() => client.DisposeAsync().AsTask());
            if (Owner is not null) await Release(() => Owner.DisposeAsync().AsTask());
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            if (failures.Count > 0) throw new AggregateException("Shared integration fixture shutdown failed.", failures);
        }
    }
}
