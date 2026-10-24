using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Core;
using Xunit.Abstractions;

namespace DotReticulum.Interfaces.Tests;

public sealed class I2pLiveFactAttribute : FactAttribute
{
    public I2pLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_I2P_SAM")))
            Skip = "Actual owned-router tests require DOTRETICULUM_I2P_SAM=127.0.0.1:<SAM-port>; no simulator fallback.";
    }
}

public sealed class I2pLiveTheoryAttribute : TheoryAttribute
{
    public I2pLiveTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_I2P_SAM")))
            Skip = "Actual pinned-Python/I2P tests require DOTRETICULUM_I2P_SAM and verified Python/upstream environment.";
    }
}

[CollectionDefinition("Parity6 actual I2P router", DisableParallelization = true)]
public sealed class Parity6I2pLiveCollection { }

/// <summary>
/// Actual SAM sockets and I2P streams. Opt-in endpoint is owned by the coordinator;
/// no router is launched/configured, and no simulator/zero-hop shim is used.
/// </summary>
[Collection("Parity6 actual I2P router")]
public sealed class Parity6I2pLiveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    private static (string Host, int Port, string Text) Endpoint()
    {
        var value = Environment.GetEnvironmentVariable("DOTRETICULUM_I2P_SAM")
            ?? throw new InvalidOperationException("The owned SAM endpoint is required.");
        if (!IPEndPoint.TryParse(value, out var endpoint) || !IPAddress.IsLoopback(endpoint.Address) ||
            endpoint.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || endpoint.Port == 0)
            throw new InvalidOperationException("DOTRETICULUM_I2P_SAM must be an explicitly owned IPv4 loopback host:port.");
        return (endpoint.Address.ToString(), endpoint.Port, value);
    }

    private static string OwnedDirectory()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "i2p-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static I2pPacketInterfaceOptions Options(string directory, string name, bool connectable)
    {
        var endpoint = Endpoint();
        return new I2pPacketInterfaceOptions
        {
            Name = name, StoragePath = directory, Connectable = connectable,
            TransportIdentityHash = RandomNumberGenerator.GetBytes(16),
            SamHost = endpoint.Host, SamPort = endpoint.Port, SetupTimeout = TimeSpan.FromSeconds(90),
            ReconnectDelay = TimeSpan.FromSeconds(1), MaxIncomingPeers = 4, MaxOutgoingPeers = 4,
        };
    }

    private static async Task<byte[]> KeyHashAsync(string path, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(path, token);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private I2pPacketInterface Bearer(I2pPacketInterfaceOptions options)
    {
        var bearer = new I2pPacketInterface(options);
        bearer.Diagnostic += error =>
        {
            output.WriteLine($"{DateTimeOffset.UtcNow:O} {options.Name}: {error.GetType().Name}: {error.Message}");
            Snapshot("diagnostic", bearer);
        };
        bearer.ChildConnected += child =>
        {
            var peer = Assert.IsType<I2pPacketPeer>(child);
            peer.Connected += () => Snapshot($"{options.Name} child connected", bearer);
            peer.Disconnected += () => Snapshot($"{options.Name} child disconnected", bearer);
        };
        return bearer;
    }

    private void Snapshot(string phase, params I2pPacketInterface[] bearers)
    {
        output.WriteLine($"{DateTimeOffset.UtcNow:O} phase={phase}");
        foreach (var bearer in bearers)
        {
            output.WriteLine("Bearer: " + JsonSerializer.Serialize(bearer.GetDiagnostics()));
            foreach (var peer in bearer.Peers)
                output.WriteLine("Peer: " + JsonSerializer.Serialize(peer.GetDiagnostics()));
        }
    }

    private async Task<I2pPacketPeer> ChildAsync(ChannelReader<I2pPacketPeer> reader, string phase,
        CancellationToken token, params I2pPacketInterface[] bearers)
    {
        Snapshot($"{phase}; queued_children={(reader.CanCount ? reader.Count : -1)}", bearers);
        try
        {
            var peer = await reader.ReadAsync(token);
            output.WriteLine($"{DateTimeOffset.UtcNow:O} child dequeued during {phase}: " +
                JsonSerializer.Serialize(peer.GetDiagnostics()));
            return peer;
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        {
            Snapshot($"{phase}; deadline; queued_children={(reader.CanCount ? reader.Count : -1)}", bearers);
            throw new TimeoutException($"Actual I2P acceptance timed out awaiting {phase}. " +
                "See queued-child counts, SAM setup phases, attempts and last-error snapshots; " +
                "SAM readiness alone does not establish stream delivery.", error);
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string phase, CancellationToken token)
    {
        try
        {
            while (!condition()) await Task.Delay(100, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new TimeoutException($"Actual I2P acceptance timed out during {phase}. " +
                "A listening SAM port is not a ready I2P destination; stock sessions require router tunnels/lease sets. " +
                "No simulator or reference-side tunnel/readiness workaround was used.");
        }
    }

    private static async Task<byte[]> PacketAsync(IAsyncEnumerator<ReadOnlyMemory<byte>> receiver, byte[] expected, CancellationToken token)
    {
        while (await receiver.MoveNextAsync().AsTask().WaitAsync(token))
        {
            if (receiver.Current.Span.SequenceEqual(expected)) return receiver.Current.ToArray();
        }
        throw new EndOfStreamException("The actual I2P packet stream ended before the expected packet arrived.");
    }

    [I2pLiveFact]
    public async Task ActualRouterSamDestinationPersistenceWithoutOverlay()
    {
        var directory = OwnedDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            var options = Options(directory, "NativeSamPersistence", true) with
            {
                SetupTimeout = TimeSpan.FromSeconds(3), MaxReconnectAttempts = 0,
            };
            string? keyPath = null;
            byte[] firstHash;
            await using (var first = Bearer(options))
            {
                await first.StartAsync(timeout.Token);
                await WaitAsync(() =>
                {
                    var keyDirectory = Path.Combine(directory, "i2p");
                    if (!Directory.Exists(keyDirectory)) return false;
                    keyPath = Directory.GetFiles(keyDirectory, "*.i2p").SingleOrDefault();
                    return keyPath is not null;
                }, "real SAM HELLO/DEST GENERATE and atomic private-destination persistence", timeout.Token);
                firstHash = await KeyHashAsync(keyPath!, timeout.Token);
            }
            await using (var second = Bearer(options))
            {
                var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                second.Diagnostic += _ => observed.TrySetResult();
                await second.StartAsync(timeout.Token);
                // A real router may already be ready; an isolated router normally times out
                // SESSION CREATE. Both are valid for this narrower destination-storage test.
                await WaitAsync(() => second.Base32Address is not null || observed.Task.IsCompleted,
                    "second bearer session attempt using its persisted private destination", timeout.Token);
                Assert.Equal(firstHash, await KeyHashAsync(keyPath!, timeout.Token));
                Assert.Single(Directory.GetFiles(Path.Combine(directory, "i2p"), "*.i2p"));
                Assert.Empty(Directory.GetFiles(Path.Combine(directory, "i2p"), "*.new"));
            }
            output.WriteLine("PASS evidence: actual SAM 3.1/Ed25519 destination generation, atomic key storage, reuse across bearer restart. Stream readiness/traffic and remote overlay NOT established.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [I2pLiveFact]
    public async Task ActualRouterNativeStreamsAreBidirectionalAndPersistAcrossBearerRestart()
    {
        var directory = OwnedDirectory();
        using var timeout = new CancellationTokenSource(Deadline);
        try
        {
            var serverOptions = Options(Path.Combine(directory, "server"), "NativeLiveServer", true);
            await using var first = Bearer(serverOptions);
            var accepted = Channel.CreateBounded<I2pPacketPeer>(4);
            first.ChildConnected += child => Assert.True(accepted.Writer.TryWrite(Assert.IsType<I2pPacketPeer>(child)));
            await first.StartAsync(timeout.Token);
            await WaitAsync(() => first.Base32Address is not null, "first server SAM session readiness", timeout.Token);
            var address = first.Base32Address!;
            output.WriteLine("Actual local-router server destination: " + address);
            var keyPath = Assert.Single(Directory.GetFiles(Path.Combine(serverOptions.StoragePath, "i2p"), "*.i2p"));
            var keyHash = await KeyHashAsync(keyPath, timeout.Token);
            var outgoing = Channel.CreateBounded<I2pPacketPeer>(4);
            await using var client = Bearer(Options(Path.Combine(directory, "client"), "NativeLiveClient", false) with { Peers = [address] });
            client.ChildConnected += child => Assert.True(outgoing.Writer.TryWrite(Assert.IsType<I2pPacketPeer>(child)));
            await client.StartAsync(timeout.Token);
            var clientPeer = await ChildAsync(outgoing.Reader, "initial native initiating child", timeout.Token, first, client);
            await WaitAsync(() => clientPeer.IsConnected, "native initiating stream", timeout.Token);
            var serverPeer = await ChildAsync(accepted.Reader, "initial native accepted stream", timeout.Token, first, client);
            await using var toClient = clientPeer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
            await using var toServer = serverPeer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
            var firstPacket = TestSupport.MakePacket(101);
            await clientPeer.SendAsync(firstPacket, timeout.Token);
            Assert.Equal(firstPacket, await PacketAsync(toServer, firstPacket, timeout.Token));
            var reply = TestSupport.MakePacket(102);
            await serverPeer.SendAsync(reply, timeout.Token);
            Assert.Equal(reply, await PacketAsync(toClient, reply, timeout.Token));
            Snapshot("initial bidirectional native packet assertions passed; retiring first server", first, client);

            await first.DisposeAsync();
            Assert.False(await toServer.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
            await using var replacement = Bearer(serverOptions);
            replacement.ChildConnected += child => Assert.True(accepted.Writer.TryWrite(Assert.IsType<I2pPacketPeer>(child)));
            await replacement.StartAsync(timeout.Token);
            await WaitAsync(() => replacement.Base32Address is not null, "persisted server SAM session readiness", timeout.Token);
            Assert.Equal(address, replacement.Base32Address);
            Assert.Equal(keyHash, await KeyHashAsync(keyPath, timeout.Token));
            var newServerPeer = await ChildAsync(accepted.Reader, "accepted stream after persisted server restart",
                timeout.Token, replacement, client);
            await WaitAsync(() => clientPeer.IsConnected && Convert.ToInt32(clientPeer.GetDiagnostics()["reconnect_attempts"]) > 0,
                "stable initiating child reconnect", timeout.Token);
            Assert.Same(clientPeer, Assert.Single(client.Peers));
            Assert.NotSame(serverPeer, newServerPeer);
            await using var newToServer = newServerPeer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
            var afterRestart = TestSupport.MakePacket(103, 100);
            await clientPeer.SendAsync(afterRestart, timeout.Token);
            Assert.Equal(afterRestart, await PacketAsync(newToServer, afterRestart, timeout.Token));
            await newServerPeer.SendAsync(reply, timeout.Token);
            Assert.Equal(reply, await PacketAsync(toClient, reply, timeout.Token));
            Assert.Empty(Directory.GetFiles(Path.Combine(serverOptions.StoragePath, "i2p"), "*.new"));
            output.WriteLine("PASS evidence: native SAM streaming both directions, persisted destination, bearer restart and stable child reconnect. Router process was not restarted.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [I2pLiveTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualPinnedPythonInterfaceExchangesPlainPacketsBothDirections(bool pythonConnectable)
    {
        var directory = OwnedDirectory();
        using var timeout = new CancellationTokenSource(Deadline);
        try
        {
            var accepted = Channel.CreateBounded<I2pPacketPeer>(4);
            var name = Guid.NewGuid().ToString("N");
            await using var nativeServer = Bearer(Options(Path.Combine(directory, "native"), "NativePythonLive", !pythonConnectable));
            nativeServer.ChildConnected += child => Assert.True(accepted.Writer.TryWrite(Assert.IsType<I2pPacketPeer>(child)));
            if (!pythonConnectable)
            {
                await nativeServer.StartAsync(timeout.Token);
                await WaitAsync(() => nativeServer.Base32Address is not null, "native server for Python initiator", timeout.Token);
            }
            await using var python = new PythonPeer(output);
            var ready = await python.StartAsync(Path.Combine(directory, "python"), name,
                pythonConnectable, nativeServer.Base32Address, timeout.Token);
            Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", ready.GetProperty("revision").GetString());
            Assert.Equal("1.5.7", ready.GetProperty("rns_version").GetString());
            Assert.Equal("1.2.1", ready.GetProperty("lxmf_version").GetString());
            Assert.Matches("^[0-9a-f]{64}$", ready.GetProperty("i2p_sha256").GetString()!);
            Assert.Equal("47bc0fe460e60ca0e9660816441323bcb507e681bc7946866c192b38bd74fb25",
                ready.GetProperty("i2p_canonical_lf_sha256").GetString());
            Assert.Equal("47bc0fe460e60ca0e9660816441323bcb507e681bc7946866c192b38bd74fb25",
                ready.GetProperty("i2p_git_blob_sha256").GetString());
            output.WriteLine("Verified reference imports: " + ready.GetProperty("rns_module").GetString() + "; " + ready.GetProperty("i2p_module").GetString());
            output.WriteLine("Actual/canonical-LF/Git-blob source SHA256: " +
                ready.GetProperty("i2p_sha256").GetString() + " / " +
                ready.GetProperty("i2p_canonical_lf_sha256").GetString() + " / " +
                ready.GetProperty("i2p_git_blob_sha256").GetString());

            I2pPacketInterface? nativeClient = null;
            try
            {
                if (pythonConnectable)
                {
                    var pythonState = await python.WaitReadyAsync(server: true, timeout.Token);
                    var address = pythonState.GetProperty("b32").GetString()!;
                    nativeClient = Bearer(Options(Path.Combine(directory, "client"), "NativeToPythonLive", false) with { Peers = [address] });
                    nativeClient.ChildConnected += child => Assert.True(accepted.Writer.TryWrite(Assert.IsType<I2pPacketPeer>(child)));
                    await nativeClient.StartAsync(timeout.Token);
                }
                var peer = await ChildAsync(accepted.Reader, "mixed Python/native child", timeout.Token,
                    nativeClient is null ? [nativeServer] : [nativeServer, nativeClient]);
                await WaitAsync(() => peer.IsConnected, "mixed Python/native stream", timeout.Token);
                await python.WaitReadyAsync(server: false, timeout.Token);
                Assert.Equal(pythonConnectable, peer.IsInitiator);
                var payload = Encoding.UTF8.GetBytes("native-to-pinned-python-" + name + "-~}");
                var expectedPythonHash = Convert.FromHexString(ready.GetProperty("dest").GetString()!);
                var raw = Packet.Create(PacketType.Data, DestinationType.Plain, expectedPythonHash, payload).ToArray();
                await peer.SendAsync(raw, timeout.Token);
                var observed = await python.EventAsync("data", timeout.Token);
                Assert.Equal(payload, Convert.FromHexString(observed.GetProperty("payload").GetString()!));
                Assert.Equal("I2PInterfacePeer", observed.GetProperty("interface_type").GetString());
                Assert.Equal(!pythonConnectable, observed.GetProperty("initiator").GetBoolean());
                Assert.Equal(expectedPythonHash, Convert.FromHexString(observed.GetProperty("destination").GetString()!));

                var back = Encoding.UTF8.GetBytes("pinned-python-to-native-" + name + "-~}");
                await using var receive = peer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
                var sent = await python.CommandAsync("send", Convert.ToHexStringLower(back), timeout.Token);
                Assert.True(sent.GetProperty("sent").GetBoolean());
                var nativeHash = Convert.FromHexString(ready.GetProperty("native_dest").GetString()!);
                var expected = Packet.Create(PacketType.Data, DestinationType.Plain, nativeHash, back).ToArray();
                Assert.Equal(expected, await PacketAsync(receive, expected, timeout.Token));
                output.WriteLine($"PASS evidence: actual pinned Python {(pythonConnectable ? "server" : "initiator")} and native peer, framed PLAIN packets both directions, no IFAC credentials or source shims.");
            }
            finally { if (nativeClient is not null) await nativeClient.DisposeAsync(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class PythonPeer(ITestOutputHelper output) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Channel<JsonElement> _events = Channel.CreateBounded<JsonElement>(128);
        private readonly List<Task> _pumps = [];
        private Process? _process;
        private long _request;

        private static string Adapter()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "tests", "Interop", "live_i2p_node.py");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("Could not locate tests\\Interop\\live_i2p_node.py.");
        }

        public async Task<JsonElement> StartAsync(string config, string name, bool connectable, string? peer, CancellationToken token)
        {
            var executable = Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON")
                ?? throw new InvalidOperationException("DOTRETICULUM_PYTHON must select the isolated pinned-reference environment.");
            var upstream = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")
                ?? throw new InvalidOperationException("DOTRETICULUM_UPSTREAM must select the verified pinned checkout.");
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { Adapter(), "--config", config, "--name", name, "--sam", Endpoint().Text })
                start.ArgumentList.Add(argument);
            if (connectable) start.ArgumentList.Add("--connectable");
            else
            {
                start.ArgumentList.Add("--peer");
                start.ArgumentList.Add(peer ?? throw new InvalidOperationException("Native server has no actual destination."));
            }
            start.Environment["PYTHONPATH"] = upstream;
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the pinned Python process.");
            _pumps.Add(PumpAsync(_process.StandardOutput, true));
            _pumps.Add(PumpAsync(_process.StandardError, false));
            _pumps.Add(WatchExitAsync(_process));
            return await EventAsync("ready", token);
        }

        private async Task WatchExitAsync(Process process)
        {
            try
            {
                await process.WaitForExitAsync(_lifetime.Token);
                _events.Writer.TryComplete(new IOException($"Pinned I2P adapter exited with code {process.ExitCode}."));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }

        private async Task PumpAsync(StreamReader reader, bool stdout)
        {
            var remainingDiagnosticCharacters = 32768;
            try
            {
                while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
                {
                    if (line.Length > 65536) throw new InvalidDataException("Python observation exceeds its bound.");
                    if (stdout && line.StartsWith("@@ ", StringComparison.Ordinal))
                    {
                        using var document = JsonDocument.Parse(line.AsSpan(3).ToString());
                        await _events.Writer.WriteAsync(document.RootElement.Clone(), _lifetime.Token);
                    }
                    else if (remainingDiagnosticCharacters > 0)
                    {
                        var bounded = line[..Math.Min(line.Length, remainingDiagnosticCharacters)];
                        remainingDiagnosticCharacters -= bounded.Length;
                        output.WriteLine("Python I2P: " + bounded);
                    }
                }
            }
            catch (Exception error) when (!_lifetime.IsCancellationRequested) { _events.Writer.TryComplete(error); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }

        public async Task<JsonElement> EventAsync(string kind, CancellationToken token)
        {
            while (true)
            {
                var observation = await _events.Reader.ReadAsync(token);
                var actual = observation.GetProperty("event").GetString();
                if (actual == "error") throw new InvalidOperationException("Pinned I2P adapter: " + observation.GetProperty("message").GetString());
                if (actual == kind) return observation;
                throw new InvalidOperationException($"Unexpected Python I2P observation {actual}; expected {kind}.");
            }
        }

        public async Task<JsonElement> CommandAsync(string command, string? payload, CancellationToken token)
        {
            var request = Interlocked.Increment(ref _request);
            await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { request, command, payload }).AsMemory(), token);
            await _process.StandardInput.FlushAsync(token);
            var result = await EventAsync(command == "send" ? "sent" : command, token);
            Assert.Equal(request, result.GetProperty("request").GetInt64());
            return result;
        }

        public async Task<JsonElement> WaitReadyAsync(bool server, CancellationToken token)
        {
            while (true)
            {
                var state = await CommandAsync("stats", null, token);
                var ready = server
                    ? state.GetProperty("online").GetBoolean() && state.GetProperty("b32").ValueKind == JsonValueKind.String
                    : state.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("online").GetBoolean());
                if (ready) return state;
                await Task.Delay(500, token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is { } process)
            {
                if (!process.HasExited)
                {
                    using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await process.StandardInput.WriteLineAsync("{\"request\":0,\"command\":\"quit\"}".AsMemory(), quit.Token);
                        await process.StandardInput.FlushAsync(quit.Token);
                        process.StandardInput.Close();
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (TimeoutException)
                    {
                        output.WriteLine("Pinned upstream I2P controller did not exit promptly; terminating only the owned adapter process tree.");
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
            }
            await _lifetime.CancelAsync();
            foreach (var pump in _pumps)
            {
                try { await pump.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            }
            _process?.Dispose();
        }
    }
}
