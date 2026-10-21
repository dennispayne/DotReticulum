using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public sealed class RuntimeDiscoveryHostTests
{
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
                var discoveryDestination = DiscoveryAnnouncer.RegisterDestination(producer.Router.TransportIdentity, null, producer.Router.RegisterLocalDestination);
                var announcement = new LocalDestination(discoveryDestination.Identity, discoveryDestination.Destination)
                {
                    AppData = DiscoveryCodec.Create(advertisement, cancellationToken: stop.Token),
                };
                producer.Router.RegisterLocalDestination(announcement);
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
