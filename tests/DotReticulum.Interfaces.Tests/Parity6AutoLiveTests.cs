using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using Xunit.Abstractions;

namespace DotReticulum.Interfaces.Tests;

/// <summary>Real pinned Python/managed .NET AutoInterface traffic over coordinator-owned Linux namespaces/veth.</summary>
[Trait("Category", "Integration")]
public sealed class Parity6AutoLiveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);
    private const string TestPassphrase = "auto-live-test-passphrase-not-a-secret";

    [Trait("Category", "Live")]
    [AutoLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPythonDiscoveryBidirectionalPacketsIfacAndPeerExpiry(bool ifac)
    {
        var device = Required("DOTRETICULUM_AUTO_DEVICE");
        var expectedPeer = IPAddress.Parse(Required("DOTRETICULUM_AUTO_EXPECTED_PEER"));
        var nativeAddress = IPAddress.Parse(Required("DOTRETICULUM_AUTO_NATIVE_ADDRESS"));
        Assert.Equal(nativeAddress, Assert.Single(new AutoPacketNetwork().GetDevices(), candidate => candidate.Name == device).Address);
        var run = Guid.NewGuid().ToString("N");
        var discoveryPort = FreeDiscoveryPorts();
        var dataPort = FreePort();
        while (dataPort == discoveryPort || dataPort == discoveryPort + 1) dataPort = FreePort();
        var group = "auto-live-" + run;
        var networkName = "auto-ifac-" + run;
        var diagnostics = new ConcurrentQueue<string>();
        var removed = new ConcurrentQueue<IPacketInterface>();
        await using var bearer = new AutoPacketInterface(new()
        {
            GroupId = group,
            Devices = [device],
            DiscoveryPort = discoveryPort,
            DataPort = dataPort,
            Diagnostic = error => diagnostics.Enqueue(error.Message),
        });
        await using var manager = new PacketInterfaceManager([bearer], channelCapacity: 32);
        manager.Diagnostic += (_, error) => diagnostics.Enqueue(error.Message);
        manager.InterfaceRemoved += removed.Enqueue;
        if (ifac) manager.SetInterfaceAccessCode(bearer, InterfaceAccessCode.Derive(networkName, TestPassphrase, 16));
        await manager.StartAsync();
        await using var python = await PythonAutoNode.StartAsync(run, group, discoveryPort, dataPort,
            ifac ? networkName : null, ifac ? TestPassphrase : null);
        var ready = python.Ready;
        output.WriteLine("Actual reference provenance: " + ready.GetRawText());
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", ready.GetProperty("revision").GetString());
        Assert.Equal("1.5.7", ready.GetProperty("rns_version").GetString());
        Assert.Equal("1.2.1", ready.GetProperty("lxmf_version").GetString());
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-auto.json")));
        Assert.Equal(fixture.RootElement.GetProperty("auto_source_canonical_lf_sha256").GetString(),
            ready.GetProperty("auto_canonical_lf_sha256").GetString());
        Assert.Equal(fixture.RootElement.GetProperty("auto_git_blob_sha256").GetString(),
            ready.GetProperty("auto_git_blob_sha256").GetString());
        var importedSource = Path.GetFullPath(ready.GetProperty("auto_module").GetString()!);
        Assert.Equal(Path.GetFullPath(Path.Combine(Required("DOTRETICULUM_UPSTREAM"), "RNS", "Interfaces", "AutoInterface.py")), importedSource);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(importedSource))),
            ready.GetProperty("auto_sha256").GetString());
        Assert.Equal(Path.GetFullPath(Path.Combine(Required("DOTRETICULUM_UPSTREAM"), "RNS", "__init__.py")),
            Path.GetFullPath(ready.GetProperty("rns_module").GetString()!));
        Assert.Equal(expectedPeer, IPAddress.Parse(ready.GetProperty("devices").GetProperty(Required("DOTRETICULUM_AUTO_PYTHON_DEVICE")).GetString()!));
        if (ifac) Assert.Equal(16, ready.GetProperty("ifac_size").GetInt32());

        await WaitUntilAsync(() => Task.FromResult(bearer.PeerCount == 1), Patience,
            () => "Native discovery failed: " + string.Join("; ", diagnostics) + "\n" + python.Log);
        var child = Assert.Single(manager.GetStatistics(), stat => ReferenceEquals(stat.Parent, bearer)).Interface;
        var childDiagnostics = Assert.IsAssignableFrom<IPacketInterfaceDiagnostics>(child).GetDiagnostics();
        Assert.Equal(expectedPeer, IPAddress.Parse((string)childDiagnostics["peer"]!));
        Assert.Equal(device, childDiagnostics["device"]);
        await WaitUntilAsync(async () =>
        {
            var stats = await python.CommandAsync("stats");
            return stats.GetProperty("peers").EnumerateArray().Any(peer =>
                IPAddress.Parse(peer.GetProperty("address").GetString()!).Equals(nativeAddress) &&
                peer.GetProperty("device").GetString() == Required("DOTRETICULUM_AUTO_PYTHON_DEVICE"));
        }, Patience, () => "Reference discovery failed:\n" + python.Log);

        var pythonDestination = Convert.FromHexString(ready.GetProperty("dest").GetString()!);
        var nativeDestination = Convert.FromHexString(ready.GetProperty("native_dest").GetString()!);
        string? wrongPayload = null;
        if (ifac)
        {
            var wrong = Encoding.UTF8.GetBytes("wrong-ifac-" + run);
            wrongPayload = Convert.ToHexStringLower(wrong);
            using var wrongCode = InterfaceAccessCode.Derive(networkName, TestPassphrase + "-wrong", 16);
            var invalid = wrongCode.Protect(Packet.Create(PacketType.Data, DestinationType.Plain, pythonDestination, wrong).ToArray());
            await child.SendAsync(invalid, default);
        }
        foreach (var size in new[] { 16, 128, 450 })
        {
            var outbound = Enumerable.Range(0, size).Select(index => (byte)(index * 37 + size)).ToArray();
            outbound[0] = 0x7e;
            outbound[1] = 0x7d;
            var expected = Convert.ToHexStringLower(outbound);
            await manager.SendToAsync(Packet.Create(PacketType.Data, DestinationType.Plain, pythonDestination, outbound), child);
            var observed = await python.WaitForEventAsync("data", value => value.GetProperty("payload").GetString() == expected, Patience);
            Assert.Equal(nativeAddress, IPAddress.Parse(observed.GetProperty("source").GetString()!));
            Assert.Equal(Required("DOTRETICULUM_AUTO_PYTHON_DEVICE"), observed.GetProperty("device").GetString());
            Assert.Equal("AutoInterfacePeer", observed.GetProperty("interface_type").GetString());
            Assert.Equal(Convert.ToHexStringLower(pythonDestination), observed.GetProperty("destination").GetString());
            if (ifac) Assert.Equal(16, observed.GetProperty("ifac_size").GetInt32());

            var reverse = outbound.Reverse().ToArray();
            var sent = await python.CommandAsync("send", Convert.ToHexStringLower(reverse));
            Assert.True(sent.GetProperty("sent").GetBoolean());
            var received = await NextPacketAsync(manager, nativeDestination, reverse);
            Assert.Same(child, received.Source);
            Assert.Equal(DestinationType.Plain, received.Packet.DestinationType);
            Assert.Equal(PacketType.Data, received.Packet.PacketType);
        }
        if (wrongPayload is not null)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.DoesNotContain(python.Events("data"), item => item.GetProperty("payload").GetString() == wrongPayload);
        }
        var exchanged = await python.CommandAsync("stats");
        var observedPeer = Assert.Single(exchanged.GetProperty("peers").EnumerateArray());
        Assert.True(observedPeer.GetProperty("rxb").GetInt64() > 0);
        Assert.True(observedPeer.GetProperty("txb").GetInt64() > 0);
        var nativeStats = Assert.Single(manager.GetStatistics(), stat => ReferenceEquals(stat.Interface, child));
        Assert.True(nativeStats.ReceivedPackets >= 3);
        Assert.Equal(3, nativeStats.SentPackets);
        if (ifac) Assert.Equal(16, nativeStats.IfacSize);
        output.WriteLine($"Verified IFAC={ifac}: both discovery identities and 16/128/450-byte data in both directions.");

        if (!ifac)
        {
            var detached = await python.CommandAsync("detach");
            Assert.False(detached.GetProperty("online").GetBoolean());
            await WaitUntilAsync(() => Task.FromResult(bearer.PeerCount == 0), Patience,
                () => "Native peer did not expire after real Python detach:\n" + python.Log);
            await WaitUntilAsync(() => Task.FromResult(removed.Contains(child) && manager.GetStatistics().Count == 1), Patience,
                () => "Expired native child was not removed from its manager.");
            Assert.True(bearer.IsOnline);
            Assert.Single(manager.GetStatistics());
            output.WriteLine("Reference detached; native child expired/was removed while its parent remained online.");
        }
        else
        {
            await manager.DisposeAsync().AsTask().WaitAsync(Patience);
            Assert.False(bearer.IsOnline);
            await WaitUntilAsync(async () =>
            {
                var stats = await python.CommandAsync("stats");
                Assert.True(stats.GetProperty("online").GetBoolean());
                return stats.GetProperty("peers").GetArrayLength() == 0;
            }, Patience, () => "Reference peer did not expire after native shutdown:\n" + python.Log);
            output.WriteLine("Native parent shut down; real reference peer expired while its parent remained online.");
        }
    }

    private static async Task<ReceivedPacket> NextPacketAsync(PacketInterfaceManager manager, byte[] destination, byte[] payload)
    {
        using var stop = new CancellationTokenSource(Patience);
        try
        {
            await foreach (var packet in manager.ReceiveWithSourceAsync(stop.Token))
                if (packet.Packet.DestinationHash.Span.SequenceEqual(destination) && packet.Packet.Payload.Span.SequenceEqual(payload)) return packet;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        throw new TimeoutException("No matching real Python packet reached the native child.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(250);
        }
        throw new TimeoutException(failure());
    }

    private static string Required(string variable) => Environment.GetEnvironmentVariable(variable)
        ?? throw new InvalidOperationException($"{variable} must be supplied by the owned namespace topology.");

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static int FreeDiscoveryPorts()
    {
        for (var i = 0; i < 100; i++)
        {
            var port = FreePort();
            if (port == 65535) continue;
            try
            {
                using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port + 1));
                return port;
            }
            catch (SocketException) { }
        }
        throw new IOException("No free discovery/reverse-discovery UDP port pair.");
    }

    private sealed class PythonAutoNode : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<JsonElement> _events = [];
        private readonly StringBuilder _log = new();
        private readonly List<Task> _pumps = [];
        private readonly string _config;
        private Process _process = null!;
        private int _request;
        private Exception? _pumpError;
        public JsonElement Ready { get; private set; }
        public string Log { get { lock (_gate) return _log.ToString(); } }

        private PythonAutoNode(string root)
        {
            var artifacts = Path.GetFullPath(Required("DOTRETICULUM_AUTO_ARTIFACTS"));
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!artifacts.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Auto artifacts must remain inside the owned repository.");
            _config = Path.Combine(artifacts, "python-" + Guid.NewGuid().ToString("N"));
        }

        public static async Task<PythonAutoNode> StartAsync(string name, string group, int discoveryPort, int dataPort,
            string? ifacName, string? passphrase)
        {
            var root = FindRoot();
            var node = new PythonAutoNode(root);
            Directory.CreateDirectory(Path.GetDirectoryName(node._config)!);
            var start = new ProcessStartInfo(Required("DOTRETICULUM_AUTO_IP"))
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = root,
            };
            foreach (var value in new[]
            {
                "netns", "exec", Required("DOTRETICULUM_AUTO_PYTHON_NAMESPACE"), Required("DOTRETICULUM_PYTHON"),
                Path.Combine(root, "tests", "Interop", "live_auto_node.py"), "--config", node._config,
                "--device", Required("DOTRETICULUM_AUTO_PYTHON_DEVICE"), "--peer", Required("DOTRETICULUM_AUTO_NATIVE_ADDRESS"),
                "--group", group, "--name", name, "--discovery-port", discoveryPort.ToString(CultureInfo.InvariantCulture),
                "--data-port", dataPort.ToString(CultureInfo.InvariantCulture),
            }) start.ArgumentList.Add(value);
            if (ifacName is not null)
            {
                start.ArgumentList.Add("--ifac-name");
                start.ArgumentList.Add(ifacName);
                start.ArgumentList.Add("--ifac-passphrase");
                start.ArgumentList.Add(passphrase!);
            }
            start.Environment["PYTHONPATH"] = Required("DOTRETICULUM_UPSTREAM");
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            node._process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch real Python AutoInterface.");
            node._pumps.Add(node.PumpAsync(node._process.StandardOutput, false));
            node._pumps.Add(node.PumpAsync(node._process.StandardError, true));
            try { node.Ready = await node.WaitForEventAsync("ready", _ => true, Patience); return node; }
            catch (Exception error)
            {
                await node.DisposeAsync();
                throw new InvalidOperationException("Real Python AutoInterface failed to become ready:\n" + node.Log, error);
            }
        }

        public JsonElement[] Events(string name)
        {
            lock (_gate) return [.. _events.Where(value => value.GetProperty("event").GetString() == name)];
        }

        public async Task<JsonElement> CommandAsync(string command, string? payload = null)
        {
            var request = Interlocked.Increment(ref _request);
            var text = JsonSerializer.Serialize(new Dictionary<string, object?> { ["request"] = request, ["command"] = command, ["payload"] = payload });
            await _process.StandardInput.WriteLineAsync(text);
            await _process.StandardInput.FlushAsync();
            var expected = command switch { "send" => "sent", "detach" => "detached", _ => command };
            return await WaitForEventAsync(expected, value => value.GetProperty("request").GetInt32() == request, TimeSpan.FromSeconds(10));
        }

        public async Task<JsonElement> WaitForEventAsync(string name, Func<JsonElement, bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_pumpError is not null) throw new InvalidOperationException("Auto adapter observation pump failed:\n" + _log, _pumpError);
                    var error = _events.FirstOrDefault(value => value.GetProperty("event").GetString() == "error");
                    if (error.ValueKind != JsonValueKind.Undefined) throw new InvalidOperationException("Auto adapter command failed: " + error);
                    foreach (var value in _events)
                        if (value.GetProperty("event").GetString() == name && predicate(value)) return value;
                }
                if (_process.HasExited) throw new InvalidOperationException("Auto adapter exited before " + name + ":\n" + Log);
                await Task.Delay(25);
            }
            throw new TimeoutException("Auto adapter did not emit " + name + ":\n" + Log);
        }

        private async Task PumpAsync(StreamReader reader, bool stderr)
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    lock (_gate)
                    {
                        _log.AppendLine((stderr ? "[stderr] " : "") + line);
                        if (_log.Length > 32768) _log.Remove(0, _log.Length - 32768);
                        if (!stderr && line.StartsWith("@@", StringComparison.Ordinal))
                        {
                            if (_events.Count >= 512) throw new IOException("Auto adapter exceeded its bounded observation event capacity.");
                            using var document = JsonDocument.Parse(line[2..]);
                            _events.Add(document.RootElement.Clone());
                        }
                    }
                }
            }
            catch (Exception error) { lock (_gate) _pumpError = error; }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                try { await _process.StandardInput.WriteLineAsync("{\"request\":0,\"command\":\"quit\"}"); _process.StandardInput.Close(); }
                catch (Exception error) when (error is IOException or InvalidOperationException) { }
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException)
                {
                    try { _process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (_process.HasExited) { }
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            await Task.WhenAll(_pumps).WaitAsync(TimeSpan.FromSeconds(10));
            _process.Dispose();
            if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
        }

        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "tests", "Interop", "live_auto_node.py"))) return directory.FullName;
            throw new FileNotFoundException("The dedicated live_auto_node.py adapter was not found above the test output.");
        }
    }
}

public sealed class AutoLiveTheoryAttribute : TheoryAttribute
{
    public AutoLiveTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_AUTO_LIVE") != "1")
        {
            Skip = "Real Python AutoInterface traffic is opt-in: run invoke-auto-topology.ps1 (DOTRETICULUM_AUTO_LIVE=1); self-echo/vector tests do not establish it.";
            return;
        }
        if (!OperatingSystem.IsLinux()) { Skip = "This live test requires the coordinator-owned Linux namespace/veth topology."; return; }
        if (!Socket.OSSupportsIPv6) { Skip = "OS IPv6 socket capability is unavailable."; return; }
        foreach (var key in new[]
        {
            "DOTRETICULUM_AUTO_DEVICE", "DOTRETICULUM_AUTO_PYTHON_DEVICE", "DOTRETICULUM_AUTO_PYTHON_NAMESPACE",
            "DOTRETICULUM_AUTO_EXPECTED_PEER", "DOTRETICULUM_AUTO_NATIVE_ADDRESS", "DOTRETICULUM_AUTO_IP",
            "DOTRETICULUM_AUTO_ARTIFACTS", "DOTRETICULUM_PYTHON", "DOTRETICULUM_UPSTREAM",
        })
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            { Skip = $"Owned Auto topology capability is not configured: {key} is missing."; return; }
        var ns = Environment.GetEnvironmentVariable("DOTRETICULUM_AUTO_PYTHON_NAMESPACE")!;
        if (!ns.StartsWith("dr-auto-", StringComparison.Ordinal) || ns.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        { Skip = "The Python namespace must be an explicitly owned dr-auto-* test namespace."; return; }
        if (!Environment.GetEnvironmentVariable("DOTRETICULUM_AUTO_DEVICE")!.StartsWith("dra", StringComparison.Ordinal) ||
            !Environment.GetEnvironmentVariable("DOTRETICULUM_AUTO_PYTHON_DEVICE")!.StartsWith("dra", StringComparison.Ordinal))
        { Skip = "This test is restricted to coordinator-owned dra* veth devices, not existing physical NICs."; return; }
        if (!File.Exists(Path.Combine("/var/run/netns", ns))) { Skip = $"Owned Python network namespace {ns} is unavailable."; return; }
        try
        {
            var capabilities = File.ReadLines("/proc/self/status").Single(line => line.StartsWith("CapEff:", StringComparison.Ordinal));
            var mask = ulong.Parse(capabilities.Split(':')[1].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if ((mask & (1UL << 21)) == 0) { Skip = "CAP_SYS_ADMIN is unavailable for entering the owned Python network namespace."; return; }
            var device = Environment.GetEnvironmentVariable("DOTRETICULUM_AUTO_DEVICE");
            var local = new AutoPacketNetwork().GetDevices().FirstOrDefault(candidate => candidate.Name == device);
            if (local is null) { Skip = $"Owned device {device} has no usable IPv6 link-local address/index."; return; }
            if (!File.Exists(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON")) ||
                !File.Exists(Environment.GetEnvironmentVariable("DOTRETICULUM_AUTO_IP")))
            { Skip = "The configured Python or ip executable is unavailable."; return; }
            using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            var options = new AutoPacketInterfaceOptions { GroupId = "dotreticulum-auto-capability" };
            probe.Bind(new IPEndPoint(new IPAddress(local.Address.GetAddressBytes(), local.Index), 0));
            probe.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, local.Index);
            probe.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(options.GetMulticastAddress(), local.Index));
            probe.SendTo(options.GetDiscoveryToken(local.Address),
                new IPEndPoint(new IPAddress(options.GetMulticastAddress().GetAddressBytes(), local.Index), options.DiscoveryPort));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SocketException or
            PlatformNotSupportedException or System.Net.NetworkInformation.NetworkInformationException)
        { Skip = $"Owned namespace IPv6 capability unavailable: {error.Message}"; }
    }
}
