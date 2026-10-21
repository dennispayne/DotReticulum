using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotReticulum.Applications.Runtime;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

/// <summary>Actual pinned Reticulum local daemon/client processes, not mocks of their authentication or HDLC.</summary>
public sealed class SharedLiveTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(n => (byte)n).ToArray();
    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [LiveFact]
    public async Task NativeIdentifiedManagementLinkQueriesActualPythonRemoteHandlers()
    {
        using var identity = Identity.Generate();
        var port = Port();
        await using var peer = await Reference.StartAsync("remote-server", port, Port(), "--allowed", Convert.ToHexStringLower(identity.Hash));
        var ready = await peer.ReadAsync("ready");
        var destination = Convert.FromHexString(ready.GetProperty("remote").GetString()!);
        await using var native = await NativeNode.StartAsync(port, server: false);
        using var client = await ManagementRemoteClient.ConnectAsync(native.Node, identity, destination, TimeSpan.FromSeconds(15));
        var status = Assert.IsType<object?[]>(await client.RequestAsync("/status", [true, false]));
        Assert.Equal(2, status.Length);
        Assert.IsType<Dictionary<object, object?>>(status[0]);
        Assert.IsType<long>(status[1]);
        var paths = Assert.IsType<object?[]>(await client.RequestAsync("/path", ["table", null, null]));
        Assert.Empty(paths);
    }

    [LiveFact]
    public async Task ActualPythonIdentifiedLinkQueriesNativeAllowlistedRemoteManagement()
    {
        var port = Port();
        await using var native = await NativeNode.StartAsync(port, server: true);
        using var identity = Identity.Generate();
        using var caller = Identity.FromPrivateKey(Enumerable.Range(0, 64).Select(n => (byte)n).ToArray());
        var service = new ManagementRemoteService(request => request.Path switch
        {
            "interface_stats" => new Dictionary<object, object?> { ["interfaces"] = Array.Empty<object?>() },
            "link_count" => 37L,
            "profiling_results" => null,
            _ => null,
        }, [caller.Hash]);
        var destination = service.Register(identity, native.Node.Router.RegisterLocalDestination);
        await using var peer = await Reference.StartAsync("remote-client", port, Port(), "--remote", Convert.ToHexStringLower(destination.Destination.Hash));
        await peer.CommandAsync("remote-status");
        var response = await peer.ReadAsync("response");
        var status = Assert.IsType<object?[]>(ManagementMessagePack.Unpack(Convert.FromHexString(response.GetProperty("packed").GetString()!)));
        Assert.Equal(3, status.Length);
        Assert.Equal(37L, status[1]);
    }

    private sealed class NativeNode : IAsyncDisposable
    {
        private readonly string directory;
        public ReticulumNode Node { get; }
        private NativeNode(ReticulumNode node, string directory) { Node = node; this.directory = directory; }
        public static async Task<NativeNode> StartAsync(int port, bool server)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "shared-native-" + Guid.NewGuid().ToString("N"));
            var type = server ? "TCPServerInterface" : "TCPClientInterface";
            var address = server ? $"listen_ip = 127.0.0.1\nlisten_port = {port}" : $"target_host = 127.0.0.1\ntarget_port = {port}";
            var config = RnsdConfigParser.Parse($"[reticulum]\nshare_instance = no\n[interfaces]\n[[Remote]]\ntype = {type}\nenabled = yes\n{address}\n");
            var node = ReticulumNode.Create(config, directory);
            try { await node.StartAsync(); return new NativeNode(node, directory); }
            catch { await node.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await Node.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [LiveFact]
    public async Task NativeRpcClientPerformsOperationsOnUnmodifiedPythonDaemon()
    {
        var port = Port();
        var control = Port();
        await using var peer = await Reference.StartAsync("server", port, control);
        using var client = new ManagementRpcClient(new IPEndPoint(IPAddress.Loopback, control), Key);
        var stats = Assert.IsType<Dictionary<object, object?>>(await client.CallAsync(new() { ["get"] = "interface_stats" }));
        Assert.NotEmpty(Assert.IsType<object?[]>(stats["interfaces"]));
        Assert.Empty(Assert.IsType<object?[]>(await client.CallAsync(new() { ["get"] = "path_table", ["max_hops"] = null })));
        var hash = Enumerable.Repeat((byte)0x67, 16).ToArray();
        Assert.Equal(true, await client.CallAsync(new() { ["blackhole_identity"] = hash, ["until"] = null, ["reason"] = "shared-live" }));
        Assert.Equal(true, await client.CallAsync(new() { ["get"] = "is_blackholed", ["identity_hash"] = hash }));
        Assert.Equal(true, await client.CallAsync(new() { ["unblackhole_identity"] = hash }));
        Assert.Equal(false, await client.CallAsync(new() { ["get"] = "is_blackholed", ["identity_hash"] = hash }));
        Assert.Equal(false, await client.CallAsync(new() { ["destination_data"] = "retain", ["destination_hash"] = hash }));
    }

    [LiveFact]
    public async Task ActualPythonMultiprocessingClientAuthenticatesAndCallsNativeServer()
    {
        await using var server = new ManagementRpcServer(new IPEndPoint(IPAddress.Loopback, 0), Key,
            (request, _) => ValueTask.FromResult<object?>(request.Path == "link_count" ? 42L : null));
        await server.StartAsync();
        await using var peer = await Reference.StartAsync("client", Port(), ((IPEndPoint)server.LocalEndpoint!).Port, "--operation", "link_count");
        var response = await peer.ReadAsync("response");
        Assert.Equal(42L, ManagementMessagePack.Unpack(Convert.FromHexString(response.GetProperty("packed").GetString()!)));
        await peer.ExpectExitAsync();
    }

    [LiveFact]
    public async Task NativeLocalClientExchangesEscapedPacketsWithPythonSharedDaemon()
    {
        var port = Port();
        await using var peer = await Reference.StartAsync("server", port, Port());
        await using var client = new LocalSharedPacketInterface(new IPEndPoint(IPAddress.Loopback, port));
        await client.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var packets = client.ReceiveAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var read = packets.MoveNextAsync().AsTask();
        // Python must have admitted this socket before its local-client fanout.
        using var rpc = new ManagementRpcClient(new IPEndPoint(IPAddress.Loopback, peer.ControlPort), Key);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            var stats = Assert.IsType<Dictionary<object, object?>>(await rpc.CallAsync(new() { ["get"] = "interface_stats" }));
            if (Assert.IsType<object?[]>(stats["interfaces"]).Length >= 2) break;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Python did not admit the native local client.");
            await Task.Delay(25);
        }
        await peer.CommandAsync("send");
        var sent = await peer.ReadAsync("packet");
        Assert.True(await read);
        var bytes = Convert.FromHexString(sent.GetProperty("packed").GetString()!);
        Assert.Equal(bytes, packets.Current.ToArray());
        await client.SendAsync(bytes, timeout.Token);
        var received = await peer.ReadAsync("received");
        Assert.Equal(bytes, Convert.FromHexString(received.GetProperty("packed").GetString()!));
    }

    [LiveFact]
    public async Task ActualRequiredPythonSharedClientExchangesPacketsWithNativeHost()
    {
        await using var server = new LocalSharedServerInterface(new IPEndPoint(IPAddress.Loopback, 0));
        var connected = new TaskCompletionSource<IPacketInterface>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ChildConnected += child => connected.TrySetResult(child);
        await server.StartAsync(CancellationToken.None);
        await using var rpc = new ManagementRpcServer(new IPEndPoint(IPAddress.Loopback, 0), Key,
            (request, _) => ValueTask.FromResult<object?>(request.Path switch
            {
                "interface_stats" => new Dictionary<object, object?> { ["interfaces"] = Array.Empty<object?>() },
                "path_table" or "rate_table" => Array.Empty<object?>(),
                _ => null,
            }));
        await rpc.StartAsync();
        await using var peer = await Reference.StartAsync("shared-client", ((IPEndPoint)server.LocalEndpoint!).Port, ((IPEndPoint)rpc.LocalEndpoint!).Port);
        var ready = await peer.ReadAsync("ready");
        Assert.True(ready.GetProperty("client").GetBoolean());
        var child = await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var packets = child.ReceiveAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await packets.MoveNextAsync());
        var sent = await peer.ReadAsync("packet");
        var bytes = Convert.FromHexString(sent.GetProperty("packed").GetString()!);
        Assert.Equal(bytes, packets.Current.ToArray());
        await child.SendAsync(bytes, timeout.Token);
        var received = await peer.ReadAsync("received");
        Assert.Equal(bytes, Convert.FromHexString(received.GetProperty("packed").GetString()!));
    }

    private sealed class Reference : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> errors;
        private readonly string directory;
        private readonly Queue<JsonElement> buffered = new();
        public int ControlPort { get; }
        private Reference(Process process, string directory, int control)
        {
            this.process = process;
            this.directory = directory;
            ControlPort = control;
            errors = process.StandardError.ReadToEndAsync();
        }
        public static async Task<Reference> StartAsync(string mode, int port, int control, params string[] extra)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "shared-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            var script = FindAdapter();
            foreach (var argument in new[] { script, mode, "--config", directory, "--port", port.ToString(), "--control-port", control.ToString(), "--key", Convert.ToHexStringLower(Key) }.Concat(extra))
                start.ArgumentList.Add(argument);
            start.Environment["PYTHONPATH"] = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")
                ?? throw new InvalidOperationException("DOTRETICULUM_UPSTREAM must point to the verified pinned checkout.");
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            var peer = new Reference(Process.Start(start) ?? throw new IOException("Python process did not start."), directory, control);
            try
            {
                if (mode != "client")
                {
                    var ready = await peer.ReadAsync("ready");
                    Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", ready.GetProperty("upstream").GetString());
                    Assert.Equal(64, ready.GetProperty("reticulum_sha256").GetString()!.Length);
                    peer.buffered.Enqueue(ready);
                }
                return peer;
            }
            catch { await peer.DisposeAsync(); throw; }
        }
        public async Task<JsonElement> ReadAsync(string expected)
        {
            if (buffered.Count > 0 && buffered.Peek().GetProperty("event").GetString() == expected) return buffered.Dequeue();
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                if (line is null) throw new IOException($"Python exited before {expected}: {await errors}");
                if (!line.StartsWith('{')) continue;
                var value = JsonDocument.Parse(line).RootElement.Clone();
                if (value.GetProperty("event").GetString() == expected) return value;
                buffered.Enqueue(value);
            }
        }
        public async Task CommandAsync(string command) { await process.StandardInput.WriteLineAsync(command); await process.StandardInput.FlushAsync(); }
        public async Task ExpectExitAsync()
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.ExitCode == 0, await errors);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    try { await CommandAsync("quit"); process.StandardInput.Close(); } catch (IOException) { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                }
                await errors;
            }
            finally
            {
                process.Dispose();
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }
        private static string FindAdapter()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "tests", "Interop", "SharedReference.py");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("SharedReference.py not found.");
        }
    }
}
