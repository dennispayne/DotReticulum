using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public sealed class RuntimeDiscoveryHostTests
{
    [Fact]
    public async Task ReloadedPublisherUsesReplacementMetadataBoundEndpointAndInterval()
    {
        var root = DiscoveryTestDirectory();
        var clock = new DiscoveryClock();
        var feed = new AdvertisementFeed();
        var registry = DiscoveryRegistry(feed);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var host = RnsdHost.Create(PublicationConfiguration(registry), Path.Combine(root, "transport_identity"),
                (_, _) => { }, registry, timeProvider: clock);
            await host.StartAsync(stop.Token);
            await host.TickDiscoveryAdvertisementsAsync(stop.Token);
            var original = ReadAdvertisement(feed, host);
            Assert.Equal("Original publisher", original.Name);
            Assert.Equal("localhost", original.ReachableOn);
            var originalServer = Assert.IsType<TcpServerPacketInterface>(host.Interfaces.Single(i => i.Name == "Published").Interface);
            Assert.Equal(originalServer.LocalEndpoint!.Port, original.Port);
            feed.Sent.Clear();

            var replacement = PublicationConfiguration(registry, replacement: true);
            Assert.True(await host.ReloadInterfaceAsync("Published", replacement, cancellationToken: stop.Token));
            clock.Advance(TimeSpan.FromSeconds(61));
            await host.TickDiscoveryAdvertisementsAsync(stop.Token);
            var updated = ReadAdvertisement(feed, host);
            var server = Assert.IsType<TcpServerPacketInterface>(host.Interfaces.Single(i => i.Name == "Published").Interface);
            Assert.NotSame(originalServer, server);
            Assert.Equal("Replacement publisher", updated.Name);
            Assert.Equal("127.0.0.1", updated.ReachableOn);
            Assert.Equal(53.2, updated.Latitude);
            Assert.Equal(-2.6, updated.Longitude);
            Assert.Equal(456d, updated.Height);
            Assert.Equal(server.LocalEndpoint!.Port, updated.Port);
            Assert.True(updated.Port > 0);
            feed.Sent.Clear();

            clock.Advance(TimeSpan.FromMinutes(6));
            await host.TickDiscoveryAdvertisementsAsync(stop.Token);
            Assert.Empty(feed.Sent);
            clock.Advance(TimeSpan.FromMinutes(5));
            await host.TickDiscoveryAdvertisementsAsync(stop.Token);
            Assert.Equal("Replacement publisher", ReadAdvertisement(feed, host).Name);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublisherRegistrationTracksDisableEnableDetachAndAttach(bool initiallyDiscoverable, bool disableInterface)
    {
        var root = DiscoveryTestDirectory();
        var clock = new DiscoveryClock();
        var feed = new AdvertisementFeed();
        var registry = DiscoveryRegistry(feed);
        var logs = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var host = RnsdHost.Create(PublicationConfiguration(registry, initiallyDiscoverable),
                Path.Combine(root, "transport_identity"), (_, message) => logs.Add(message), registry, timeProvider: clock);
            await host.StartAsync(stop.Token);
            await host.TickDiscoveryAdvertisementsAsync(stop.Token);
            if (initiallyDiscoverable) Assert.Equal("Original publisher", ReadAdvertisement(feed, host).Name);
            else Assert.Empty(feed.Sent);
            feed.Sent.Clear();

            if (initiallyDiscoverable)
                Assert.True(await host.ReloadInterfaceAsync("Published",
                    PublicationConfiguration(registry, discoverable: disableInterface, enabled: !disableInterface),
                    cancellationToken: stop.Token));
            else
                Assert.True(await host.DetachInterfaceAsync("Published", cancellationToken: stop.Token));
            clock.Advance(TimeSpan.FromHours(1));
            await host.TickDiscoveryAdvertisementsAsync(stop.Token);
            Assert.Empty(feed.Sent);

            var enabled = PublicationConfiguration(registry, replacement: true);
            if (initiallyDiscoverable)
                Assert.True(await host.ReloadInterfaceAsync("Published", enabled, cancellationToken: stop.Token));
            else
                Assert.True(await host.AttachInterfaceAsync("Published", enabled, cancellationToken: stop.Token));
            clock.Advance(TimeSpan.FromSeconds(61));
            await host.TickDiscoveryAdvertisementsAsync(stop.Token);
            Assert.Equal("Replacement publisher", ReadAdvertisement(feed, host).Name);
            feed.Sent.Clear();

            Assert.True(await host.DetachInterfaceAsync("Published", cancellationToken: stop.Token));
            clock.Advance(TimeSpan.FromHours(1));
            await host.TickDiscoveryAdvertisementsAsync(stop.Token);
            Assert.Empty(feed.Sent);
            Assert.DoesNotContain(logs, message => message.Contains("Discovery advertisement for", StringComparison.Ordinal));

            Assert.True(await host.AttachInterfaceAsync("Published", enabled, cancellationToken: stop.Token));
            clock.Advance(TimeSpan.FromSeconds(61));
            await host.TickDiscoveryAdvertisementsAsync(stop.Token);
            var attached = ReadAdvertisement(feed, host);
            var server = Assert.IsType<TcpServerPacketInterface>(host.Interfaces.Single(i => i.Name == "Published").Interface);
            Assert.Equal("Replacement publisher", attached.Name);
            Assert.Equal(server.LocalEndpoint!.Port, attached.Port);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static string DiscoveryTestDirectory() => Path.Combine(Directory.GetCurrentDirectory(), "TestResults",
        "discovery-reload-" + Guid.NewGuid().ToString("N"));

    private static InterfaceFactoryRegistry DiscoveryRegistry(AdvertisementFeed feed)
    {
        var registry = new InterfaceFactoryRegistry();
        registry.Register("AdvertisementFeed", [], _ => feed);
        return registry;
    }

    private static RnsdConfig PublicationConfiguration(InterfaceFactoryRegistry registry, bool discoverable = true,
        bool replacement = false, bool enabled = true) => RnsdConfigParser.Parse($"""
            [reticulum]
            share_instance=no
            enable_transport=yes
            [interfaces]
            [[Feed]]
            type=AdvertisementFeed
            enabled=yes
            [[Published]]
            type=TCPServerInterface
            enabled={(enabled ? "yes" : "no")}
            listen_ip=127.0.0.1
            listen_port=0
            discoverable={(discoverable ? "yes" : "no")}
            reachable_on={(replacement ? "127.0.0.1" : "localhost")}
            discovery_name={(replacement ? "Replacement publisher" : "Original publisher")}
            discovery_stamp_value=1
            announce_interval={(replacement ? 10 : 5)}
            latitude={(replacement ? "53.2" : "52.1")}
            longitude={(replacement ? "-2.6" : "-1.5")}
            height={(replacement ? 456 : 123)}
            """, registry);

    private static DiscoveryRecord ReadAdvertisement(AdvertisementFeed feed, RnsdHost host)
    {
        var packet = Packet.Parse(Assert.Single(feed.Sent));
        var validation = Announce.Validate(packet);
        using var identity = validation.Identity;
        Assert.True(validation.IsValid);
        Assert.NotNull(identity);
        Assert.Equal(host.Router.TransportIdentity.Hash, identity.Hash);
        Assert.Equal(Destination.Create(DestinationType.Single, "rnstransport", ["discovery", "interface"], identity).Hash,
            packet.DestinationHash.ToArray());
        Assert.NotNull(validation.Data!.AppData);
        return DiscoveryCodec.Decode(validation.Data.AppData, identity, packet.Hops, requiredValue: 1);
    }

    private sealed class DiscoveryClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class AdvertisementFeed : IPacketInterface
    {
        private readonly Channel<ReadOnlyMemory<byte>> _received = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public List<byte[]> Sent { get; } = [];
        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var packet in _received.Reader.ReadAllAsync(cancellationToken)) yield return packet;
        }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(packet.ToArray());
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            _received.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task SignedDiscoveryPreservesTcpPolicyAndBackboneMetadataAttachesNativeHdlcAdapter()
    {
        var root = ReticulumNodeTests.TempDir();
        var feedPort = ReticulumNodeTests.FreePort();
        var publicPort = ReticulumNodeTests.FreePort();
        var producerConfig = RnsdConfigParser.Parse($"""
            [reticulum]
            share_instance=no
            enable_transport=yes
            [interfaces]
            [[Feed]]
            type=TCPServerInterface
            enabled=yes
            listen_ip=127.0.0.1
            listen_port={feedPort}
            [[Published]]
            type=TCPServerInterface
            enabled=yes
            listen_ip=127.0.0.1
            listen_port={publicPort}
            discoverable=yes
            reachable_on=127.0.0.1
            discovery_stamp_value=1
            discovery_name=Published
            latitude=52.1
            longitude=-1.5
            height=123
            """);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            await using var producer = RnsdHost.Create(producerConfig, Path.Combine(root, "producer", "transport_identity"),
                (_, message) => logs.Enqueue(message));
            await producer.StartAsync(stop.Token);
            var consumerConfig = RnsdConfigParser.Parse($"""
                [reticulum]
                share_instance=no
                [interfaces]
                [[Bootstrap]]
                type=TCPClientInterface
                enabled=yes
                target_host=127.0.0.1
                target_port={feedPort}
                bootstrap_only=yes
                """) with
            {
                Discovery = new DiscoveryOptions
                {
                    Enabled = true, RequiredStampValue = 1, MaxAutoConnected = 1,
                    AllowUnverifiedImplementations = true,
                    AllowedSources = [producer.Router.TransportIdentity.Hash],
                },
            };
            await using var consumer = RnsdHost.Create(consumerConfig, Path.Combine(root, "consumer", "transport_identity"),
                (_, message) => logs.Enqueue(message));
            await consumer.StartAsync(stop.Token);
            var consume = consumer.RunAsync(stop.Token);
            var publish = producer.RunAsync(stop.Token);
            try
            {
                while ((await consumer.Discovery!.SnapshotAsync(cancellationToken: stop.Token)).Count == 0)
                {
                    if (consume.IsCompleted) await consume;
                    await Task.Delay(25, stop.Token);
                }
                var record = Assert.Single(await consumer.Discovery!.SnapshotAsync(cancellationToken: stop.Token));
                Assert.Equal("Published", record.Name);
                Assert.Equal(publicPort, record.Port);
                Assert.Equal(52.1, record.Latitude);
                Assert.Equal(producer.Router.TransportId, Convert.FromHexString(record.TransportId));
                Assert.Single(consumer.Interfaces);
                Assert.Equal("Bootstrap", consumer.Interfaces[0].Name);
                var advertisement = new DiscoveryAdvertisement
                {
                    InterfaceType = "BackboneInterface", Transport = true, TransportIdentity = producer.Router.TransportId,
                    Name = "Native HDLC adapter", ReachableOn = "localhost", Port = publicPort, StampValue = 1,
                };
                using var announcement = new LocalDestination(producer.Router.TransportIdentity,
                    Destination.Create(DestinationType.Single, "rnstransport", ["discovery", "interface"], producer.Router.TransportIdentity))
                {
                    AppData = DiscoveryCodec.Create(advertisement, cancellationToken: stop.Token),
                };
                await Task.Delay(1100, stop.Token);
                await producer.Router.AnnounceAsync(announcement, cancellationToken: stop.Token);
                while (!consumer.Interfaces.Any(i => i.Name == advertisement.Name)) await Task.Delay(25, stop.Token);
                while (consumer.Interfaces.Any(i => i.Name == "Bootstrap")) await Task.Delay(25, stop.Token);
                using var endpointIdentity = Identity.Generate();
                var endpoint = new LocalDestination(endpointIdentity, Destination.Create(DestinationType.Single, "runtime-tests", ["echo"], endpointIdentity))
                { AcceptLinks = true };
                endpoint.RegisterRequestHandler("/echo", request => request.Data, RequestPolicy.All);
                producer.Router.RegisterLocalDestination(endpoint);
                await producer.Router.AnnounceAsync(endpoint, cancellationToken: stop.Token);
                while (!consumer.Router.HasPath(endpoint.Destination.Hash)) await Task.Delay(25, stop.Token);
                var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
                var link = consumer.Router.Links.Connect(endpointIdentity, endpoint.Destination.Hash, established: result => established.TrySetResult(result));
                await established.Task.WaitAsync(stop.Token);
                var receipt = link.Request("/echo", new byte[] { 0x7e, 0x7d, 3 });
                Assert.NotNull(receipt);
                Assert.Equal(new byte[] { 0x7e, 0x7d, 3 }, Assert.IsType<byte[]>(await receipt.ResponseTask.WaitAsync(stop.Token)));
                link.Teardown();
                Assert.False(consume.IsCompleted);
                Assert.False(publish.IsCompleted);
            }
            catch (Exception error) { throw new Xunit.Sdk.XunitException($"{error}\n{string.Join('\n', logs)}"); }
            finally
            {
                await stop.CancelAsync();
                await Task.WhenAll(consume, publish);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DiscoveryExecutablePolicyFailsClosedDuringHostConstruction()
    {
        var root = ReticulumNodeTests.TempDir();
        var configuration = RnsdConfigParser.Parse("""
            [reticulum]
            share_instance=no
            [interfaces]
            [[Publisher]]
            type=TCPServerInterface
            enabled=yes
            listen_port=0
            discoverable=yes
            reachable_on=localhost
            location_cmd=untrusted.exe
            """);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                RnsdHost.CreateAsync(configuration, Path.Combine(root, "transport_identity"), (_, _) => { }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
