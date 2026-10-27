using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Tests.Interop;
using Xunit.Abstractions;

namespace DotReticulum.Applications.Tests;

public sealed class LiveLinkTransportFixture : IAsyncLifetime
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rnsd-livelink-" + Guid.NewGuid().ToString("N"));
    private readonly System.Text.StringBuilder _log = new();
    private Task _run = Task.CompletedTask;

    public RnsdHost? Host { get; private set; }
    public PyLinkNode A { get; private set; } = null!;
    public PyLinkNode B { get; private set; } = null!;
    public string RnsdLog { get { lock (_log) return _log.ToString(); } }

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1") return;

        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
        }

        var config = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
              enable_transport = yes
            [interfaces]
              [[Server]]
                type = TCPServerInterface
                enabled = yes
                listen_ip = 127.0.0.1
                listen_port = {port}
            """);
        Host = RnsdHost.Create(config, Path.Combine(_dir, "storage", "transport_identity"), (level, message) => { lock (_log) _log.AppendLine($"[{level}] {message}"); });
        Host.Router.Log = message => { lock (_log) _log.AppendLine(message); };
        await Host.StartAsync(_cts.Token);
        _run = Host.RunAsync(_cts.Token);
        A = await PyLinkNode.StartAsync("a", rnsdPort: port);
        B = await PyLinkNode.StartAsync("b", rnsdPort: port);
        await Task.Delay(1000);
    }

    public async Task DisposeAsync()
    {
        if (A is not null) await A.DisposeAsync();
        if (B is not null) await B.DisposeAsync();
        await _cts.CancelAsync();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        if (Host is not null) await Host.DisposeAsync();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

// LIVE: Python A <-> .NET rnsd (transport, link table) <-> Python B. Link establishment, data, keepalive, teardown and multi-MB Resources
// all cross the .NET hop. Skipped unless DOTRETICULUM_LIVE=1:
//   dotnet test tests/DotReticulum.Applications.Tests -c Release --filter "FullyQualifiedName~LiveLinkTransport"
[Trait("Category", "Integration")]
public sealed class LiveLinkTransportTests(LiveLinkTransportFixture live, ITestOutputHelper output) : IClassFixture<LiveLinkTransportFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Trait("Category", "Live")]
    [LiveFact]
    public async Task PythonChannelAndBuffer_ThroughDotNetTransport()
    {
        try
        {
            await live.B.Command("announce", "announced", Patience);
            await live.A.WaitForAnnounce(live.B.Hash, Patience);
            var bUp = live.B.Count("link_up");
            await live.A.Command($"connect {live.B.HashHex}", "link_up", Patience);
            await live.B.NextAsync("link_up", bUp, Patience);

            var before = live.B.Count("channel_msg");
            var burst = await live.A.Command("chburst 80 hop-", "channel_burst_done", Patience);
            await live.B.NextAsync("channel_msg", before + 79, Patience);
            var texts = live.B.Events("channel_msg").Skip(before).Select(e => e.GetProperty("text").GetString()).ToList();
            Assert.Equal(Enumerable.Range(0, 80).Select(i => "hop-" + i), texts);
            output.WriteLine($"80 channel messages A->B in order across the .NET hop; sender window {burst.GetProperty("window")}");

            var doneBefore = live.B.Count("buffer_done");
            var sent = await live.A.Command("bufsend 400000 seed-hop random 1", "buffer_sent", TimeSpan.FromMinutes(2));
            var done = await live.B.NextAsync("buffer_done", doneBefore, TimeSpan.FromMinutes(2));
            Assert.Equal(sent.GetProperty("sha256").GetString(), done.GetProperty("sha256").GetString());
            Assert.Equal(400_000, done.GetProperty("size").GetInt32());
            output.WriteLine("Buffer stream A->B 400000 B sha256 identical across the .NET hop");
        }
        catch (Exception ex) when (ex is TimeoutException or Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"{ex.Message}\n--- rnsd log ---\n{live.RnsdLog}\n--- A ---\n{live.A.Log}\n--- B ---\n{live.B.Log}");
        }
    }

    [Trait("Category", "Live")]
    [LiveFact]
    public async Task PythonLink_ThroughDotNetTransport_DataKeepaliveResourcesAndTeardown()
    {
        try
        {
            await live.B.Command("announce", "announced", Patience);
            await live.A.WaitForAnnounce(live.B.Hash, Patience);

            var bUp = live.B.Count("link_up");
            var aUp = await live.A.Command($"connect {live.B.HashHex}", "link_up", Patience);
            var bUpEvent = await live.B.NextAsync("link_up", bUp, Patience);
            Assert.Equal("initiator", aUp.GetProperty("role").GetString());
            Assert.Equal("responder", bUpEvent.GetProperty("role").GetString());
            Assert.Equal(aUp.GetProperty("link_id").GetString(), bUpEvent.GetProperty("link_id").GetString());
            var aStat = await live.A.Command("stat", "stat", Patience);
            output.WriteLine($"Link {aUp.GetProperty("link_id")} established across the .NET hop, RTT A {aStat.GetProperty("rtt")} s");

            // data both ways
            var bData = live.B.Count("data");
            var sent = await live.A.Command($"send {Convert.ToHexStringLower("A to B"u8)}", "sent", Patience);
            Assert.True(sent.GetProperty("ok").GetBoolean());
            Assert.Equal(Convert.ToHexStringLower("A to B"u8), (await live.B.NextAsync("data", bData, Patience)).GetProperty("payload").GetString());
            Assert.True(sent.GetProperty("delivered").GetBoolean(), "proof from B did not return through the .NET link table");
            var aData = live.A.Count("data");
            await live.B.Command($"send {Convert.ToHexStringLower("B to A"u8)}", "sent", Patience);
            Assert.Equal(Convert.ToHexStringLower("B to A"u8), (await live.A.NextAsync("data", aData, Patience)).GetProperty("payload").GetString());

            // resource A -> B
            var bDone = live.B.Count("resource_done");
            var aSent = await live.A.Command("resource 2600000 seed-ab random meta-ab", "resource_sent", TimeSpan.FromMinutes(3));
            var bRes = await live.B.NextAsync("resource_done", bDone, TimeSpan.FromMinutes(1));
            Assert.Equal(0x06, bRes.GetProperty("status").GetInt32());
            Assert.Equal(aSent.GetProperty("sha256").GetString(), bRes.GetProperty("sha256").GetString());
            output.WriteLine($"Resource A->B 2.6 MB identical via .NET hop; segments={bRes.GetProperty("segments")}");

            // resource B -> A
            var aDone = live.A.Count("resource_done");
            var bSent = await live.B.Command("resource 1800000 seed-ba text", "resource_sent", TimeSpan.FromMinutes(3));
            var aRes = await live.A.NextAsync("resource_done", aDone, TimeSpan.FromMinutes(1));
            Assert.Equal(bSent.GetProperty("sha256").GetString(), aRes.GetProperty("sha256").GetString());
            output.WriteLine($"Resource B->A 1.8 MB (text, compressed={aRes.GetProperty("compressed")}) identical via .NET hop");

            // request/response across the hop
            var resp = await live.A.Command("request /file 300000:seed-req:random", "response", TimeSpan.FromMinutes(2));
            Assert.True(resp.GetProperty("ok").GetBoolean());
            Assert.Equal(300_000, resp.GetProperty("size").GetInt32());

            // keepalive across the hop (link table must keep forwarding the 0xFF/0xFE traffic)
            await Task.Delay(TimeSpan.FromSeconds(14));
            var idle = await live.A.Command("stat", "stat", Patience);
            Assert.Equal(2, idle.GetProperty("status").GetInt32());
            var idleB = await live.B.Command("stat", "stat", Patience);
            Assert.Equal(2, idleB.GetProperty("status").GetInt32());
            output.WriteLine($"After 14 s idle both Active (A inbound age {idle.GetProperty("last_inbound_age")}, B {idleB.GetProperty("last_inbound_age")})");

            // teardown by A propagates to B
            var bClosed = live.B.Count("link_closed");
            await live.A.SendLineAsync("close");
            var closed = await live.B.NextAsync("link_closed", bClosed, Patience);
            output.WriteLine($"Teardown by A seen by B, reason {closed.GetProperty("reason")}");
        }
        catch (Exception ex) when (ex is TimeoutException or Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"{ex.Message}\n--- rnsd log ---\n{live.RnsdLog}\n--- A ---\n{live.A.Log}\n--- B ---\n{live.B.Log}");
        }
    }
}
