using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using Xunit.Abstractions;

namespace DotReticulum.Transport.Tests;

// LIVE interop against the Reticulum-hosted Python snapshot pinned in tests/Interop/reference_environment.py, driven by
// tests/Interop/live_announce_harness.py. Skipped unless DOTRETICULUM_LIVE=1.
//   DOTRETICULUM_PYTHON    python executable (default: python)
//   DOTRETICULUM_UPSTREAM  checkout of the pinned upstream source; put on PYTHONPATH for the harness
//   $env:DOTRETICULUM_LIVE='1'; dotnet test tests/DotReticulum.Transport.Tests -c Release --filter "FullyQualifiedName~LiveAnnounce"
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

public sealed class LiveAnnounceFixture : IAsyncLifetime
{
    private readonly ConcurrentQueue<JsonElement> _events = new();
    private readonly StringBuilder _log = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _background = [];
    private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _python;
    private string? _configDir;

    public AnnounceEngine Engine { get; } = new();
    public PacketInterfaceManager Manager { get; private set; } = null!;
    public ConcurrentQueue<Packet> DataPackets { get; } = new();
    public ConcurrentQueue<string> AnnounceOutcomes { get; } = new();
    public JsonElement Ready { get; private set; }
    public IEnumerable<JsonElement> Events => _events;

    public byte[] RatchetHash => Convert.FromHexString(Ready.GetProperty("ratchet_hash").GetString()!);
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

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1")
        {
            return;
        }

        var pythonExe = Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python";
        var upstream = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")
            ?? throw new InvalidOperationException("DOTRETICULUM_UPSTREAM must point at the pinned upstream checkout.");
        var port = FreeTcpPort();
        _configDir = Path.Combine(AppContext.BaseDirectory, "live-rns-" + Guid.NewGuid().ToString("N"));

        var psi = new ProcessStartInfo(pythonExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { FindHarness(), "--config", _configDir, "--tcp-server-port", port.ToString() })
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["PYTHONPATH"] = upstream;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        _python = Process.Start(psi) ?? throw new InvalidOperationException("Could not start python.");
        _background.Add(Task.Run(() => PumpStdout(_python)));
        _background.Add(Task.Run(() => PumpStderr(_python)));
        Ready = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(60));

        Manager = new PacketInterfaceManager([new TcpClientPacketInterface("127.0.0.1", port)]);
        await Manager.StartAsync();
        var processor = new AnnounceProcessor(Manager, Engine)
        {
            AnnounceProcessed = (r, result) => AnnounceOutcomes.Enqueue($"{Convert.ToHexStringLower(r.Packet.DestinationHash.Span)} {result.Outcome}"),
            PacketReceived = r =>
            {
                DataPackets.Enqueue(r.Packet);
                return ValueTask.CompletedTask;
            },
        };
        _background.Add(Task.Run(async () =>
        {
            try
            {
                await processor.RunAsync(_cts.Token);
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                lock (_log)
                {
                    _log.AppendLine("[processor faulted] " + ex);
                }
            }
        }));
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
            if (Manager is not null)
            {
                await Manager.DisposeAsync();
            }

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

    public async Task<JsonElement> Command(string line, string expectedEvent)
    {
        var before = _events.Count(e => e.GetProperty("event").GetString() == expectedEvent);
        await _python!.StandardInput.WriteLineAsync(line);
        await _python.StandardInput.FlushAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var found = _events.Where(e => e.GetProperty("event").GetString() == expectedEvent).ToList();
            if (found.Count > before)
            {
                return found[^1];
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"No '{expectedEvent}' from Python. Output:\n{Log}");
    }

    private static int FreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static string FindHarness()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Interop", "live_announce_harness.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("tests/Interop/live_announce_harness.py not found above the test output directory.");
    }

    private async Task PumpStdout(Process p)
    {
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var doc = JsonDocument.Parse(line[2..]);
                var root = doc.RootElement.Clone();
                if (root.GetProperty("event").GetString() == "ready")
                {
                    _ready.TrySetResult(root);
                }

                _events.Enqueue(root);
            }

            lock (_log)
            {
                _log.AppendLine(line);
            }
        }

        _ready.TrySetException(new InvalidOperationException("Python exited before ready. Output:\n" + Log));
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

public sealed class LiveAnnounceTests(LiveAnnounceFixture live, ITestOutputHelper output) : IClassFixture<LiveAnnounceFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly byte[] Message = "dotnet says hello to python"u8.ToArray();

    [LiveFact]
    public async Task PythonAnnounces_AreValidatedAndRecalled()
    {
        try { await WaitUntil(() => live.Engine.Known.Recall(live.RatchetHash) is not null && live.Engine.Known.Recall(live.PlainHash) is not null); }
        catch (Xunit.Sdk.XunitException) { throw new Xunit.Sdk.XunitException("data packets: " + string.Join("; ", live.DataPackets.Select(p => p.PacketType + " " + Convert.ToHexStringLower(p.DestinationHash.Span))) + " outcomes: " + string.Join("; ", live.AnnounceOutcomes) + "\nlog:\n" + live.Log); }

        Assert.Equal("dotreticulum-p04-ratchet"u8.ToArray(), live.Engine.Known.RecallAppData(live.RatchetHash));
        Assert.Equal("dotreticulum-p04-plain"u8.ToArray(), live.Engine.Known.RecallAppData(live.PlainHash));
        Assert.Equal(32, live.Engine.Known.GetRatchet(live.RatchetHash)!.Length);
        Assert.Null(live.Engine.Known.GetRatchet(live.PlainHash));
        Assert.NotNull(live.Engine.GetPath(live.RatchetHash));
        output.WriteLine($"validated+recalled python announces; ratchet={Convert.ToHexStringLower(live.Engine.Known.GetRatchet(live.RatchetHash)!)}");
    }

    [LiveFact]
    public async Task PythonAnnounceReachesRegisteredHandler_WithAspectFilter()
    {
        var hits = new ConcurrentQueue<AnnounceReceived>();
        var matching = new CallbackAnnounceHandler(null, hits.Enqueue);
        var wrongHits = new ConcurrentQueue<AnnounceReceived>();
        var wrongAspect = new CallbackAnnounceHandler("dotreticulum.interop.other", wrongHits.Enqueue);
        live.Engine.RegisterHandler(matching);
        live.Engine.RegisterHandler(wrongAspect);
        try
        {
            await WaitUntil(() => hits.Any(h => h.DestinationHash.AsSpan().SequenceEqual(live.RatchetHash)));
        }
        finally
        {
            live.Engine.DeregisterHandler(matching);
            live.Engine.DeregisterHandler(wrongAspect);
        }
        Assert.Empty(wrongHits);
    }

    [LiveFact]
    public async Task DotNetEncryptsToPythonRatchet_PythonDecrypts()
    {
        await WaitUntil(() => live.Engine.Known.GetRatchet(live.RatchetHash) is not null);
        var token = live.Engine.Known.Encrypt(live.RatchetHash, Message)!;
        await live.Manager.SendAsync(Packet.Create(PacketType.Data, DestinationType.Single, live.RatchetHash, token));

        var e = await live.WaitForEvent(x => x.GetProperty("event").GetString() == "packet"
            && x.GetProperty("dest").GetString() == Convert.ToHexStringLower(live.RatchetHash), Patience);
        Assert.Equal(Convert.ToHexStringLower(Message), e.GetProperty("payload").GetString());
        output.WriteLine("python (ratchets enforced) decrypted the packet we encrypted to the announced ratchet");
    }

    [LiveFact]
    public async Task DotNetEncryptsToPythonIdentity_PythonDecrypts()
    {
        await WaitUntil(() => live.Engine.Known.Recall(live.PlainHash) is not null);
        var token = live.Engine.Known.Encrypt(live.PlainHash, Message)!;
        await live.Manager.SendAsync(Packet.Create(PacketType.Data, DestinationType.Single, live.PlainHash, token));

        var e = await live.WaitForEvent(x => x.GetProperty("event").GetString() == "packet"
            && x.GetProperty("dest").GetString() == Convert.ToHexStringLower(live.PlainHash), Patience);
        Assert.Equal(Convert.ToHexStringLower(Message), e.GetProperty("payload").GetString());
    }

    [LiveFact]
    public Task DotNetRatchetAnnounce_IsRecordedByPython_AndPythonEncryptsToUs() => RoundTrip(withRatchet: true);

    [LiveFact]
    public Task DotNetPlainAnnounce_IsRecordedByPython_AndPythonEncryptsToUs() => RoundTrip(withRatchet: false);

    private async Task RoundTrip(bool withRatchet)
    {
        var aspect = withRatchet ? "p04dotnetratchet" : "p04dotnetplain";
        using var identity = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "dotreticulum", ["interop", aspect], identity);
        using var ratchets = withRatchet ? new DestinationRatchets { Enforce = true } : null;
        var hash = Convert.ToHexStringLower(destination.Hash);
        var appData = Encoding.UTF8.GetBytes("dotnet-" + aspect);

        // Re-announce (fresh random hash each time) until upstream has accepted one.
        var deadline = DateTime.UtcNow + Patience;
        JsonElement? seen = null;
        while (seen is null && DateTime.UtcNow < deadline)
        {
            await live.Manager.SendAsync(Announce.Create(identity, destination, appData, ratchets));
            try
            {
                seen = await live.WaitForEvent(e => e.GetProperty("event").GetString() == "announce" && e.GetProperty("dest").GetString() == hash, TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
        }

        Assert.True(seen is not null, "python never accepted our announce. Output:\n" + live.Log);
        Assert.Equal(Convert.ToHexStringLower(appData), seen.Value.GetProperty("app_data").GetString());

        var recall = await live.Command($"recall {hash}", "recall");
        Assert.True(recall.GetProperty("known").GetBoolean());
        Assert.Equal(Convert.ToHexStringLower(identity.Hash), recall.GetProperty("identity_hash").GetString());
        Assert.Equal(Convert.ToHexStringLower(appData), recall.GetProperty("app_data").GetString());
        Assert.True(recall.GetProperty("has_path").GetBoolean());
        Assert.Equal(withRatchet ? Convert.ToHexStringLower(ratchets!.CurrentPublicKey!) : "", recall.GetProperty("ratchet").GetString());
        output.WriteLine($"python recall: {recall}");

        var sent = await live.Command($"send dotreticulum.interop.{aspect} {Convert.ToHexStringLower(Message)} {hash}", "sent");
        Assert.True(sent.GetProperty("recalled").GetBoolean());
        Assert.True(sent.GetProperty("ok").GetBoolean());
        Assert.Equal(hash, sent.GetProperty("dest").GetString());

        await WaitUntil(() => live.DataPackets.Any(p => p.DestinationHash.Span.SequenceEqual(destination.Hash)));
        var packet = live.DataPackets.First(p => p.DestinationHash.Span.SequenceEqual(destination.Hash));
        var plaintext = withRatchet ? ratchets!.Decrypt(identity, packet.Payload.Span) : identity.Decrypt(packet.Payload.Span);
        Assert.Equal(Message, plaintext);
        output.WriteLine($"we decrypted the packet python encrypted to our {(withRatchet ? "ratchet" : "identity")}");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(50);
        }
    }
}
