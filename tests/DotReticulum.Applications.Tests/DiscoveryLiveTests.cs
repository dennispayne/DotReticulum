using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public sealed class DiscoveryBackboneLiveFactAttribute : FactAttribute
{
    public DiscoveryBackboneLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1")
            Skip = "Actual pinned Backbone discovery is opt-in: set DOTRETICULUM_LIVE=1, DOTRETICULUM_PYTHON and DOTRETICULUM_UPSTREAM.";
        else if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
            Skip = "Pinned BackboneInterface.__init__ supports Linux/Android only; no reference platform shim or fake Backbone advertisement is used.";
    }
}

public sealed class DiscoveryLiveTests
{
    [DiscoveryBackboneLiveFact]
    public async Task ActualPythonBackboneAdvertisementAutoconnectsAndCarriesEncryptedLinkAfterBootstrapDetaches()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "discovery-backbone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        using var sourceIdentity = Identity.FromPrivateKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var feedPort = FreePort();
        var backbonePort = FreePort(feedPort);
        var logs = new ConcurrentQueue<string>();
        var configuration = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
              discover_interfaces = yes
              required_discovery_value = 1
              autoconnect_discovered_interfaces = 1
              interface_discovery_sources = {sourceIdentity.HexHash}
            [interfaces]
              [[Bootstrap feed]]
                type = TCPServerInterface
                enabled = yes
                listen_ip = 127.0.0.1
                listen_port = {feedPort}
                bootstrap_only = yes
            """);
        var host = RnsdHost.Create(configuration, Path.Combine(root, "native", "transport_identity"), (_, message) => logs.Enqueue(message));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        foreach (var argument in new[] { FindScript(), "--config", Path.Combine(root, "python"), "--port", feedPort.ToString(),
            "--backbone-port", backbonePort.ToString() }) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["PYTHONPATH"] = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")
            ?? throw new InvalidOperationException("DOTRETICULUM_UPSTREAM is required.");
        var events = new ConcurrentQueue<JsonElement>();
        var started = false;
        Task run = Task.CompletedTask;
        Task stdout = Task.CompletedTask;
        Task stderr = Task.CompletedTask;
        async Task WaitUntil(Func<bool> condition, string description)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            while (!condition())
            {
                if (started && process.HasExited) throw new InvalidOperationException($"Reference exited {process.ExitCode}; {string.Join('\n', logs)}");
                if (run.IsCompleted) await run;
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out awaiting {description}; {string.Join('\n', logs)}");
                await Task.Delay(25, token);
            }
        }
        async Task<JsonElement> WaitEvent(string name)
        {
            await WaitUntil(() => events.Any(e => e.GetProperty("event").GetString() == name), name);
            return events.Last(e => e.GetProperty("event").GetString() == name);
        }
        async Task Command(string json)
        {
            await process.StandardInput.WriteLineAsync(json);
            await process.StandardInput.FlushAsync(token);
        }
        try
        {
            await host.StartAsync(token);
            run = host.RunAsync(token);
            started = process.Start();
            Assert.True(started);
            stdout = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                    if (line.StartsWith("@@", StringComparison.Ordinal))
                        events.Enqueue(JsonDocument.Parse(line[2..]).RootElement.Clone());
                    else logs.Enqueue(line);
            }, token);
            stderr = Task.Run(async () => logs.Enqueue(await process.StandardError.ReadToEndAsync(token)), token);
            var ready = await WaitEvent("ready");
            Assert.Equal("BackboneInterface", ready.GetProperty("backbone_type").GetString());
            Assert.Equal("RNS.Interfaces.BackboneInterface", ready.GetProperty("backbone_module").GetString());
            Assert.Equal("c3445132d89af04998c35b12e5101c8975d73599f62fe71b501b3d734c150438",
                ready.GetProperty("backbone_source_sha256").GetString());
            Assert.Equal(backbonePort, ready.GetProperty("backbone_port").GetInt32());
            Assert.Equal(sourceIdentity.HexHash, ready.GetProperty("identity").GetString());
            await Command("{\"op\":\"announce\"}");
            await WaitEvent("announced");
            await WaitUntil(() => host.Interfaces.Any(i => i.Name == "Python Backbone"), "native autoconnect to actual Python Backbone");
            var record = Assert.Single(await host.Discovery!.SnapshotAsync(cancellationToken: token));
            Assert.Equal("BackboneInterface", record.InterfaceType);
            Assert.Equal("RNS", record.Implementation);
            Assert.Equal("1.5.7", record.Version);
            Assert.True(record.Transport);
            Assert.Equal("localhost", record.ReachableOn);
            Assert.Equal(backbonePort, record.Port);
            Assert.Equal(sourceIdentity.HexHash, record.NetworkId);
            await WaitUntil(() => host.Interfaces.All(i => i.Name != "Bootstrap feed"), "bootstrap feed teardown");
            Assert.Equal("Python Backbone", Assert.Single(host.Interfaces).Name);
            await Command("{\"op\":\"data_announce\"}");
            var dataAnnounce = await WaitEvent("data_announced");
            Assert.Equal("BackboneClientInterface", dataAnnounce.GetProperty("peer_type").GetString());
            var destination = Convert.FromHexString(ready.GetProperty("data_destination").GetString()!);
            await WaitUntil(() => host.Router.HasPath(destination), "endpoint announce exclusively over Backbone");
            using var endpointIdentity = host.Router.Engine.Known.Recall(destination);
            Assert.NotNull(endpointIdentity);
            var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var link = host.Router.Links.Connect(endpointIdentity, destination, value => established.TrySetResult(value),
                _ => established.TrySetException(new IOException("Backbone link establishment failed.")));
            try
            {
                await established.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                var echoed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                link.PacketReceived = (data, _) => echoed.TrySetResult(data);
                var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
                Assert.NotNull(link.Send(payload));
                Assert.Equal(payload, await echoed.Task.WaitAsync(TimeSpan.FromSeconds(15), token));
                var observed = await WaitEvent("echoed");
                Assert.Equal("BackboneClientInterface", observed.GetProperty("bearer").GetString());
                Assert.Equal(Convert.ToHexStringLower(payload), observed.GetProperty("payload").GetString());
            }
            finally { link.Teardown(); }
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try
                {
                    await Command("{\"op\":\"quit\"}");
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
                { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            }
            await timeout.CancelAsync();
            try { await Task.WhenAll(stdout, stderr, run); } catch (OperationCanceledException) { }
            await host.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [InProcessLiveFact]
    public async Task PlainAndEncryptedAdvertisementsCrossRealTcpBothDirections()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "discovery-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var nativeLog = new StringBuilder();
        var node = ReticulumNode.Create(RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
            [interfaces]
              [[Discovery]]
                type = TCPServerInterface
                enabled = yes
                listen_ip = 127.0.0.1
                listen_port = {port}
            """), Path.Combine(root, "native"), (_, text) => { lock (nativeLog) nativeLog.AppendLine(text); });
        using var network = Identity.FromPrivateKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        using var sender = Identity.Generate();
        var events = new ConcurrentQueue<JsonElement>();
        var errors = new StringBuilder();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        var processStarted = false;
        var script = FindScript();
        foreach (var arg in new[] { script, "--config", Path.Combine(root, "python"), "--port", port.ToString() }) process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["PYTHONPATH"] = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")
            ?? throw new InvalidOperationException("DOTRETICULUM_UPSTREAM is required.");
        var received = new ConcurrentQueue<DiscoveryRecord>();
        await using var discovery = new DiscoveryService(Path.Combine(root, "state"),
            new() { Enabled = true, RequiredStampValue = 8 }, networkIdentity: network, discovered: received.Enqueue);
        var adapterTasks = new ConcurrentBag<Task>();
        node.Router.Engine.RegisterHandler(new CallbackAnnounceHandler(DiscoveryCodec.Aspect, announce =>
            adapterTasks.Add(discovery.ReceiveAsync(announce.Identity, announce.AppData ?? [], announce.Hops, token).AsTask())));
        Task output = Task.CompletedTask;
        Task stderr = Task.CompletedTask;
        try
        {
            await node.StartAsync(token);
            processStarted = process.Start();
            Assert.True(processStarted);
            output = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                    if (line.StartsWith("@@", StringComparison.Ordinal))
                        events.Enqueue(JsonDocument.Parse(line[2..]).RootElement.Clone());
                    else lock (errors) errors.AppendLine(line);
            }, token);
            stderr = Task.Run(async () => { var text = await process.StandardError.ReadToEndAsync(token); lock (errors) errors.Append(text); }, token);
            async Task<JsonElement> Wait(string name, int count = 1)
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (events.Count(e => e.GetProperty("event").GetString() == name) < count)
                {
                    if (process.HasExited) throw new InvalidOperationException($"Reference exited {process.ExitCode}: {errors}");
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException($"Missing {name} #{count}; observed {string.Join(", ", events.Select(e => e.ToString()))}\nPython: {errors}\nNative: {nativeLog}");
                    await Task.Delay(20, token);
                }
                return events.Last(e => e.GetProperty("event").GetString() == name);
            }
            async Task Command(object command)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
                await process.StandardInput.FlushAsync(token);
            }
            await Wait("ready");
            await Task.Delay(300, token);
            var pythonReceipts = 0;
            var nativeReceipts = 0;
            foreach (var encrypted in new[] { false, true })
            {
                // RNS transport ignores same-destination announces without a newer second-resolution emission time.
                if (encrypted) await Task.Delay(1100, token);
                var advertised = new DiscoveryAdvertisement
                {
                    Name = encrypted ? "Native encrypted" : "Native plain", TransportIdentity = sender.Hash,
                    ReachableOn = "native.example", Port = 4242, StampValue = 8, Encrypt = encrypted,
                };
                var data = DiscoveryCodec.Create(advertised, network, token);
                var local = new LocalDestination(sender, Destination.Create(DestinationType.Single, "rnstransport", ["discovery", "interface"], sender)) { AppData = data };
                node.Router.RegisterLocalDestination(local);
                await node.Router.AnnounceAsync(local, cancellationToken: token);
                var receipt = await Wait("received", ++pythonReceipts);
                Assert.Equal(advertised.Name, receipt.GetProperty("name").GetString());
                Assert.Equal("DotReticulum", receipt.GetProperty("implementation").GetString());
                await Command(new { op = "announce", encrypted });
                await Wait("announced", ++nativeReceipts);
                while (received.Count < nativeReceipts) await Task.Delay(20, token);
                Assert.Equal("Python discovery", received.Last().Name);
                Assert.Equal("RNS", received.Last().Implementation);
            }
            await Task.WhenAll(adapterTasks);
        }
        finally
        {
            if (processStarted && !process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("{\"op\":\"quit\"}");
                    await process.StandardInput.FlushAsync();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
                { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            }
            await timeout.CancelAsync();
            try { await Task.WhenAll(output, stderr); } catch (OperationCanceledException) { }
            process.Dispose();
            await node.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string FindScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "tests", "DotReticulum.Applications.Tests", "DiscoveryReference.py");
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("DiscoveryReference.py not found.");
    }

    private static int FreePort(int excluded = 0)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (port != excluded) return port;
        }
        throw new IOException("Could not select distinct discovery test ports.");
    }
}
