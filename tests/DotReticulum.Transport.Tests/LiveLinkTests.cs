using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Tests.Interop;
using Xunit.Abstractions;

namespace DotReticulum.Transport.Tests;

// LIVE interop against the Reticulum-hosted snapshot pinned in tests/Interop/reference_environment.py via tests/Interop/live_link_node.py.
// Skipped unless DOTRETICULUM_LIVE=1 (plus DOTRETICULUM_PYTHON / DOTRETICULUM_UPSTREAM):
//   dotnet test tests/DotReticulum.Transport.Tests -c Release --filter "FullyQualifiedName~LiveLink"
// One Python process with a TCPServerInterface and a .NET TransportRouter connected to it over real TCP, per test.
[Trait("Category", "Integration")]
public sealed class LiveLinkTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    private sealed class Session : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task _run = Task.CompletedTask;
        public PyLinkNode Py { get; private set; } = null!;
        public PacketInterfaceManager Manager { get; private set; } = null!;
        public TransportRouter Router { get; private set; } = null!;
        public StringBuilder Log { get; } = new();

        public static async Task<Session> StartAsync()
        {
            int port;
            using (var l = new TcpListener(IPAddress.Loopback, 0))
            {
                l.Start();
                port = ((IPEndPoint)l.LocalEndpoint).Port;
            }

            var s = new Session { Py = await PyLinkNode.StartAsync("", tcpServerPort: port) };
            s.Manager = new PacketInterfaceManager([new TcpClientPacketInterface("127.0.0.1", port)]);
            s.Router = new TransportRouter(s.Manager, new TransportRouterOptions { PathRequestGrace = TimeSpan.Zero })
            {
                Log = m => { lock (s.Log) s.Log.AppendLine(m); },
            };
            try
            {
                await s.Manager.StartAsync();
                s._run = Task.Run(() => s.Router.RunAsync(s._cts.Token));
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    var remaining = Patience - clock.Elapsed;
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException("Pinned Python TCP server did not register the actual accepted peer.");
                    var interfaces = await s.Py.Command("interfaces", "interfaces", remaining);
                    if (interfaces.GetProperty("interfaces").EnumerateArray().Any(item =>
                        item.GetProperty("name").GetString() == "Client on PyTCPServer" && item.GetProperty("online").GetBoolean()))
                        return s;
                    await Task.Delay(25);
                }
            }
            catch
            {
                await s.DisposeAsync();
                throw;
            }
        }

        public async Task<(Link Link, TaskCompletionSource<Link> Closed)> ConnectToPythonAsync()
        {
            await Py.Command("announce", "announced", Patience);
            await Until(() => Router.HasPath(Py.Hash), 20);
            var identity = Router.Engine.Known.Recall(Py.Hash)!;
            var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pythonUpBefore = Py.Count("link_up");
            var link = Router.Links.Connect(identity, Py.Hash, l => established.TrySetResult(l), l => closed.TrySetResult(l));
            await established.Task.WaitAsync(Patience);
            await Py.NextAsync("link_up", pythonUpBefore, Patience);
            return (link, closed);
        }

        public LocalDestination ServeDotNet(Action<Link>? onLink = null)
        {
            var identity = Identity.Generate();
            var local = new LocalDestination(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["interop", "link"], identity))
            {
                AcceptLinks = true,
                ProofStrategy = ProofStrategy.All,
                LinkEstablished = l =>
                {
                    l.ResourceStrategy = ResourceStrategy.AcceptAll;
                    onLink?.Invoke(l);
                },
            };
            Router.RegisterLocalDestination(local);
            return local;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            await Manager.DisposeAsync();
            await Py.DisposeAsync();
        }
    }

    private static async Task Until(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("condition not reached in time");
            await Task.Delay(25);
        }
    }

    private async Task<T> WithLog<T>(Session s, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is TimeoutException or Xunit.Sdk.XunitException)
        {
            string log;
            lock (s.Log) log = s.Log.ToString();
            throw new Xunit.Sdk.XunitException($"{ex.Message}\n--- .NET router log ---\n{log}\n--- python ---\n{s.Py.Log}");
        }
    }

    private static async Task<string> IdentifiedHash(Session s) =>
        (await s.Py.NextAsync("ready", 0, Patience)).GetProperty("identity_hash").GetString()!;

    private static byte[] RandomBytes(int size)
    {
        var data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // (a) .NET initiator -> Python responder: data both ways with receipts, RTT, keepalive, teardown by each side
    [Trait("Category", "Live")]
    [LiveFact]
    public async Task DotNetInitiator_PythonResponder_DataRttKeepaliveAndTeardownByEachSide()
    {
        await using var s = await Session.StartAsync();
        await WithLog(s, async () =>
        {
            var upBefore = s.Py.Count("link_up");
            var (link, closed) = await s.ConnectToPythonAsync();
            var up = await s.Py.NextAsync("link_up", upBefore, Patience);
            Assert.Equal("responder", up.GetProperty("role").GetString());
            Assert.Equal(Convert.ToHexStringLower(link.LinkId), up.GetProperty("link_id").GetString());
            Assert.NotNull(link.Rtt);

            var stat = await s.Py.Command("stat", "stat", Patience);
            Assert.Equal((int)LinkStatus.Active, stat.GetProperty("status").GetInt32());
            Assert.NotEqual(JsonValueKind.Null, stat.GetProperty("rtt").ValueKind);
            output.WriteLine($"RTT: .NET {link.Rtt:F4}s, Python {stat.GetProperty("rtt").GetDouble():F4}s; MTU .NET {link.Mtu}/MDU {link.Mdu}, Python {stat.GetProperty("mtu")}/{stat.GetProperty("mdu")}");

            // .NET -> Python, proven by upstream (PROVE_ALL) so the receipt completes
            var dataBefore = s.Py.Count("data");
            var receipt = link.Send("hello python"u8.ToArray());
            Assert.NotNull(receipt);
            var seen = await s.Py.NextAsync("data", dataBefore, Patience);
            Assert.Equal(Convert.ToHexStringLower("hello python"u8), seen.GetProperty("payload").GetString());
            Assert.True(await receipt.Completion.WaitAsync(Patience), "upstream's link packet proof did not validate");

            // Python -> .NET
            var got = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            link.PacketReceived = (data, _) => got.TrySetResult(data);
            var sent = await s.Py.Command($"send {Convert.ToHexStringLower("hello dotnet"u8)}", "sent", Patience);
            // upstream's initiator-side destination has no proof strategy, so no proof is expected for this direction
            Assert.True(sent.GetProperty("ok").GetBoolean());
            Assert.Equal("hello dotnet"u8.ToArray(), await got.Task.WaitAsync(Patience));

            // keepalive: both sides idle past the minimum keepalive interval (5 s) and the link must stay up with traffic flowing
            await Task.Delay(TimeSpan.FromSeconds(14));
            Assert.Equal(LinkStatus.Active, link.Status);
            var idle = await s.Py.Command("stat", "stat", Patience);
            Assert.Equal((int)LinkStatus.Active, idle.GetProperty("status").GetInt32());
            Assert.True(idle.GetProperty("last_inbound_age").GetDouble() < 11, $"Python saw no inbound traffic for {idle.GetProperty("last_inbound_age")} s");
            output.WriteLine($"After 14 s idle both Active; Python keepalive={idle.GetProperty("keepalive")} s, last inbound {idle.GetProperty("last_inbound_age").GetDouble():F1} s ago");

            // teardown by the .NET initiator
            var closedBefore = s.Py.Count("link_closed");
            link.Teardown();
            var pyClosed = await s.Py.NextAsync("link_closed", closedBefore, Patience);
            Assert.Equal(2, pyClosed.GetProperty("reason").GetInt32());
            Assert.Equal(LinkStatus.Closed, link.Status);

            // teardown by the Python responder on a fresh link
            var (link2, closed2) = await s.ConnectToPythonAsync();
            await s.Py.NextAsync("link_up", upBefore + 1, Patience);
            await s.Py.SendLineAsync("close");
            await closed2.Task.WaitAsync(Patience);
            Assert.Equal(LinkStatus.Closed, link2.Status);
            Assert.Equal(LinkTeardownReason.DestinationClosed, link2.TeardownReason);
            return 0;
        });
    }

    // (b) Python initiator -> .NET responder
    [Trait("Category", "Live")]
    [LiveFact]
    public async Task PythonInitiator_DotNetResponder_DataIdentifyRequestAndTeardown()
    {
        await using var s = await Session.StartAsync();
        await WithLog(s, async () =>
        {
            var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var identified = new TaskCompletionSource<Identity>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var local = s.ServeDotNet(l =>
            {
                l.PacketReceived = (data, _) => received.TrySetResult(data);
                l.RemoteIdentified = (_, id) => identified.TrySetResult(id);
                l.Closed = x => closed.TrySetResult(x);
                accepted.TrySetResult(l);
            });
            local.RegisterRequestHandler("/echo", ctx => ctx.Data, RequestPolicy.All);
            local.RegisterRequestHandler("/reverse", ctx => ((byte[])ctx.Data!).Reverse().ToArray(), RequestPolicy.All);
            await s.Router.AnnounceAsync(local);
            await s.Py.WaitForAnnounce(local.Destination.Hash, Patience);

            var upBefore = s.Py.Count("link_up");
            var connected = s.Py.Command($"connect {Convert.ToHexStringLower(local.Destination.Hash)}", "link_up", Patience);
            var link = await accepted.Task.WaitAsync(Patience);
            var up = await connected;
            Assert.Equal("initiator", up.GetProperty("role").GetString());
            Assert.Equal(Convert.ToHexStringLower(link.LinkId), up.GetProperty("link_id").GetString());
            await Until(() => link.Status == LinkStatus.Active && link.Rtt is not null, 15);
            var stat = await s.Py.Command("stat", "stat", Patience);
            output.WriteLine($"RTT: .NET {link.Rtt:F4}s, Python {stat.GetProperty("rtt").GetDouble():F4}s; status {link.Status}");

            // Python -> .NET (proof from the .NET destination: PROVE_ALL covers link packets)
            var sent = await s.Py.Command($"send {Convert.ToHexStringLower("from python"u8)}", "sent", Patience);
            Assert.True(sent.GetProperty("delivered").GetBoolean(), ".NET link packet proof was not accepted by upstream");
            Assert.Equal("from python"u8.ToArray(), await received.Task.WaitAsync(Patience));

            // .NET -> Python
            var dataBefore = s.Py.Count("data");
            var receipt = link.Send("from dotnet"u8.ToArray())!;
            var seen = await s.Py.NextAsync("data", dataBefore, Patience);
            Assert.Equal(Convert.ToHexStringLower("from dotnet"u8), seen.GetProperty("payload").GetString());
            Assert.NotNull(receipt);

            // Python identifies itself over the link
            await s.Py.SendLineAsync("identify");
            var id = await identified.Task.WaitAsync(Patience);
            Assert.Equal(await IdentifiedHash(s), id.HexHash);

            // request/response: Python requests, .NET answers (small as a REQUEST/RESPONSE packet, large as Resources)
            var small = await s.Py.Command("request /echo hello-request", "response", Patience);
            Assert.True(small.GetProperty("ok").GetBoolean());
            Assert.Equal("hello-request", small.GetProperty("text").GetString());
            var bigText = new string('r', 5000) + "end";
            var big = await s.Py.Command($"request /reverse {bigText}", "response", Patience);
            Assert.True(big.GetProperty("ok").GetBoolean());
            Assert.Equal(Sha(Encoding.UTF8.GetBytes(bigText).Reverse().ToArray()), big.GetProperty("sha256").GetString());

            // teardown by the Python initiator, then a second link torn down by .NET
            await s.Py.SendLineAsync("close");
            await closed.Task.WaitAsync(Patience);
            Assert.Equal(LinkStatus.Closed, link.Status);
            Assert.Equal(LinkTeardownReason.InitiatorClosed, link.TeardownReason);

            accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closedBefore = s.Py.Count("link_closed");
            await s.Py.Command($"connect {Convert.ToHexStringLower(local.Destination.Hash)}", "link_up", Patience);
            var second = await accepted.Task.WaitAsync(Patience);
            second.Teardown();
            var pyClosed = await s.Py.NextAsync("link_closed", closedBefore, Patience);
            Assert.Equal(3, pyClosed.GetProperty("reason").GetInt32());
            return 0;
        });
    }

    public static IEnumerable<object[]> ResourceCases() =>
    [
        [1_000_000, "random"],
        [3_500_000, "random"],
        [2_200_000, "text"],
    ];

    // (c) multi-MB resources in both directions over a direct link, verified by SHA-256; the .NET side initiates the link
    [Trait("Category", "Live")]
    [LiveTheory]
    [MemberData(nameof(ResourceCases))]
    public async Task Resource_TransfersBothWaysOverDirectLink_ByteIdentical(int size, string mode)
    {
        await using var s = await Session.StartAsync();
        await WithLog(s, async () =>
        {
            var (link, _) = await s.ConnectToPythonAsync();
            link.ResourceStrategy = ResourceStrategy.AcceptAll;
            var rxDone = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            link.ResourceReceived = r => rxDone.TrySetResult(r);

            // .NET -> Python
            var payload = mode == "text" ? Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("dotnet payload line 0123456789\n", (size / 31) + 1)))[..size] : RandomBytes(size);
            var doneBefore = s.Py.Count("resource_done");
            var txDone = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = DateTime.UtcNow;
            link.SendResource(payload, metadata: "dotnet-meta", concluded: r => txDone.TrySetResult(r));
            var pyDone = await s.Py.NextAsync("resource_done", doneBefore, TimeSpan.FromMinutes(3));
            var tx = await txDone.Task.WaitAsync(TimeSpan.FromMinutes(1));
            var toPython = DateTime.UtcNow - started;
            Assert.Equal(ResourceStatus.Complete, tx.Status);
            Assert.Equal(0x06, pyDone.GetProperty("status").GetInt32());
            Assert.Equal(size, pyDone.GetProperty("size").GetInt32());
            Assert.Equal(Sha(payload), pyDone.GetProperty("sha256").GetString());
            Assert.Equal("\"dotnet-meta\"", pyDone.GetProperty("metadata").GetString());
            output.WriteLine($".NET -> Python {size} B ({mode}): sha256 identical, {pyDone.GetProperty("segments")} segment(s), compressed={pyDone.GetProperty("compressed")}, {toPython.TotalSeconds:F1}s");

            // Python -> .NET
            started = DateTime.UtcNow;
            var sentTask = s.Py.Command($"resource {size} seed-{mode} {mode} py-meta", "resource_sent", TimeSpan.FromMinutes(3));
            var rx = await rxDone.Task.WaitAsync(TimeSpan.FromMinutes(3));
            var sentEvent = await sentTask;
            var toDotNet = DateTime.UtcNow - started;
            Assert.Equal(ResourceStatus.Complete, rx.Status);
            Assert.Equal(0x06, sentEvent.GetProperty("status").GetInt32());
            Assert.Equal(sentEvent.GetProperty("sha256").GetString(), Sha(rx.Data!));
            Assert.Equal(size, rx.Data!.Length);
            Assert.Equal("py-meta", rx.Metadata);
            output.WriteLine($"Python -> .NET {size} B ({mode}): sha256 identical, compressed(py)={sentEvent.GetProperty("compressed")}, {toDotNet.TotalSeconds:F1}s");
            return 0;
        });
    }

    // (c, other direction of link establishment) Python initiator -> .NET responder: resources both ways plus a file-response request
    [Trait("Category", "Live")]
    [LiveFact]
    public async Task Resource_TransfersOverPythonInitiatedLink_AndFileResponseRequest()
    {
        await using var s = await Session.StartAsync();
        await WithLog(s, async () =>
        {
            var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rxDone = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var local = s.ServeDotNet(l =>
            {
                l.ResourceReceived = r => rxDone.TrySetResult(r);
                accepted.TrySetResult(l);
            });
            local.RegisterRequestHandler("/blob", ctx => new FileResponse(RandomBytes(300_000), "blob-meta"), RequestPolicy.All);
            await s.Router.AnnounceAsync(local);
            await s.Py.WaitForAnnounce(local.Destination.Hash, Patience);
            var connecting = s.Py.Command($"connect {Convert.ToHexStringLower(local.Destination.Hash)}", "link_up", Patience);
            var link = await accepted.Task.WaitAsync(Patience);
            await connecting;

            var sentEvent = await s.Py.Command("resource 2500000 seed-py random", "resource_sent", TimeSpan.FromMinutes(3));
            var rx = await rxDone.Task.WaitAsync(TimeSpan.FromMinutes(1));
            Assert.Equal(sentEvent.GetProperty("sha256").GetString(), Sha(rx.Data!));

            var payload = RandomBytes(1_500_000);
            var doneBefore = s.Py.Count("resource_done");
            link.SendResource(payload);
            var pyDone = await s.Py.NextAsync("resource_done", doneBefore, TimeSpan.FromMinutes(3));
            Assert.Equal(Sha(payload), pyDone.GetProperty("sha256").GetString());

            var response = await s.Py.Command("request /blob go", "response", TimeSpan.FromMinutes(2));
            Assert.True(response.GetProperty("ok").GetBoolean());
            Assert.Equal(300_000, response.GetProperty("size").GetInt32());
            Assert.Contains("blob-meta", response.GetProperty("metadata").GetString());
            output.WriteLine($"Python->.NET 2.5 MB and .NET->Python 1.5 MB identical; file response 300 kB received by Python with metadata {response.GetProperty("metadata")}");
            return 0;
        });
    }

    // (e) Channel messages and Buffer streams between .NET and upstream Python, with the .NET side as link initiator
    [Trait("Category", "Live")]
    [LiveFact]
    public async Task Channel_MessagesAndBufferStreamsInteroperate_BothWays()
    {
        await using var s = await Session.StartAsync();
        await WithLog(s, async () =>
        {
            var (link, _) = await s.ConnectToPythonAsync();
            var channel = link.GetChannel();
            channel.RegisterMessageType<LiveTextMessage>();
            var received = new List<string>();
            var burstDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.AddMessageHandler(m =>
            {
                lock (received)
                {
                    received.Add(((LiveTextMessage)m).Text);
                    if (received.Count == 60) burstDone.TrySetResult();
                }

                return true;
            });
            var reader = ChannelBuffer.CreateReader(2, channel);
            var writer = ChannelBuffer.CreateWriter(1, channel);

            // .NET -> Python: 60 messages through the window, delivered in order (Python proves each one)
            var before = s.Py.Count("channel_msg");
            for (var i = 0; i < 60; i++)
            {
                while (!channel.IsReadyToSend) await Task.Delay(2);
                channel.Send(new LiveTextMessage { Text = "dotnet-" + i });
            }

            await s.Py.NextAsync("channel_msg", before + 59, Patience);
            var texts = s.Py.Events("channel_msg").Skip(before).Select(e => e.GetProperty("text").GetString()).ToList();
            Assert.Equal(Enumerable.Range(0, 60).Select(i => "dotnet-" + i), texts);
            output.WriteLine($".NET -> Python: 60 channel messages in order; .NET window now {channel.WindowSize}/{channel.WindowMaxSize}");

            // Python -> .NET
            var burst = await s.Py.Command("chburst 60 py-", "channel_burst_done", Patience);
            await burstDone.Task.WaitAsync(Patience);
            lock (received) Assert.Equal(Enumerable.Range(0, 60).Select(i => "py-" + i), received);
            output.WriteLine($"Python -> .NET: 60 channel messages in order; Python window {burst.GetProperty("window")}");

            // Buffer: .NET writes (compressible + random) on stream 1, Python reads
            var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("channel buffer line\n", 10_000)));
            byte[] payload = [.. text, .. RandomBytes(150_000)];
            var doneBefore = s.Py.Count("buffer_done");
            await writer.WriteAsync(payload);
            await writer.CloseAsync();
            var done = await s.Py.NextAsync("buffer_done", doneBefore, TimeSpan.FromMinutes(2));
            Assert.Equal(payload.Length, done.GetProperty("size").GetInt32());
            Assert.Equal(Sha(payload), done.GetProperty("sha256").GetString());
            output.WriteLine($".NET -> Python buffer stream {payload.Length} B sha256 identical");

            // Python writes on stream 2, .NET reads to EOF
            var readTask = Task.Run(async () =>
            {
                using var ms = new MemoryStream();
                await reader.CopyToAsync(ms);
                return ms.ToArray();
            });
            var sent = await s.Py.Command("bufsend 200000 seed-buf text", "buffer_sent", TimeSpan.FromMinutes(2));
            var got = await readTask.WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(200_000, got.Length);
            Assert.Equal(sent.GetProperty("sha256").GetString(), Sha(got));
            output.WriteLine("Python -> .NET buffer stream 200000 B sha256 identical");
            return 0;
        });
    }

    [Trait("Category", "Live")]
    [LiveTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Parity5UpgradedMtu_DataResourceAndChannelPayloadsExceed500BytesInBothDirections(bool dotNetInitiator)
    {
        await using var s = await Session.StartAsync();
        await WithLog(s, async () =>
        {
            Link link;
            if (dotNetInitiator)
            {
                (link, _) = await s.ConnectToPythonAsync();
            }
            else
            {
                var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
                var local = s.ServeDotNet(l => accepted.TrySetResult(l));
                await s.Router.AnnounceAsync(local);
                await s.Py.WaitForAnnounce(local.Destination.Hash, Patience);
                var pythonUpBefore = s.Py.Count("link_up");
                await s.Py.SendLineAsync("connect " + Convert.ToHexStringLower(local.Destination.Hash));
                link = await accepted.Task.WaitAsync(Patience);
                await s.Py.NextAsync("link_up", pythonUpBefore, Patience);
            }
            var stat = await s.Py.Command("stat", "stat", Patience);
            Assert.Equal(16384, link.Mtu);
            Assert.Equal(link.Mtu, stat.GetProperty("mtu").GetInt32());
            Assert.Equal(link.Mdu, stat.GetProperty("mdu").GetInt32());
            var payload = RandomBytes(8193);
            var before = s.Py.Count("data");
            Assert.NotNull(link.Send(payload));
            var seen = await s.Py.NextAsync("data", before, Patience);
            Assert.Equal(Convert.ToHexStringLower(payload), seen.GetProperty("payload").GetString());
            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            link.PacketReceived = (data, _) => received.TrySetResult(data);
            await s.Py.SendLineAsync("send " + Convert.ToHexStringLower(payload));
            Assert.Equal(payload, await received.Task.WaitAsync(Patience));

            var channel = link.GetChannel();
            channel.RegisterMessageType<LiveTextMessage>();
            var message = new string('x', 3001);
            var channelBefore = s.Py.Count("channel_msg");
            channel.Send(new LiveTextMessage { Text = message });
            Assert.Equal(message, (await s.Py.NextAsync("channel_msg", channelBefore, Patience)).GetProperty("text").GetString());
            var incoming = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.AddMessageHandler(m => { incoming.TrySetResult(((LiveTextMessage)m).Text); return true; });
            await s.Py.SendLineAsync("chsend " + message);
            Assert.Equal(message, await incoming.Task.WaitAsync(Patience));

            var resourceData = RandomBytes(150_017);
            var resourceBefore = s.Py.Count("resource_done");
            var resourceFinished = new TaskCompletionSource<ResourceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            link.SendResource(resourceData, autoCompress: false, concluded: r => resourceFinished.TrySetResult(r.Status));
            Assert.Equal(ResourceStatus.Complete, await resourceFinished.Task.WaitAsync(Patience));
            var done = await s.Py.NextAsync("resource_done", resourceBefore, Patience);
            Assert.Equal(resourceData.Length, done.GetProperty("size").GetInt32());
            Assert.Equal(Sha(resourceData), done.GetProperty("sha256").GetString());
            var incomingResource = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            link.ResourceStrategy = ResourceStrategy.AcceptAll;
            link.ResourceReceived = r => { if (r.Status == ResourceStatus.Complete) incomingResource.TrySetResult(r.Data!); };
            await s.Py.SendLineAsync("resource 150019 upgraded-reverse random");
            var reverse = await incomingResource.Task.WaitAsync(Patience);
            var sent = await s.Py.NextAsync("resource_sent", 0, Patience);
            Assert.Equal(150019, reverse.Length);
            Assert.Equal(Sha(reverse), sent.GetProperty("sha256").GetString());
            link.Teardown();
            return 0;
        });
    }

    [Trait("Category", "Live")]
    [LiveFact]
    public async Task Parity5StreamFileResponse_UpstreamReadsMultipartFileAndMetadata()
    {
        await using var s = await Session.StartAsync();
        var file = Path.Combine(Path.GetTempPath(), "dotreticulum-response-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var data = RandomBytes(2_200_037);
            await File.WriteAllBytesAsync(file, data);
            await WithLog(s, async () =>
            {
                var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
                var local = s.ServeDotNet(link => accepted.TrySetResult(link));
                local.RegisterRequestHandler("/stream", _ => new StreamFileResponse(
                    new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read),
                    new Dictionary<object, object?> { ["name"] = "stream-response" }), RequestPolicy.All);
                await s.Router.AnnounceAsync(local);
                await s.Py.WaitForAnnounce(local.Destination.Hash, Patience);
                await s.Py.SendLineAsync("connect " + Convert.ToHexStringLower(local.Destination.Hash));
                await accepted.Task.WaitAsync(Patience);
                var response = await s.Py.Command("request /stream ignored", "response", Patience);
                Assert.True(response.GetProperty("ok").GetBoolean());
                Assert.Equal(data.Length, response.GetProperty("size").GetInt32());
                Assert.Equal(Sha(data), response.GetProperty("sha256").GetString());
                Assert.Contains("stream-response", response.GetProperty("metadata").GetString());
                await Until(() =>
                {
                    try
                    {
                        using var reopened = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        Assert.Equal(data.Length, reopened.Length);
                        return true;
                    }
                    catch (IOException) { return false; }
                }, 10);
                return 0;
            });
        }
        finally { File.Delete(file); }
    }

    // .NET requests of a Python responder: large request (sent as a Resource), echoed back as a large response Resource, and a file response
    [Trait("Category", "Live")]
    [LiveFact]
    public async Task DotNetRequests_AgainstPython_UseResourcesForLargeRequestResponseAndFileResponse()
    {
        await using var s = await Session.StartAsync();
        await WithLog(s, async () =>
        {
            var (link, _) = await s.ConnectToPythonAsync();
            var small = link.Request("/echo", "tiny"u8.ToArray());
            Assert.Equal("tiny"u8.ToArray(), (byte[])(await small!.ResponseTask.WaitAsync(Patience))!);

            var data = RandomBytes(60_000);
            var big = link.Request("/reverse", data, timeout: 120);
            var response = (byte[])(await big!.ResponseTask.WaitAsync(TimeSpan.FromMinutes(2)))!;
            Assert.Equal(data.Reverse().ToArray(), response);

            var file = link.Request("/file", Encoding.UTF8.GetBytes("400000:seed-file:random").Length > 0 ? "400000:seed-file:random" : null, timeout: 120);
            var body = (byte[])(await file!.ResponseTask.WaitAsync(TimeSpan.FromMinutes(2)))!;
            Assert.Equal(400_000, body.Length);
            Assert.NotNull(file.Metadata);
            output.WriteLine($"Request/response via Resources: 60 kB request echoed reversed; 400 kB file response with metadata {System.Text.Json.JsonSerializer.Serialize(file.Metadata is Dictionary<object, object?> d ? d.ToDictionary(k => k.Key.ToString()!, v => v.Value?.ToString()) : null)}");
            return 0;
        });
    }
}

public sealed class LiveTextMessage : MessageBase
{
    public string Text { get; set; } = "";

    public override ushort MsgType => 0x0101;

    public override byte[] Pack() => Encoding.UTF8.GetBytes(Text);

    public override void Unpack(ReadOnlySpan<byte> raw) => Text = Encoding.UTF8.GetString(raw);
}

public sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1")
        {
            Skip = "Live Python interop is opt-in: set DOTRETICULUM_LIVE=1 (plus DOTRETICULUM_PYTHON / DOTRETICULUM_UPSTREAM).";
        }
    }
}
