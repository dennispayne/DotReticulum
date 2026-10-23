using System.Net;
using System.Net.Sockets;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Tests.Interop;
using Xunit.Abstractions;

namespace DotReticulum.Applications.Tests;

public sealed class Parity4TransportLiveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private const string Harness = "live_mixed_transport_node.py";

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // One read/write at a time per direction; no unbounded task or byte queue.
    private sealed class ImpairedBridge(int targetPort) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private TcpClient? _incoming;
        private TcpClient? _outgoing;
        private Task _run = Task.CompletedTask;
        private int _sessions;
        private int _dropFrames;
        private int _dropped;
        public int Sessions => Volatile.Read(ref _sessions);
        public int Dropped => Volatile.Read(ref _dropped);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Start()
        {
            _listener.Start(1);
            _run = RunAsync();
        }

        public void DropNextFrame() => Interlocked.Exchange(ref _dropFrames, 1);

        public void Disconnect()
        {
            lock (_gate) { _incoming?.Dispose(); _outgoing?.Dispose(); }
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var incoming = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var outgoing = new TcpClient();
                    await outgoing.ConnectAsync(IPAddress.Loopback, targetPort, _stop.Token);
                    lock (_gate) { _incoming = incoming; _outgoing = outgoing; }
                    Interlocked.Increment(ref _sessions);
                    using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    var up = Pump(incoming.GetStream(), outgoing.GetStream(), true, session.Token);
                    var down = Pump(outgoing.GetStream(), incoming.GetStream(), false, session.Token);
                    await Task.WhenAny(up, down);
                    await session.CancelAsync();
                    try { await Task.WhenAll(up, down); }
                    catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
                    lock (_gate) { _incoming = null; _outgoing = null; }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        private async Task Pump(NetworkStream from, NetworkStream to, bool impair, CancellationToken token)
        {
            var input = new byte[2048];
            var frame = new List<byte>(2048);
            while (true)
            {
                var count = await from.ReadAsync(input, token);
                if (count == 0) return;
                for (var index = 0; index < count; index++)
                {
                    var value = input[index];
                    frame.Add(value);
                    if (frame.Count > 65_536) throw new InvalidDataException("Test bridge received an unbounded HDLC frame.");
                    if (value != 0x7e) continue;
                    if (frame.Count > 2)
                    {
                        if (impair && Interlocked.Exchange(ref _dropFrames, 0) != 0)
                            Interlocked.Increment(ref _dropped);
                        else
                        {
                            // 20 ms one-way latency plus a 64 kbit/s serialization delay.
                            await Task.Delay(TimeSpan.FromMilliseconds(20 + frame.Count * 8.0 / 64), token);
                            await to.WriteAsync(frame.ToArray(), token);
                        }
                    }
                    frame.Clear();
                    frame.Add(0x7e);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            Disconnect();
            await _run.WaitAsync(TimeSpan.FromSeconds(10));
            _stop.Dispose();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (!condition()) await Task.Delay(25, timeout.Token);
    }

    [LiveFact]
    public async Task MixedTransportsRecoverAfterConstrainedLostAnnouncementAndTcpReconnectAndExchangeTunnels()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "parity4-live-" + Guid.NewGuid().ToString("N"));
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
                network_name = parity4-mixed
                pass_phrase = owned-live-interface-key
                ifac_size = 128
                bitrate = 64000
            """);
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var host = RnsdHost.Create(config, Path.Combine(directory, "transport_identity"),
            (level, message) => logs.Enqueue($"[{level}] {message}"));
        using var stop = new CancellationTokenSource();
        await using var bridge = new ImpairedBridge(port);
        Task run = Task.CompletedTask;
        PyLinkNode? python = null, a = null, b = null;
        try
        {
            await host.StartAsync(stop.Token);
            run = host.RunAsync(stop.Token);
            bridge.Start();
            var credentials = new[] { "--network-name", "parity4-mixed", "--network-key", "owned-live-interface-key" };
            python = await PyLinkNode.StartAsync("transport", tcpServerPort: pythonPort, rnsdPort: bridge.Port, harness: Harness, extraArguments: credentials);
            a = await PyLinkNode.StartAsync("a", rnsdPort: pythonPort, harness: Harness, extraArguments: credentials);
            b = await PyLinkNode.StartAsync("b", rnsdPort: port, harness: Harness, extraArguments: credentials);
            Assert.True(python.Events("ready").Single().GetProperty("transport").GetBoolean());
            await Until(() => bridge.Sessions == 1 && host.Router.TunnelCount >= 1);

            var request = await a.Command($"request_path {b.HashHex} 45", "path", Patience);
            Assert.True(request.GetProperty("has_path").GetBoolean());
            Assert.Equal(3, request.GetProperty("hops").GetInt32());

            // The first transport-to-transport announcement is lost, then a fresh one repairs the path.
            bridge.DropNextFrame();
            await a.Command("announce", "announced", Patience);
            await Until(() => bridge.Dropped == 1);
            Assert.False(host.Router.HasPath(a.Hash), "The bridge must lose the announcement, not an empty delimiter or unrelated control frame.");
            await a.Command("announce", "announced", Patience);
            await Until(() => host.Router.HasPath(a.Hash));
            Assert.Equal(2, host.Router.HopsTo(a.Hash));
            Assert.True((await a.Command($"datagram {b.HashHex} 010203", "datagram_sent", Patience)).GetProperty("delivered").GetBoolean());

            var before = host.Router.ExportState(_ => new byte[32]);
            Assert.Contains(before.Tunnels!, tunnel => tunnel.Paths.Any(path => path.DestinationHash.AsSpan().SequenceEqual(a.Hash)));
            var peer = Assert.Single(host.Router.Paths, path => path.DestinationHash.AsSpan().SequenceEqual(a.Hash)).Interface!;
            await host.Router.SynthesizeTunnelAsync(peer, System.Security.Cryptography.SHA256.HashData("native-server-peer"u8));
            var tunnelStatus = await python.Command("tunnels", "tunnels", Patience);
            // Delivery is asynchronous across the impaired bridge.
            for (var attempt = 0; tunnelStatus.GetProperty("entries").GetArrayLength() == 0 && attempt < 100; attempt++)
            {
                await Task.Delay(50);
                tunnelStatus = await python.Command("tunnels", "tunnels", Patience);
            }
            Assert.NotEmpty(tunnelStatus.GetProperty("entries").EnumerateArray());

            bridge.Disconnect();
            await Until(() => bridge.Sessions >= 2);
            await Until(() => host.Router.HasPath(a.Hash));
            Assert.Equal(2, host.Router.HopsTo(a.Hash));
            Assert.True((await a.Command($"datagram {b.HashHex} 040506", "datagram_sent", Patience)).GetProperty("delivered").GetBoolean());
            var reverse = await b.Command($"request_path {a.HashHex} 45", "path", Patience);
            Assert.True(reverse.GetProperty("has_path").GetBoolean());
            Assert.Equal(3, reverse.GetProperty("hops").GetInt32());
            Assert.True((await b.Command($"datagram {a.HashHex} 070809", "datagram_sent", Patience)).GetProperty("delivered").GetBoolean());
            output.WriteLine($"Two actual IFAC-authenticated transports, 64 kbit/s + 40 ms RTT, {bridge.Dropped} dropped frame, {bridge.Sessions} TCP sessions; tunnel restoration and proven encrypted datagrams both ways.");
        }
        finally
        {
            if (a is not null) { output.WriteLine(a.Log); await a.DisposeAsync(); }
            if (b is not null) { output.WriteLine(b.Log); await b.DisposeAsync(); }
            if (python is not null) { output.WriteLine(python.Log); await python.DisposeAsync(); }
            await stop.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            output.WriteLine(string.Join("\n", logs));
        }
        await host.DisposeAsync();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
