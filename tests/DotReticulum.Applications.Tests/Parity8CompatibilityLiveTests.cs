using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Tests.Interop;
using DotReticulum.Transport;
using Xunit.Abstractions;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Integration")]
public sealed class Parity8CompatibilityLiveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(4);

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (!predicate()) await Task.Delay(25, timeout.Token);
    }

    [Trait("Category", "Live")]
    [LiveFact]
    public async Task MixedTransports_RetryLostResourcePartsBothWays_ThenReconnectAndRestartWithSurvivingLeaves()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "parity8-mesh-" + Guid.NewGuid().ToString("N"));
        var port = Port();
        var pythonPort = Port();
        var config = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
              enable_transport = yes
              qlen_in_data = 2
              qlen_in_announce = 2
              qlen_in_pr = 2
              qlen_in_il = 2
            [interfaces]
              [[Server]]
                type = TCPServerInterface
                enabled = yes
                mode = gateway
                listen_ip = 127.0.0.1
                listen_port = {port}
            """);
        var logs = new ConcurrentQueue<string>();
        var identityPath = Path.Combine(directory, "transport_identity");
        RnsdHost? host = null;
        CancellationTokenSource? stop = null;
        Task running = Task.CompletedTask;
        PyLinkNode? router = null, a = null, b = null;
        await using var bridge = new ResourceLossBridge(port);

        async Task StartHost()
        {
            stop = new CancellationTokenSource();
            host = RnsdHost.Create(config, identityPath, (level, message) => logs.Enqueue($"[{level}] {message}"));
            await host.StartAsync(stop.Token);
            running = host.RunAsync(stop.Token);
        }

        async Task StopHost()
        {
            if (host is null) return;
            await stop!.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            await host.DisposeAsync();
            host = null;
            stop.Dispose();
            stop = null;
        }

        async Task Discover()
        {
            Assert.True((await a!.Command($"request_path {b!.HashHex} 60", "path", Patience)).GetProperty("has_path").GetBoolean());
            Assert.Equal(3, (await a.Command($"path {b.HashHex}", "path", Patience)).GetProperty("hops").GetInt32());
            Assert.True((await b.Command($"request_path {a.HashHex} 60", "path", Patience)).GetProperty("has_path").GetBoolean());
            Assert.Equal(3, (await b.Command($"path {a.HashHex}", "path", Patience)).GetProperty("hops").GetInt32());
        }

        async Task Connect(PyLinkNode initiator, PyLinkNode responder)
        {
            var before = responder.Count("link_up");
            var established = await initiator.Command($"connect {responder.HashHex}", "link_up", Patience);
            var accepted = await responder.NextAsync("link_up", before, Patience);
            Assert.Equal(established.GetProperty("link_id").GetString(), accepted.GetProperty("link_id").GetString());
            Assert.Equal(1, host!.Router.LinkTableCount);
        }

        async Task Close(PyLinkNode initiator, PyLinkNode responder)
        {
            var before = responder.Count("link_closed");
            await initiator.SendLineAsync("close");
            await responder.NextAsync("link_closed", before, Patience);
        }

        async Task Send(PyLinkNode sender, PyLinkNode receiver, string payload)
        {
            var before = receiver.Count("data");
            var sent = await sender.Command("send " + payload, "sent", Patience);
            Assert.True(sent.GetProperty("ok").GetBoolean());
            Assert.True(sent.GetProperty("delivered").GetBoolean());
            Assert.Equal(payload, (await receiver.NextAsync("data", before, Patience)).GetProperty("payload").GetString());
        }

        async Task Resource(PyLinkNode sender, PyLinkNode receiver, bool fromPython, int size, string seed)
        {
            var before = receiver.Count("resource_done");
            var lostBefore = bridge.Dropped;
            var retryBefore = bridge.Retried;
            bridge.LoseResourceParts(fromPython, 3);
            var sent = await sender.Command($"resource {size} {seed} random", "resource_sent", TransferTimeout);
            var received = await receiver.NextAsync("resource_done", before, Patience);
            Assert.Equal(6, sent.GetProperty("status").GetInt32());
            Assert.Equal(6, received.GetProperty("status").GetInt32());
            Assert.Equal(size, received.GetProperty("size").GetInt32());
            Assert.Equal(sent.GetProperty("sha256").GetString(), received.GetProperty("sha256").GetString());
            Assert.False(sent.GetProperty("compressed").GetBoolean());
            Assert.Equal(lostBefore + 3, bridge.Dropped);
            Assert.Equal(retryBefore + 3, bridge.Retried);
            Assert.Single(receiver.Events("resource_done").Skip(before));
        }

        try
        {
            await StartHost();
            var identity = host!.Router.TransportId!.ToArray();
            bridge.Start();
            router = await PyLinkNode.StartAsync("transport", tcpServerPort: pythonPort, rnsdPort: bridge.Port,
                harness: "live_mixed_transport_node.py");
            a = await PyLinkNode.StartAsync("a", rnsdPort: pythonPort, harness: "parity8_transport_node.py");
            b = await PyLinkNode.StartAsync("b", rnsdPort: port, harness: "parity8_transport_node.py");
            await Until(() => Assert.IsType<TcpServerPacketInterface>(host.Interfaces.Single().Interface).ClientCount == 2);
            var ready = Assert.Single(router.Events("ready"));
            Assert.True(ready.GetProperty("transport").GetBoolean());
            Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", ready.GetProperty("revision").GetString());
            await Discover();
            await Connect(a, b);
            await Resource(a, b, fromPython: true, 2_200_017, "parity8-ab");
            await Resource(b, a, fromPython: false, 2_200_019, "parity8-ba");
            await Close(a, b);

            var sessions = bridge.Sessions;
            bridge.Disconnect();
            await Until(() => bridge.Sessions > sessions);
            await Discover();
            await Connect(b, a);
            await Send(b, a, "7265636f6e6e656374");
            await Send(a, b, "7265636f6e6e6563746564");
            await Close(b, a);

            sessions = bridge.Sessions;
            await StopHost();
            await StartHost();
            Assert.Equal(identity, host!.Router.TransportId);
            await Until(() => bridge.Sessions > sessions);
            await Until(() => Assert.IsType<TcpServerPacketInterface>(host.Interfaces.Single().Interface).ClientCount == 2);
            // Both leaf processes stay alive; fresh announces replace stale routes to old TCP peers.
            await a.Command("announce", "announced", Patience);
            await b.Command("announce", "announced", Patience);
            await Until(() => host.Router.HasPath(a.Hash) && host.Router.HasPath(b.Hash));
            Assert.Equal(2, host.Router.HopsTo(a.Hash));
            Assert.Equal(1, host.Router.HopsTo(b.Hash));
            await Discover();
            await Connect(b, a);
            await Send(a, b, "726573746172742d6162");
            await Send(b, a, "726573746172742d6261");
            await Resource(b, a, fromPython: false, 220_019, "parity8-restart");
            await Close(b, a);
            Assert.True(bridge.BackpressureWaits > 0, "The capacity-one bearer queue must actually apply backpressure.");
            Assert.Equal(9, bridge.Dropped);
            Assert.Equal(9, bridge.Retried);
            output.WriteLine($"A(Python leaf) -- Python transport -- impaired TCP -- .NET transport -- B(Python leaf); " +
                $"2.2 MB incompressible resources both ways, 20 ms/frame latency, capacity-one bearer queues, " +
                $"router inbound capacities=2; dropped/retransmitted parts={bridge.Dropped}/{bridge.Retried}, " +
                $"backpressure waits={bridge.BackpressureWaits}, TCP sessions={bridge.Sessions}; " +
                "proved packets both ways after TCP reconnect and persisted-identity .NET router restart, then another impaired resource.");
        }
        finally
        {
            try
            {
                var peers = new[] { a, b, router }.OfType<PyLinkNode>().ToArray();
                foreach (var peer in peers) output.WriteLine(peer.Name + ":\n" + peer.Log);
                await Task.WhenAll(peers.Select(peer => peer.DisposeAsync().AsTask()));
            }
            finally
            {
                await StopHost();
                output.WriteLine(string.Join("\n", logs));
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class ResourceLossBridge(int targetPort) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, byte> _missing = new();
        private TcpClient? _incoming, _outgoing;
        private Task _running = Task.CompletedTask;
        private int _sessions, _dropped, _retried, _backpressureWaits, _dropUp, _dropDown;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Sessions => Volatile.Read(ref _sessions);
        public int Dropped => Volatile.Read(ref _dropped);
        public int Retried => Volatile.Read(ref _retried);
        public int BackpressureWaits => Volatile.Read(ref _backpressureWaits);

        public void Start() { _listener.Start(1); _running = Run(); }
        public void LoseResourceParts(bool fromPython, int count) =>
            Interlocked.Exchange(ref (fromPython ? ref _dropUp : ref _dropDown), count);
        public void Disconnect() { lock (_gate) { _incoming?.Dispose(); _outgoing?.Dispose(); } }

        private async Task Run()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var incoming = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var outgoing = new TcpClient();
                    using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    lock (_gate) { _incoming = incoming; _outgoing = outgoing; }
                    try
                    {
                        await outgoing.ConnectAsync(IPAddress.Loopback, targetPort, session.Token);
                        Interlocked.Increment(ref _sessions);
                        var up = Pump(incoming.GetStream(), outgoing.GetStream(), true, session.Token);
                        var down = Pump(outgoing.GetStream(), incoming.GetStream(), false, session.Token);
                        await Task.WhenAny(up, down);
                        await session.CancelAsync();
                        await Task.WhenAll(up, down);
                    }
                    catch (Exception error) when (error is not InvalidDataException
                        && error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
                    finally { lock (_gate) { _incoming = null; _outgoing = null; } }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        private async Task Pump(NetworkStream source, NetworkStream destination, bool fromPython, CancellationToken token)
        {
            var queue = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
            {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
            });
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(token);
            var read = ReadFrames();
            var write = WriteFrames();
            try
            {
                await Task.WhenAny(read, write);
                await stopped.CancelAsync();
                await Task.WhenAll(read, write);
            }
            finally { await stopped.CancelAsync(); }

            async Task ReadFrames()
            {
                var input = new byte[4096];
                var frame = new List<byte>(4096);
                try
                {
                    while (true)
                    {
                        var count = await source.ReadAsync(input, stopped.Token);
                        if (count == 0) return;
                        for (var index = 0; index < count; index++)
                        {
                            var value = input[index];
                            frame.Add(value);
                            if (frame.Count > (InterfaceAccessCode.MaximumPacketSize + 64) * 2 + 2)
                                throw new InvalidDataException("Unbounded HDLC frame in compatibility bearer.");
                            if (value != 0x7e) continue;
                            if (frame.Count > 2)
                            {
                                var bytes = frame.ToArray();
                                if (!queue.Writer.TryWrite(bytes))
                                {
                                    Interlocked.Increment(ref _backpressureWaits);
                                    await queue.Writer.WriteAsync(bytes, stopped.Token);
                                }
                            }
                            frame.Clear();
                            frame.Add(0x7e);
                        }
                    }
                }
                finally { queue.Writer.TryComplete(); }
            }

            async Task WriteFrames()
            {
                await foreach (var frame in queue.Reader.ReadAllAsync(stopped.Token))
                {
                    var decoded = new List<byte>(frame.Length);
                    var escaped = false;
                    foreach (var value in frame)
                    {
                        if (value == 0x7e) continue;
                        if (escaped) { decoded.Add((byte)(value ^ 0x20)); escaped = false; }
                        else if (value == 0x7d) escaped = true;
                        else decoded.Add(value);
                    }
                    if (!Packet.TryParse(decoded.ToArray(), out var packet, InterfaceAccessCode.MaximumPacketSize))
                        throw new InvalidDataException("Malformed packet in compatibility bearer.");
                    if (packet.Context == LinkContext.Resource)
                    {
                        var key = (fromPython ? "up:" : "down:") + Convert.ToHexStringLower(packet.CalculateHash());
                        ref var remaining = ref (fromPython ? ref _dropUp : ref _dropDown);
                        if (Volatile.Read(ref remaining) > 0)
                        {
                            Interlocked.Decrement(ref remaining);
                            if (!_missing.TryAdd(key, 0)) throw new InvalidDataException("Loss schedule selected a duplicate resource part.");
                            Interlocked.Increment(ref _dropped);
                            continue;
                        }
                        if (_missing.TryRemove(key, out _)) Interlocked.Increment(ref _retried);
                    }
                    await Task.Delay(20, stopped.Token);
                    await destination.WriteAsync(frame, stopped.Token);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            Disconnect();
            try { await _running.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { _stop.Dispose(); }
        }
    }
}
