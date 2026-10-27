using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using Xunit.Abstractions;
using static DotReticulum.Interfaces.Tests.TestSupport;

namespace DotReticulum.Interfaces.Tests;

// LIVE interop against the Reticulum-hosted Python snapshot pinned in tests/Interop/reference_environment.py
// driven by tests/Interop/live_interfaces_harness.py. Skipped unless DOTRETICULUM_LIVE=1.
//   DOTRETICULUM_PYTHON    python executable with the harness' requirements (default: python)
//   DOTRETICULUM_UPSTREAM  checkout of the pinned upstream source; put on PYTHONPATH for the harness
// Example (PowerShell):
//   $env:DOTRETICULUM_LIVE='1'; $env:DOTRETICULUM_PYTHON='<venv>\Scripts\python.exe'; $env:DOTRETICULUM_UPSTREAM='<clone>'
//   dotnet test tests/DotReticulum.Interfaces.Tests -c Release --filter "FullyQualifiedName~LiveInterop"
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

public sealed class LiveHarness : IAsyncLifetime
{
    private readonly ConcurrentQueue<JsonElement> _events = new();
    private readonly StringBuilder _log = new();
    private readonly CancellationTokenSource _cts = new();
    private Process? _python;
    private string? _configDir;
    private readonly List<Task> _background = [];
    private TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<ReceivedPacket> FromClientSide { get; } = new();
    public ConcurrentQueue<ReceivedPacket> FromServerSide { get; } = new();
    public TcpServerPacketInterface DotNetTcpServer { get; private set; } = null!;
    public UdpPacketInterface DotNetUdp { get; private set; } = null!;
    public TcpClientPacketInterface DotNetTcpClient { get; private set; } = null!;
    public JsonElement Ready { get; private set; }
    public PacketInterfaceManager? ServerSide { get; private set; }
    public PacketInterfaceManager? ClientSide { get; private set; }

    public byte[] DestHash => Convert.FromHexString(Ready.GetProperty("dest_hash").GetString()!);
    public byte[] PlainHash => Convert.FromHexString(Ready.GetProperty("plain_hash").GetString()!);

    public string Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    public IEnumerable<JsonElement> Events => _events;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1")
        {
            return;
        }

        var pythonExe = Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python";
        var upstream = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")
            ?? throw new InvalidOperationException("DOTRETICULUM_UPSTREAM must point at the pinned upstream checkout.");
        var harness = FindHarness();

        var udpDotNet = FreeUdpPort();
        var udpPython = FreeUdpPort();
        var pythonTcpPort = FreeTcpPort();

        DotNetTcpServer = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0));
        DotNetUdp = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, udpDotNet), new IPEndPoint(IPAddress.Loopback, udpPython));
        ServerSide = new PacketInterfaceManager([DotNetTcpServer, DotNetUdp]);
        await ServerSide.StartAsync();
        _background.Add(Collect(ServerSide, FromServerSide));

        _configDir = Path.Combine(AppContext.BaseDirectory, "live-rns-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo(pythonExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[]
        {
            harness, "--config", _configDir,
            "--tcp-server-port", pythonTcpPort.ToString(),
            "--dotnet-tcp-port", DotNetTcpServer.LocalEndpoint!.Port.ToString(),
            "--udp-listen-port", udpPython.ToString(),
            "--udp-forward-port", udpDotNet.ToString(),
        })
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["PYTHONPATH"] = upstream;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        _python = Process.Start(psi) ?? throw new InvalidOperationException("Could not start python.");
        _background.Add(Task.Run(() => PumpStdout(_python)));
        _background.Add(Task.Run(() => PumpStderr(_python)));

        Ready = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(60));

        DotNetTcpClient = new TcpClientPacketInterface("127.0.0.1", pythonTcpPort);
        ClientSide = new PacketInterfaceManager([DotNetTcpClient]);
        await ClientSide.StartAsync();
        _background.Add(Collect(ClientSide, FromClientSide));
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_python is { HasExited: false })
            {
                try
                {
                    await _python.StandardInput.WriteLineAsync("quit");
                    _python.StandardInput.Close();
                }
                catch (IOException)
                {
                }

                if (!_python.WaitForExit(10_000))
                {
                    _python.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            await _cts.CancelAsync();
            if (ClientSide is not null) await ClientSide.DisposeAsync();
            if (ServerSide is not null) await ServerSide.DisposeAsync();
            await Task.WhenAll(_background.Select(async t =>
            {
                try
                {
                    await t.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Pump tasks end when the process or manager goes away.
                }
            }));
            _python?.Dispose();
            if (_configDir is not null && Directory.Exists(_configDir))
            {
                Directory.Delete(_configDir, recursive: true);
            }
        }
    }

    public async Task<JsonElement> WaitForEvent(Func<JsonElement, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var e in _events)
            {
                if (match(e))
                {
                    return e;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Python event not seen in time. Python output:\n" + Log);
    }

    public async Task<JsonElement> RequestStats()
    {
        var before = _events.Count(e => e.GetProperty("event").GetString() == "stats");
        await _python!.StandardInput.WriteLineAsync("stats");
        await _python.StandardInput.FlushAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var stats = _events.Where(e => e.GetProperty("event").GetString() == "stats").ToList();
            if (stats.Count > before)
            {
                return stats[^1];
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("No stats from Python.");
    }

    private static string FindHarness()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Interop", "live_interfaces_harness.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("tests/Interop/live_interfaces_harness.py not found above the test output directory.");
    }

    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private Task Collect(PacketInterfaceManager manager, ConcurrentQueue<ReceivedPacket> sink) => Task.Run(async () =>
    {
        try
        {
            await foreach (var packet in manager.ReceiveWithSourceAsync(_cts.Token))
            {
                sink.Enqueue(packet);
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

    private async Task PumpStdout(Process p)
    {
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync()) is not null)
        {
            lock (_log)
            {
                _log.AppendLine(line);
            }

            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            var json = JsonDocument.Parse(line[2..]).RootElement.Clone();
            if (json.GetProperty("event").GetString() == "ready")
            {
                _ready.TrySetResult(json);
            }

            _events.Enqueue(json);
        }

        _ready.TrySetException(new InvalidOperationException("Python exited before becoming ready. Output:\n" + Log));
    }

    private async Task PumpStderr(Process p)
    {
        string? line;
        while ((line = await p.StandardError.ReadLineAsync()) is not null)
        {
            lock (_log)
            {
                _log.AppendLine("[stderr] " + line);
            }
        }
    }
}

[Trait("Category", "Integration")]
public class LiveInteropTests(LiveHarness harness, ITestOutputHelper output) : IClassFixture<LiveHarness>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private async Task<ReceivedPacket> WaitForAnnounce(System.Collections.Concurrent.ConcurrentQueue<ReceivedPacket> queue, IPacketInterface source)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var r in queue)
            {
                if (ReferenceEquals(r.Source, source) && r.Packet.PacketType == PacketType.Announce
                    && r.Packet.DestinationHash.Span.SequenceEqual(harness.DestHash))
                {
                    return r;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("No Python announce received. Python output:\n" + harness.Log);
    }

    private void AssertIsPythonAnnounce(ReceivedPacket received)
    {
        var p = received.Packet;
        output.WriteLine($"announce via {received.Source.GetType().Name}: dest={Convert.ToHexString(p.DestinationHash.Span).ToLowerInvariant()} " +
                         $"len={p.Length} hops={p.Hops} header={p.HeaderType} ctxflag={p.ContextFlag} payload={p.Payload.Length}B");
        Assert.Equal(DestinationType.Single, p.DestinationType);
        Assert.Equal(HeaderType.Header1, p.HeaderType);
        Assert.False(p.ContextFlag);
        Assert.True(p.Hops <= 1);

        // payload = public_key(64) || name_hash(10) || random_hash(10) || signature(64) || app_data
        var payload = p.Payload.Span;
        var appData = Convert.FromHexString(harness.Ready.GetProperty("app_data").GetString()!);
        Assert.Equal(64 + 10 + 10 + 64 + appData.Length, payload.Length);
        Assert.Equal(harness.Ready.GetProperty("name_hash").GetString(), Convert.ToHexString(payload.Slice(64, 10)).ToLowerInvariant());
        Assert.True(payload[(64 + 10 + 10 + 64)..].SequenceEqual(appData));

        // The destination hash must be derivable from the announced public key and name hash alone.
        var identityHash = SHA256.HashData(payload[..64])[..16];
        var derived = SHA256.HashData([.. payload.Slice(64, 10), .. identityHash])[..16];
        Assert.True(derived.AsSpan().SequenceEqual(p.DestinationHash.Span));
    }

    [Trait("Category", "Live")]
    [LiveFact]
    public async Task TcpClient_ReceivesRealPythonAnnounces_FromPythonTcpServerInterface()
    {
        var announce = await WaitForAnnounce(harness.FromClientSide, harness.DotNetTcpClient);
        AssertIsPythonAnnounce(announce);
    }

    [Trait("Category", "Live")]
    [LiveFact]
    public async Task TcpServer_ReceivesRealPythonAnnounces_FromPythonTcpClientInterface()
    {
        var announce = await WaitForAnnounce(harness.FromServerSide, harness.DotNetTcpServer);
        AssertIsPythonAnnounce(announce);
        Assert.True(harness.DotNetTcpServer.ClientCount >= 1);
    }

    [Trait("Category", "Live")]
    [LiveFact]
    public async Task Udp_ReceivesRealPythonAnnounces_FromPythonUdpInterface()
    {
        var announce = await WaitForAnnounce(harness.FromServerSide, harness.DotNetUdp);
        AssertIsPythonAnnounce(announce);
    }

    private async Task AssertPythonReceives(IPacketInterface via, string expectedInterfaceName, string label)
    {
        var payload = Encoding.ASCII.GetBytes($"dotnet-to-python:{label}:{Guid.NewGuid():N}");
        var packet = Packet.Create(PacketType.Data, DestinationType.Plain, harness.PlainHash, payload);
        await via.SendAsync(packet.ToArray(), default);
        var seen = await harness.WaitForEvent(
            e => e.GetProperty("event").GetString() == "packet"
                 && e.GetProperty("payload").GetString() == Convert.ToHexString(payload).ToLowerInvariant(),
            Patience);
        output.WriteLine($"python received .NET packet via {label}: interface='{seen.GetProperty("interface").GetString()}' hops={seen.GetProperty("hops").GetInt32()}");
        Assert.Contains(expectedInterfaceName, seen.GetProperty("interface").GetString());
    }

    [Trait("Category", "Live")]
    [LiveFact]
    public async Task Python_ReceivesDotNetPacket_OverTcpClient()
    {
        await WaitForAnnounce(harness.FromClientSide, harness.DotNetTcpClient);
        await AssertPythonReceives(harness.DotNetTcpClient, "Client on PyTCPServer", "tcp-client");
    }

    [Trait("Category", "Live")]
    [LiveFact]
    public async Task Python_ReceivesDotNetPacket_OverTcpServer()
    {
        await WaitForAnnounce(harness.FromServerSide, harness.DotNetTcpServer);
        await AssertPythonReceives(harness.DotNetTcpServer, "PyTCPClient", "tcp-server");
    }

    [Trait("Category", "Live")]
    [LiveFact]
    public async Task Python_ReceivesDotNetPacket_OverUdp()
    {
        await WaitForAnnounce(harness.FromServerSide, harness.DotNetUdp);
        await AssertPythonReceives(harness.DotNetUdp, "PyUDP", "udp");
    }

    [Trait("Category", "Live")]
    [LiveFact]
    public async Task PythonInterfaceCountersShowTrafficOnAllThreeBearers()
    {
        await WaitForAnnounce(harness.FromClientSide, harness.DotNetTcpClient);
        await WaitForAnnounce(harness.FromServerSide, harness.DotNetTcpServer);
        await WaitForAnnounce(harness.FromServerSide, harness.DotNetUdp);
        await AssertPythonReceives(harness.DotNetTcpClient, "Client on PyTCPServer", "stats-tcp-client");
        await AssertPythonReceives(harness.DotNetTcpServer, "PyTCPClient", "stats-tcp-server");
        await AssertPythonReceives(harness.DotNetUdp, "PyUDP", "stats-udp");
        var stats = (await harness.RequestStats()).GetProperty("interfaces");
        output.WriteLine("python interface counters: " + stats.GetRawText());
        long Rx(string name) => stats.EnumerateObject().Where(p => p.Name.Contains(name)).Sum(p => p.Value.GetProperty("rxb").GetInt64());
        long Tx(string name) => stats.EnumerateObject().Where(p => p.Name.Contains(name)).Sum(p => p.Value.GetProperty("txb").GetInt64());
        Assert.True(Rx("PyUDP") > 0);
        Assert.True(Rx("PyTCPClient") > 0);
        Assert.True(Rx("Client on PyTCPServer") > 0);
        Assert.True(Tx("PyUDP") > 0);
        Assert.True(Tx("PyTCPClient") > 0);
        Assert.True(Tx("Client on PyTCPServer") > 0);
    }
}
