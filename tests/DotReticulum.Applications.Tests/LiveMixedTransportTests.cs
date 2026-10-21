using System.Net;
using System.Net.Sockets;
using System.Text;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Tests.Interop;
using Xunit.Abstractions;

namespace DotReticulum.Applications.Tests;

// Actual upstream transport (not a leaf): A --TCP-- Python router --TCP-- .NET router --TCP-- B.
// All listeners bind loopback; pinned checkout and imported RNS path are verified by the harness.
public sealed class LiveMixedTransportTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);
    private const string Harness = "live_mixed_transport_node.py";

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [LiveFact]
    public async Task PythonTransportAndDotNetRouter_ForwardUnknownPathRequests_Announces_EncryptedDatagramsAndLinks()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "mixed-router-" + Guid.NewGuid().ToString("N"));
        var log = new StringBuilder();
        using var stop = new CancellationTokenSource();
        var port = Port();
        var pythonPort = Port();
        var config = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
              enable_transport = yes
            [interfaces]
              [[Server]]
                type = TCPServerInterface
                enabled = yes
                mode = gateway
                listen_ip = 127.0.0.1
                listen_port = {port}
            """);
        var host = RnsdHost.Create(config, Path.Combine(directory, "transport_identity"),
            (level, message) => { lock (log) log.AppendLine($"[{level}] {message}"); });
        host.Router.Log = message => { lock (log) log.AppendLine(message); };
        Task run = Task.CompletedTask;
        PyLinkNode? router = null, a = null, b = null;
        try
        {
            await host.StartAsync(stop.Token);
            run = host.RunAsync(stop.Token);
            router = await PyLinkNode.StartAsync("python-router", tcpServerPort: pythonPort, rnsdPort: port, harness: Harness);
            a = await PyLinkNode.StartAsync("a", rnsdPort: pythonPort, harness: Harness);
            b = await PyLinkNode.StartAsync("b", rnsdPort: port, harness: Harness);
            Assert.True(router.Events("ready").Single().GetProperty("transport").GetBoolean());
            Assert.False(a.Events("ready").Single().GetProperty("transport").GetBoolean());
            Assert.False(b.Events("ready").Single().GetProperty("transport").GetBoolean());
            Assert.False(host.Router.HasPath(b.Hash));
            Assert.False((await a.Command($"path {b.HashHex}", "path", Patience)).GetProperty("has_path").GetBoolean());
            Assert.False((await router.Command($"path {b.HashHex}", "path", Patience)).GetProperty("has_path").GetBoolean());

            // No cached announce exists anywhere: the request must cross BOTH transport nodes to reach B.
            var path = await a.Command($"request_path {b.HashHex} 30", "path", Patience);
            Assert.True(path.GetProperty("has_path").GetBoolean());
            Assert.Equal(3, path.GetProperty("hops").GetInt32());
            Assert.Equal(1, host.Router.HopsTo(b.Hash));
            lock (log) Assert.Contains($"Path request for {b.HashHex}", log.ToString());
            Assert.False(host.Router.HasPath(a.Hash));
            Assert.False((await b.Command($"path {a.HashHex}", "path", Patience)).GetProperty("has_path").GetBoolean());
            var reversePath = await b.Command($"request_path {a.HashHex} 30", "path", Patience);
            Assert.True(reversePath.GetProperty("has_path").GetBoolean());
            Assert.Equal(3, reversePath.GetProperty("hops").GetInt32());
            Assert.Equal(2, host.Router.HopsTo(a.Hash));
            output.WriteLine("Unknown path discovery in both directions: 3 hops, Python + .NET transport.");

            await b.Command("announce", "announced", Patience);
            Assert.Equal(3, (await a.WaitForAnnounce(b.Hash, Patience)).GetProperty("hops").GetInt32());
            await a.Command("announce", "announced", Patience);
            Assert.Equal(3, (await b.WaitForAnnounce(a.Hash, Patience)).GetProperty("hops").GetInt32());

            var receivedB = b.Count("datagram");
            Assert.True((await a.Command($"datagram {b.HashHex} 616263", "datagram_sent", Patience)).GetProperty("delivered").GetBoolean());
            var datagram = await b.NextAsync("datagram", receivedB, Patience);
            Assert.Equal("616263", datagram.GetProperty("payload").GetString());
            Assert.Equal(3, datagram.GetProperty("hops").GetInt32());
            var receivedA = a.Count("datagram");
            Assert.True((await b.Command($"datagram {a.HashHex} 646566", "datagram_sent", Patience)).GetProperty("delivered").GetBoolean());
            Assert.Equal("646566", (await a.NextAsync("datagram", receivedA, Patience)).GetProperty("payload").GetString());

            var upB = b.Count("link_up");
            var established = await a.Command($"connect {b.HashHex}", "link_up", Patience);
            var accepted = await b.NextAsync("link_up", upB, Patience);
            Assert.Equal(established.GetProperty("link_id").GetString(), accepted.GetProperty("link_id").GetString());
            Assert.Equal(1, host.Router.LinkTableCount);
            var dataB = b.Count("data");
            Assert.True((await a.Command("send 70696e67", "sent", Patience)).GetProperty("delivered").GetBoolean());
            Assert.Equal("70696e67", (await b.NextAsync("data", dataB, Patience)).GetProperty("payload").GetString());
            var dataA = a.Count("data");
            Assert.True((await b.Command("send 706f6e67", "sent", Patience)).GetProperty("delivered").GetBoolean());
            Assert.Equal("706f6e67", (await a.NextAsync("data", dataA, Patience)).GetProperty("payload").GetString());
            var closedB = b.Count("link_closed");
            await a.SendLineAsync("close");
            await b.NextAsync("link_closed", closedB, Patience);
            output.WriteLine("Announces, encrypted datagrams + validated delivery proofs, link data/proofs both ways, teardown crossed both transports.");
        }
        finally
        {
            lock (log) output.WriteLine(log.ToString());
            if (a is not null) { output.WriteLine("A:\n" + a.Log); await a.DisposeAsync(); }
            if (b is not null) { output.WriteLine("B:\n" + b.Log); await b.DisposeAsync(); }
            if (router is not null) { output.WriteLine("Python router:\n" + router.Log); await router.DisposeAsync(); }
            await stop.CancelAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            await host.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
