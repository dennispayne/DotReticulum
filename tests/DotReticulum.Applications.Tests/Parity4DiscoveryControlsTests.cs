using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public sealed class Parity4DiscoveryControlsTests
{
    [Fact]
    public async Task ControlledHdlcAutoconnectAppliesIfacAndDiscoveredRoutingOverrides()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "parity4-discovery-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            await using var peer = RnsdHost.Create(RnsdConfigParser.Parse("""
                [reticulum]
                  share_instance=no
                  enable_transport=yes
                [interfaces]
                  [[Controlled HDLC peer]]
                    type=TCPServerInterface
                    enabled=yes
                    mode=gateway
                    listen_ip=127.0.0.1
                    listen_port=0
                    network_name=discovered-network
                    pass_phrase=owned-test-network
                """), Path.Combine(root, "peer", "identity"), (_, message) => logs.Enqueue(message));
            await using var host = RnsdHost.Create(RnsdConfigParser.Parse("""
                [reticulum]
                  share_instance=no
                  enable_transport=yes
                  discover_interfaces=yes
                  required_discovery_value=1
                  autoconnect_discovered_interfaces=1
                  autoconnect_interface_mode=boundary
                  autoconnect_interface_gravity=7
                  autoconnect_announces_to_internal=yes
                  default_gravity=13
                  ic_max_held_announces=17
                """), Path.Combine(root, "host", "identity"), (_, message) => logs.Enqueue(message));
            await peer.StartAsync(stop.Token);
            await host.StartAsync(stop.Token);
            var run = Task.WhenAll(peer.RunAsync(stop.Token), host.RunAsync(stop.Token));
            try
            {
                var port = Assert.IsType<TcpServerPacketInterface>(Assert.Single(peer.Interfaces).Interface).LocalEndpoint!.Port;
                // This controlled advertisement exercises the compatible HDLC adapter, not a real Python Backbone.
                var payload = DiscoveryCodec.Create(new DiscoveryAdvertisement
                {
                    Name = "Controlled discovery peer", InterfaceType = "BackboneInterface",
                    TransportIdentity = peer.Router.TransportIdentity.Hash, Transport = true,
                    Implementation = "RNS", Version = "1.5.7", ReachableOn = "localhost", Port = port,
                    PublishIfac = true, NetworkName = "discovered-network", Passphrase = "owned-test-network", StampValue = 1,
                }, cancellationToken: stop.Token);
                Assert.True(await host.Discovery!.ReceiveAsync(peer.Router.TransportIdentity, payload, cancellationToken: stop.Token));
                await host.Discovery.TickAsync(stop.Token);
                var connected = Assert.Single(host.Interfaces).Interface;
                var options = host.Router.GetInterfaceAnnounceOptions(connected);
                Assert.Equal(InterfaceMode.Boundary, host.Router.GetInterfaceMode(connected));
                Assert.Equal(7, options.Gravity);
                Assert.True(options.AnnouncesToInternal);
                Assert.Equal(17, options.HeldAnnounceCapacity);
                Assert.NotNull(options.RateLimit);

                using var identity = Identity.Generate();
                var destination = Destination.Create(DestinationType.Single, "parity4", ["discovery"], identity);
                await peer.Router.AnnounceAsync(new LocalDestination(identity, destination), cancellationToken: stop.Token);
                try
                {
                    while (!host.Router.HasPath(destination.Hash)) await Task.Delay(20, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail($"Discovery announcement timed out.\nPeer: {System.Text.Json.JsonSerializer.Serialize(peer.GetStatistics())}\nHost: {System.Text.Json.JsonSerializer.Serialize(host.GetStatistics())}\n{string.Join('\n', logs)}");
                }
                Assert.Same(connected, Assert.Single(host.Router.Paths).Interface);
                Assert.False(run.IsCompleted, string.Join("\n", logs));
            }
            finally
            {
                await stop.CancelAsync();
                try { await run; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class MetadataBearer : IPacketInterface
    {
        public long Bitrate => 64_000;
        public int HardwareMtu => 1024;
        public bool SupportsMtuDiscovery => true;
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RuntimeStatisticsExposeMeasuredIfacAndConfiguredBearerCapabilitiesWithoutCredentials()
    {
        var iface = new MetadataBearer();
        await using var manager = new PacketInterfaceManager([iface]);
        manager.SetInterfaceAccessCode(iface, InterfaceAccessCode.Derive("statistics-network", "owned-test-key", size: 16));
        var stats = RuntimeStatistics.Collect(manager, [("metadata", iface)]);
        var entries = Assert.IsType<object?[]>(stats["interfaces"]);
        var entry = Assert.IsType<Dictionary<object, object?>>(Assert.Single(entries));
        Assert.Equal(64_000L, entry["bitrate"]);
        Assert.Equal(1024, entry["dotreticulum_hardware_mtu"]);
        Assert.Equal(true, entry["dotreticulum_mtu_discovery"]);
        Assert.Equal(16, entry["ifac_size"]);
        Assert.Equal("statistics-network", entry["ifac_netname"]);
        Assert.NotNull(entry["ifac_signature"]);
        Assert.DoesNotContain(entry.Keys, key => key.ToString()!.Contains("passphrase", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("networkname = first\npassphrase = first-key", "first", "first-key")]
    [InlineData("networkname = first\nnetwork_name = second\npassphrase = first-key\npass_phrase = second-key", "second", "second-key")]
    [InlineData("networkname = first\nnetwork_name = None\npassphrase = first-key\npass_phrase = ''", "first", "first-key")]
    public void AdvertisementsUseTheSameNormalizedIfacAliasesAsTheActualBearer(string settings, string name, string key)
    {
        var configuration = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
            [interfaces]
              [[Published]]
                type = TCPServerInterface
                enabled = yes
                mode = gateway
                listen_ip = 127.0.0.1
                listen_port = 4242
                discoverable = yes
                reachable_on = 127.0.0.1
                publish_ifac = yes
                {settings}
            """);
        using var identity = Identity.Generate();
        var advertisement = DiscoveryAdvertisementFactory.Create(Assert.Single(configuration.Interfaces), identity, true, 4242);
        Assert.Equal(name, advertisement.NetworkName);
        Assert.Equal(key, advertisement.Passphrase);
        Assert.True(advertisement.PublishIfac);
        Assert.Equal("TCPServerInterface", advertisement.InterfaceType);
    }
}
