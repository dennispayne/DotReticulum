using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Transport;
using Xunit.Abstractions;

namespace DotReticulum.Applications.Tests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1")
        {
            Skip = "Live Python interop is opt-in: set DOTRETICULUM_LIVE=1 (plus DOTRETICULUM_PYTHON / DOTRETICULUM_UPSTREAM).";
        }
    }
}

public sealed class InProcessLiveFactAttribute : FactAttribute
{
    public InProcessLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1")
            Skip = "Live Python interop is opt-in: set DOTRETICULUM_LIVE=1 (plus DOTRETICULUM_PYTHON / DOTRETICULUM_UPSTREAM).";
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTRETICULUM_RNSD_EXE")))
            Skip = "Registering a local destination requires the in-process host, not DOTRETICULUM_RNSD_EXE.";
    }
}

/// <summary>A real upstream Python RNS leaf node (tests/Interop/live_transport_node.py) connected to rnsd by TCP.</summary>
public sealed class PyNode : IAsyncDisposable
{
    private readonly ConcurrentQueue<JsonElement> _events = new();
    private readonly StringBuilder _log = new();
    private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _configDir = Path.Combine(AppContext.BaseDirectory, "live-rns-" + Guid.NewGuid().ToString("N"));
    private Process _process = null!;
    private readonly List<Task> _pumps = [];

    public string Name { get; }
    public byte[] Hash { get; private set; } = [];
    public string HashHex => Convert.ToHexStringLower(Hash);
    public string Log { get { lock (_log) return _log.ToString(); } }

    private PyNode(string name) => Name = name;

    public static async Task<PyNode> StartAsync(string name, int rnsdPort)
    {
        var node = new PyNode(name);
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { FindHarness(), "--config", node._configDir, "--rnsd-port", rnsdPort.ToString(), "--name", name }) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONPATH"] = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")
            ?? throw new InvalidOperationException("DOTRETICULUM_UPSTREAM must point at the pinned upstream checkout.");
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        node._process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start python.");
        node._pumps.Add(Task.Run(() => node.PumpStdout()));
        node._pumps.Add(Task.Run(() => node.PumpStderr()));
        var ready = await node._ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
        node.Hash = Convert.FromHexString(ready.GetProperty("dest").GetString()!);
        return node;
    }

    public async Task<JsonElement> WaitForEvent(Func<JsonElement, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var e in _events) if (match(e)) return e;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Python node {Name}: event not seen in time. Output:\n{Log}");
    }

    public Task<JsonElement> WaitForAnnounce(byte[] hash, TimeSpan timeout) =>
        WaitForEvent(e => e.GetProperty("event").GetString() == "announce" && e.GetProperty("dest").GetString() == Convert.ToHexStringLower(hash), timeout);

    public async Task<JsonElement> Command(string line, string expectedEvent, TimeSpan timeout)
    {
        var before = _events.Count(e => e.GetProperty("event").GetString() == expectedEvent);
        await _process.StandardInput.WriteLineAsync(line);
        await _process.StandardInput.FlushAsync();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = _events.Where(e => e.GetProperty("event").GetString() == expectedEvent).ToList();
            if (found.Count > before) return found[^1];
            await Task.Delay(50);
        }

        throw new TimeoutException($"Python node {Name}: no '{expectedEvent}' for '{line}'. Output:\n{Log}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                try { await _process.StandardInput.WriteLineAsync("quit"); _process.StandardInput.Close(); }
                catch (IOException) { }
                if (!_process.WaitForExit(10_000)) _process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            foreach (var t in _pumps)
            {
                try { await t.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { }
            }

            _process.Dispose();
            if (Directory.Exists(_configDir)) Directory.Delete(_configDir, recursive: true);
        }
    }

    private static string FindHarness()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Interop", "live_transport_node.py");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("tests/Interop/live_transport_node.py not found above the test output directory.");
    }

    private async Task PumpStdout()
    {
        string? line;
        while ((line = await _process.StandardOutput.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(line[2..]);
                var root = doc.RootElement.Clone();
                if (root.GetProperty("event").GetString() == "ready") _ready.TrySetResult(root);
                _events.Enqueue(root);
            }

            lock (_log) _log.AppendLine(line);
        }

        _ready.TrySetException(new InvalidOperationException($"Python node {Name} exited before ready. Output:\n{Log}"));
    }

    private async Task PumpStderr()
    {
        string? line;
        while ((line = await _process.StandardError.ReadLineAsync()) is not null)
        {
            lock (_log) _log.AppendLine("[stderr] " + line);
        }
    }
}

/// <summary>.NET rnsd transport node (real RnsdHost, real TCP) with Python nodes A and B attached.</summary>
public sealed class LiveTransportFixture : IAsyncLifetime
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rnsd-live-" + Guid.NewGuid().ToString("N"));
    private Task _run = Task.CompletedTask;
    private readonly StringBuilder _rnsdLog = new();

    private System.Diagnostics.Process? _external;
    public static string? ExternalExe => Environment.GetEnvironmentVariable("DOTRETICULUM_RNSD_EXE");

    public RnsdHost? Host { get; private set; }
    public int Port { get; private set; }
    public PyNode A { get; private set; } = null!;
    public PyNode B { get; private set; } = null!;
    public string RnsdLog { get { lock (_rnsdLog) return _rnsdLog.ToString(); } }

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1") return;

        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
        }

        var config = RnsdConfigParser.Parse($"""
            [reticulum]
              enable_transport = yes
            [interfaces]
              [[Server]]
                type = TCPServerInterface
                listen_ip = 127.0.0.1
                listen_port = {Port}
            """);
        if (ExternalExe is { } exe)
        {
            // Run the real (e.g. NativeAOT) rnsd binary instead of the in-process host.
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "config"), $"[reticulum]\n  enable_transport = yes\n[logging]\n  loglevel = 6\n[interfaces]\n  [[Server]]\n    type = TCPServerInterface\n    listen_ip = 127.0.0.1\n    listen_port = {Port}\n");
            var psi = new System.Diagnostics.ProcessStartInfo(exe, ["rnsd", "--config", _dir]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            _external = System.Diagnostics.Process.Start(psi)!;
            _external.OutputDataReceived += (_, e) => { lock (_rnsdLog) _rnsdLog.AppendLine(e.Data); };
            _external.BeginOutputReadLine();
            _external.BeginErrorReadLine();
            await Task.Delay(2000);
        }
        else
        {
            Host = RnsdHost.Create(config, Path.Combine(_dir, "storage", "transport_identity"), (level, message) => { lock (_rnsdLog) _rnsdLog.AppendLine($"[{level}] {message}"); });
            Host.Router.Log = message => { lock (_rnsdLog) _rnsdLog.AppendLine(message); };
            await Host.StartAsync(_cts.Token);
            _run = Host.RunAsync(_cts.Token);
        }
        A = await PyNode.StartAsync("a", Port);
        B = await PyNode.StartAsync("b", Port);
        await Task.Delay(1000);
    }

    public async Task DisposeAsync()
    {
        if (A is not null) await A.DisposeAsync();
        if (B is not null) await B.DisposeAsync();
        await _cts.CancelAsync();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        if (Host is not null) await Host.DisposeAsync();
        if (_external is { HasExited: false }) { _external.Kill(entireProcessTree: true); _external.Dispose(); }
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

// LIVE: Python A <-TCP-> .NET rnsd (transport) <-TCP-> Python B. Skipped unless DOTRETICULUM_LIVE=1.
//   $env:DOTRETICULUM_LIVE='1'; $env:DOTRETICULUM_PYTHON='<python>'; $env:DOTRETICULUM_UPSTREAM='<pinned checkout>'
//   dotnet test tests/DotReticulum.Applications.Tests -c Release --filter "FullyQualifiedName~LiveTransport"
public sealed class LiveTransportTests(LiveTransportFixture live, ITestOutputHelper output) : IClassFixture<LiveTransportFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly byte[] Message = "routed through dotnet"u8.ToArray();

    private async Task<T> WithLog<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is TimeoutException or Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"{ex.Message}\n--- rnsd log ---\n{live.RnsdLog}");
        }
    }

    [LiveFact]
    public async Task BAnnounce_IsRebroadcastByDotNet_AndALearnsPathWithTwoHops()
    {
        await WithLog(async () =>
        {
            await live.B.Command("announce", "announced", Patience);
            var seen = await live.A.WaitForAnnounce(live.B.Hash, Patience);
            Assert.Equal(2, seen.GetProperty("hops").GetInt32());
            var path = await live.A.Command($"path {live.B.HashHex}", "path", Patience);
            Assert.True(path.GetProperty("has_path").GetBoolean());
            Assert.Equal(2, path.GetProperty("hops").GetInt32());
            if (live.Host is not null) Assert.Equal(1, live.Host.Router.HopsTo(live.B.Hash));
            output.WriteLine($"A learned path to B via .NET: {path}");
            return 0;
        });
    }

    [LiveFact]
    public async Task AEncryptedPacketToB_IsRoutedByDotNet_BDecrypts_AndProofReturnsViaReverseTable()
    {
        await WithLog(async () =>
        {
            await live.B.Command("announce", "announced", Patience);
            await live.A.WaitForAnnounce(live.B.Hash, Patience);
            var sent = await live.A.Command($"send {live.B.HashHex} dotreticulum.interop.p05b {Convert.ToHexStringLower(Message)}", "sent", TimeSpan.FromSeconds(40));
            Assert.True(sent.GetProperty("ok").GetBoolean());
            var received = await live.B.WaitForEvent(e => e.GetProperty("event").GetString() == "packet" && e.GetProperty("payload").GetString() == Convert.ToHexStringLower(Message), Patience);
            Assert.Equal(2, received.GetProperty("hops").GetInt32());
            Assert.True(sent.GetProperty("proved").GetBoolean(), "A never received B's proof back through the .NET reverse table");
            output.WriteLine($"B decrypted: {received}; A proof validated: {sent}");
            return 0;
        });
    }

    [LiveFact]
    public async Task PathRequestFromLateJoiner_IsAnsweredByDotNetFromItsPathTable()
    {
        await WithLog(async () =>
        {
            await live.B.Command("announce", "announced", Patience);
            await live.A.WaitForAnnounce(live.B.Hash, Patience);
            await using var c = await PyNode.StartAsync("c", live.Port);
            await Task.Delay(1000);
            var before = await c.Command($"path {live.B.HashHex}", "path", Patience);
            Assert.False(before.GetProperty("has_path").GetBoolean());
            var after = await c.Command($"request_path {live.B.HashHex} 15", "path", TimeSpan.FromSeconds(30));
            Assert.True(after.GetProperty("has_path").GetBoolean(), "path request was not answered");
            Assert.Equal(2, after.GetProperty("hops").GetInt32());
            output.WriteLine($"C before: {before}; after path request: {after}");
            return 0;
        });
    }

    [InProcessLiveFact]
    public async Task DotNetAnnounce_ReachesBothPythonNodes_AndPythonEncryptsToIt()
    {
        await WithLog(async () =>
        {
            Assert.NotNull(live.Host);
            var identity = Identity.Generate();
            var destination = Destination.Create(DestinationType.Single, "dotreticulum", ["interop", "p05dotnet"], identity);
            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var local = new LocalDestination(identity, destination)
            {
                AppData = "dotnet-p05"u8.ToArray(),
                PacketReceived = r =>
                {
                    var plain = identity.Decrypt(r.Packet.Payload.Span);
                    received.TrySetResult(plain!);
                    return ValueTask.CompletedTask;
                },
            };
            live.Host.Router.RegisterLocalDestination(local);
            await live.Host.Router.AnnounceAsync(local);

            var onA = await live.A.WaitForAnnounce(destination.Hash, Patience);
            var onB = await live.B.WaitForAnnounce(destination.Hash, Patience);
            Assert.Equal(1, onA.GetProperty("hops").GetInt32());
            Assert.Equal(1, onB.GetProperty("hops").GetInt32());
            Assert.Equal(Convert.ToHexStringLower("dotnet-p05"u8), onA.GetProperty("app_data").GetString());

            var sent = await live.A.Command($"send {Convert.ToHexStringLower(destination.Hash)} dotreticulum.interop.p05dotnet {Convert.ToHexStringLower(Message)}", "sent", TimeSpan.FromSeconds(25));
            Assert.True(sent.GetProperty("recalled").GetBoolean());
            Assert.Equal(Message, await received.Task.WaitAsync(Patience));
            output.WriteLine($"A and B accepted the .NET announce ({onA}, {onB}); .NET decrypted A's packet");
            return 0;
        });
    }
}
