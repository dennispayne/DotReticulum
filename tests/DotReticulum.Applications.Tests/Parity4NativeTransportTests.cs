using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DotReticulum.Applications.Runtime;
using DotReticulum.Tests.Interop;
using Xunit.Abstractions;

namespace DotReticulum.Applications.Tests;

public sealed class Parity4NativeLiveFactAttribute : FactAttribute
{
    public Parity4NativeLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_NATIVE_EXE")))
            Skip = "Requires DOTRETICULUM_LIVE=1, verified Python reference and DOTRETICULUM_NATIVE_EXE.";
    }
}

public sealed class Parity4NativeTransportTests(ITestOutputHelper output)
{
    private sealed class Daemon : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly Task[] _pumps;
        public string Log => string.Join("\n", _lines);

        public Daemon(string config)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_NATIVE_EXE")!)
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "rnsd", "--config", config, "--interactive", "-v" }) start.ArgumentList.Add(argument);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Native daemon did not start.");
            _pumps = [Pump(_process.StandardOutput), Pump(_process.StandardError)];
        }

        private async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                _lines.Enqueue(line);
                while (_lines.Count > 1000) _lines.TryDequeue(out _);
            }
        }

        public async Task Command(string command, string marker)
        {
            var count = _lines.Count(line => line.Contains(marker, StringComparison.Ordinal));
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (_lines.Count(line => line.Contains(marker, StringComparison.Ordinal)) <= count)
            {
                Assert.False(_process.HasExited, Log);
                await Task.Delay(25, timeout.Token);
            }
        }

        public async Task Stop()
        {
            if (_process.HasExited) return;
            await _process.StandardInput.WriteLineAsync("quit");
            await _process.StandardInput.FlushAsync();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(_pumps);
            Assert.Equal(0, _process.ExitCode);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Stop(); }
            finally
            {
                if (!_process.HasExited) { _process.Kill(true); await _process.WaitForExitAsync(); }
                await Task.WhenAll(_pumps);
                _process.Dispose();
            }
        }
    }

    [Parity4NativeLiveFact]
    public async Task PublishedDaemonPersistsDormantTunnelsAndAuthenticallyRestoresPythonTransportPathsAfterRestart()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "parity4-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var leafProbe = new TcpListener(IPAddress.Loopback, 0);
        leafProbe.Start();
        var pythonPort = ((IPEndPoint)leafProbe.LocalEndpoint).Port;
        leafProbe.Stop();
        await File.WriteAllTextAsync(Path.Combine(directory, "config"), $"""
            [reticulum]
              enable_transport = yes
              share_instance = no
            [logging]
              loglevel = 6
            [interfaces]
              [[Server]]
                type = TCPServerInterface
                enabled = yes
                mode = gateway
                listen_ip = 127.0.0.1
                listen_port = {port}
            """);
        var store = new RuntimeStateStore(Path.Combine(directory, "storage"));
        Daemon? daemon = null;
        PyLinkNode? python = null, leaf = null;
        try
        {
            daemon = new Daemon(directory);
            await daemon.Command("status", "Status:");
            python = await PyLinkNode.StartAsync("native-peer-router", tcpServerPort: pythonPort,
                rnsdPort: port, harness: "live_mixed_transport_node.py");
            leaf = await PyLinkNode.StartAsync("native-peer-leaf", rnsdPort: pythonPort, harness: "live_mixed_transport_node.py");
            await leaf.Command("announce", "announced", TimeSpan.FromSeconds(30));
            RuntimeStateSnapshot snapshot = RuntimeStateSnapshot.Empty;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            do
            {
                await daemon.Command("persist", "State persisted.");
                snapshot = await store.LoadAsync(timeout.Token);
                if (snapshot.Tunnels.Any(t => t.Paths.Any(p => p.DestinationHash.AsSpan().SequenceEqual(leaf.Hash)))) break;
                await Task.Delay(100, timeout.Token);
            } while (true);
            var tunnel = Assert.Single(snapshot.Tunnels, t => t.Paths.Any(p => p.DestinationHash.AsSpan().SequenceEqual(leaf.Hash)));
            Assert.Equal(2, Assert.Single(tunnel.Paths, p => p.DestinationHash.AsSpan().SequenceEqual(leaf.Hash)).Hops);
            await daemon.Stop();
            output.WriteLine("First published daemon:\n" + daemon.Log);
            await daemon.DisposeAsync();

            // Same Python transport reconnects and synthesizes; the leaf does not reannounce.
            daemon = new Daemon(directory);
            await daemon.Command("status", "Status:");
            do
            {
                await daemon.Command("persist", "State persisted.");
                snapshot = await store.LoadAsync(timeout.Token);
                if (snapshot.Paths.Any(p => p.DestinationHash.AsSpan().SequenceEqual(leaf.Hash))) break;
                await Task.Delay(100, timeout.Token);
            } while (true);
            Assert.Contains(snapshot.Tunnels, t => t.TunnelId.AsSpan().SequenceEqual(tunnel.TunnelId));
            Assert.Equal(2, Assert.Single(snapshot.Paths, p => p.DestinationHash.AsSpan().SequenceEqual(leaf.Hash)).Hops);
            output.WriteLine("Published native tunnel synthesis, persisted cached announcement, daemon restart, actual Python transport reconnect and authenticated path restoration succeeded.");
        }
        finally
        {
            if (leaf is not null) { output.WriteLine(leaf.Log); await leaf.DisposeAsync(); }
            if (python is not null) { output.WriteLine(python.Log); await python.DisposeAsync(); }
            if (daemon is not null) { await daemon.DisposeAsync(); output.WriteLine(daemon.Log); }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
