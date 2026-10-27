using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Tests.Interop;
using DotReticulum.Transport;
using Xunit.Abstractions;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Integration")]
public sealed class Parity5TransferLiveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task Until(Func<bool> predicate, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!predicate()) await Task.Delay(20, cancellation.Token);
    }

    [Trait("Category", "Live")]
    [LiveFact]
    public Task MixedPythonTransport_ChannelLossStreamsResourcesExhaustionAndFreshLinkRecovery() => RunTransfer(16384);

    [Trait("Category", "Live")]
    [LiveFact]
    public Task MixedPythonTransport_NarrowMtuBottleneckKeepsStreamedTransfersAndRecoveryCompatible() => RunTransfer(1024);

    private async Task RunTransfer(int mtu)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "parity5-mixed-" + Guid.NewGuid().ToString("N"));
        var port = Port();
        var pythonPort = Port();
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var config = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
              enable_transport = yes
            [interfaces]
              [[Server]]
                type = TCPServerInterface
                enabled = yes
                listen_ip = 127.0.0.1
                listen_port = {port}
                fixed_mtu = {mtu}
            """);
        await using var host = RnsdHost.Create(config, Path.Combine(directory, "transport_identity"),
            (level, message) => logs.Enqueue($"[{level}] {message}"));
        using var stop = new CancellationTokenSource();
        await using var bridge = new Parity4TransportLiveTests.ImpairedBridge(port, bitrate: 10_000_000, latencyMilliseconds: 20);
        PyLinkNode? transport = null, leaf = null;
        Task running = Task.CompletedTask;
        try
        {
            await host.StartAsync(stop.Token);
            running = host.RunAsync(stop.Token);
            bridge.Start();
            transport = await PyLinkNode.StartAsync("transport", tcpServerPort: pythonPort, rnsdPort: bridge.Port, harness: "live_mixed_transport_node.py");
            leaf = await PyLinkNode.StartAsync("leaf", rnsdPort: pythonPort);
            Assert.True(transport.Events("ready").Single().GetProperty("transport").GetBoolean());
            await leaf.Command("announce", "announced", Patience);
            await Until(() => host.Router.HasPath(leaf.Hash), Patience);
            Assert.Equal(2, host.Router.HopsTo(leaf.Hash));

            async Task<Link> Connect()
            {
                var upBefore = leaf.Count("link_up");
                var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
                var result = host.Router.Links.Connect(host.Router.Engine.Known.Recall(leaf.Hash)!, leaf.Hash,
                    link => established.TrySetResult(link));
                await established.Task.WaitAsync(Patience);
                await leaf.NextAsync("link_up", upBefore, Patience);
                Assert.Equal(mtu, result.Mtu);
                return result;
            }

            var link = await Connect();
            var channel = link.GetChannel();
            channel.RegisterMessageType<TransferText>();
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.AddMessageHandler(message => { received.TrySetResult(((TransferText)message).Text); return true; });
            var text = new string('m', Math.Min(3001, link.Mdu - 6));
            Assert.True(text.Length > Packet.Mtu);
            var before = leaf.Count("channel_msg");
            var dropped = bridge.Dropped;
            bridge.DropNextChannel(fromPython: false);
            channel.Send(new TransferText { Text = text });
            Assert.Equal(text, (await leaf.NextAsync("channel_msg", before, Patience)).GetProperty("text").GetString());
            Assert.Equal(dropped + 1, bridge.Dropped);
            bridge.DropNextChannel(fromPython: true);
            await leaf.SendLineAsync("chsend " + text);
            Assert.Equal(text, await received.Task.WaitAsync(Patience));
            Assert.Equal(dropped + 2, bridge.Dropped);
            Assert.Single(leaf.Events("channel_msg").Skip(before));

            var payload = new byte[120_017];
            RandomNumberGenerator.Fill(payload);
            var bufferBefore = leaf.Count("buffer_done");
            var writer = ChannelBuffer.CreateWriter(1, channel);
            await writer.WriteAsync(payload, stop.Token);
            await writer.CloseAsync();
            var bufferDone = await leaf.NextAsync("buffer_done", bufferBefore, Patience);
            Assert.Equal(payload.Length, bufferDone.GetProperty("size").GetInt32());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), bufferDone.GetProperty("sha256").GetString());

            var resourcePayload = new byte[2_200_017];
            RandomNumberGenerator.Fill(resourcePayload);
            var resourceBefore = leaf.Count("resource_done");
            var sent = new TaskCompletionSource<ResourceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inputPath = Path.Combine(directory, "stream-input.bin");
            await File.WriteAllBytesAsync(inputPath, resourcePayload);
            link.SendResourceFile(inputPath, autoCompress: false, concluded: resource => sent.TrySetResult(resource.Status));
            Assert.Equal(ResourceStatus.Complete, await sent.Task.WaitAsync(Patience));
            var resourceDone = await leaf.NextAsync("resource_done", resourceBefore, Patience);
            Assert.Equal(resourcePayload.Length, resourceDone.GetProperty("size").GetInt32());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(resourcePayload)), resourceDone.GetProperty("sha256").GetString());

            var reverseResource = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            link.ResourceStrategy = ResourceStrategy.AcceptAll;
            link.ResourcePolicy = new ResourcePolicy { MemoryThreshold = 1024, StorageDirectory = Path.Combine(directory, "resources") };
            link.ResourceReceived = resource =>
            {
                if (resource.Status == ResourceStatus.Complete) reverseResource.TrySetResult(resource);
            };
            await leaf.SendLineAsync("resource 2200019 mixed-reverse random");
            await using var reverse = await reverseResource.Task.WaitAsync(Patience);
            var reverseSent = await leaf.NextAsync("resource_sent", 0, Patience);
            Assert.True(reverse.IsFileBacked);
            Assert.False(reverse.HasDataInMemory);
            using (var input = reverse.OpenRead())
            {
                Assert.Equal(2_200_019, input.Length);
                Assert.Equal(Convert.ToHexStringLower(await SHA256.HashDataAsync(input)), reverseSent.GetProperty("sha256").GetString());
            }
            await reverse.DisposeAsync();
            Assert.Equal(Path.Combine(link.ResourcePolicy.StorageDirectory, ResourceStorageScavenger.OwnershipMarkerFileName),
                Assert.Single(Directory.GetFiles(link.ResourcePolicy.StorageDirectory)));

            await Until(() => channel.IsReadyToSend, Patience);
            bridge.DropAllChannels(true);
            channel.Send(new TransferText { Text = "retry exhaustion" });
            await Until(() => link.Status == LinkStatus.Closed, TimeSpan.FromMinutes(3));
            Assert.True(bridge.Dropped > dropped + 2);
            bridge.DropAllChannels(false);
            var recovered = await Connect();
            var response = recovered.Request("/echo", "fresh link", timeout: 60)!;
            Assert.Equal("fresh link", await response.ResponseTask.WaitAsync(Patience));
            recovered.Teardown();
            output.WriteLine($"Actual Python transport + .NET transport: 40 ms bridge RTT, negotiated bottleneck MTU {mtu}; {bridge.Dropped} channel frames lost, exact-once delivery after retry, Buffer stream, file-backed multi-megabyte resources both ways, exhaustion teardown and fresh-link request recovery.");
        }
        finally
        {
            foreach (var message in logs) output.WriteLine(message);
            if (leaf is not null) { output.WriteLine("Leaf:\n" + leaf.Log); await leaf.DisposeAsync(); }
            if (transport is not null) { output.WriteLine("Transport:\n" + transport.Log); await transport.DisposeAsync(); }
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            await host.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TransferText : MessageBase
    {
        public TransferText() { }
        public override ushort MsgType => 0x0101;
        public string Text { get; set; } = "";
        public override byte[] Pack() => Encoding.UTF8.GetBytes(Text);
        public override void Unpack(ReadOnlySpan<byte> raw) => Text = Encoding.UTF8.GetString(raw);
    }
}
