using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces.Tests;

// Independent oracle: GenerateParity6I2pVectors.py, pinned RNS a29bfb51c468945c96a1ee22cae0114e993e2391.
// Controlled SAM tests prove bounded software contracts, not actual router or I2P network interoperability.
public sealed class Parity6I2pTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-i2p.json")));

    private static I2pPacketInterfaceOptions Options(Parity6I2pSamSimulator simulator, string storage, bool connectable = false) => new()
    {
        Name = "I2P vector", StoragePath = storage, Connectable = connectable,
        TransportIdentityHash = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
        SamHost = "127.0.0.1", SamPort = simulator.Port, ReconnectDelay = TimeSpan.FromMilliseconds(30),
        SetupTimeout = TimeSpan.FromSeconds(3),
    };

    private static string OwnedDirectory()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "i2p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void DefaultsFramingAndPersistenceNamesMatchExecutedReference()
    {
        using var fixture = Vectors();
        var root = fixture.RootElement;
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", root.GetProperty("revision").GetString());
        Assert.Equal("1.5.7", root.GetProperty("version").GetString());
        Assert.Equal(I2pPacketInterface.NominalBitrate, root.GetProperty("bitrate").GetInt64());
        Assert.Equal(I2pPacketInterface.DefaultIfacSize, root.GetProperty("defaultIfacSize").GetInt32());
        var options = new I2pPacketInterfaceOptions { Name = "test", StoragePath = "." };
        Assert.False(options.Connectable);
        Assert.Equal(root.GetProperty("reconnectSeconds").GetInt32(), options.ReconnectDelay.TotalSeconds);
        Assert.Equal(root.GetProperty("probeAfterSeconds").GetInt32(), options.KeepAliveInterval.TotalSeconds);
        Assert.Equal(root.GetProperty("readTimeoutSeconds").GetInt32(), options.ReadTimeout.TotalSeconds);
        var destination = Convert.FromBase64String(root.GetProperty("publicDestination").GetString()!);
        Assert.Equal(root.GetProperty("publicBase32").GetString(), I2pDestination.PublicBase32(destination));
        foreach (var item in root.GetProperty("paths").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var hash = Convert.FromHexString(item.GetProperty("identityHash").GetString()!);
            Assert.Equal(item.GetProperty("selectedFile").GetString(),
                I2pDestination.KeyFileName(name, hash, item.GetProperty("legacyExists").GetBoolean()));
            Assert.Equal(item.GetProperty("legacyFile").GetString(), I2pDestination.KeyFileName(name, [], true));
        }
        foreach (var item in root.GetProperty("frames").EnumerateArray())
        {
            var payload = Convert.FromHexString(item.GetProperty("payload").GetString()!);
            var expected = Convert.FromHexString(item.GetProperty("frame").GetString()!);
            Assert.Equal(expected, HdlcPacketFraming.Encode(payload, 1064, false));
            Assert.Equal(expected.Length, item.GetProperty("txb").GetInt32());
            var decoder = new HdlcFrameDecoder(1064, false);
            var frames = new List<byte[]>();
            foreach (var value in expected) decoder.Feed(new byte[] { value }, frames);
            Assert.Equal(payload, Assert.Single(frames));
        }
    }

    [Fact]
    public async Task OutgoingFragmentedSamNamingAndBidirectionalPacketsUseIndependentChild()
    {
        await using var simulator = new Parity6I2pSamSimulator { FragmentReplies = true };
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var children = Channel.CreateBounded<I2pPacketPeer>(4);
        await using var bearer = new I2pPacketInterface(Options(simulator, ".") with { Peers = ["peer.i2p"], ConfiguredBitrate = 8000 });
        bearer.ChildConnected += peer => Assert.True(children.Writer.TryWrite(Assert.IsType<I2pPacketPeer>(peer)));
        await bearer.StartAsync(timeout.Token);
        var child = await children.Reader.ReadAsync(timeout.Token);
        var remote = await simulator.OutgoingAsync(timeout.Token);
        await TestSupport.WaitFor(() => child.IsConnected);
        Assert.True(child.IsInitiator);
        Assert.Equal(1064, child.HardwareMtu);
        Assert.Equal(256000, child.Bitrate);
        Assert.Equal(8000, bearer.Bitrate);
        Assert.True(child.SupportsMtuDiscovery);
        Assert.Equal(1, Volatile.Read(ref simulator.NamingLookups));
        var packet = TestSupport.MakePacket(51);
        await child.SendAsync(packet, timeout.Token);
        Assert.Equal(packet, await Parity6I2pSamSimulator.PacketAsync(remote, timeout.Token));
        await remote.WriteAsync(HdlcPacketFraming.Encode(packet), timeout.Token);
        await using var receive = child.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.Equal(packet, (await TestSupport.Next(receive)).ToArray());
        await bearer.SendAsync(TestSupport.MakePacket(99), timeout.Token); // Parent deliberately does not broadcast.
        Assert.Equal(packet.Length, Convert.ToInt32(child.GetDiagnostics()["rxb"]));
        Assert.True(Convert.ToInt64(child.GetDiagnostics()["txb"]) > packet.Length);
        Assert.Throws<InvalidOperationException>(() => child.ReceiveAsync(timeout.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncomingCoalescedOrFragmentedDestinationPreservesFramesAndPeerIsolation(bool fragment)
    {
        var directory = OwnedDirectory();
        try
        {
            await using var simulator = new Parity6I2pSamSimulator { FragmentReplies = fragment };
            using var timeout = new CancellationTokenSource(TestSupport.Patience);
            var children = Channel.CreateBounded<I2pPacketPeer>(4);
            await using var bearer = new I2pPacketInterface(Options(simulator, directory, true) with { ConfiguredBitrate = 8000 });
            bearer.ChildConnected += _ => throw new InvalidOperationException("observer failure");
            bearer.ChildConnected += peer => Assert.True(children.Writer.TryWrite(Assert.IsType<I2pPacketPeer>(peer)));
            var diagnostics = Channel.CreateBounded<Exception>(4);
            bearer.Diagnostic += error => diagnostics.Writer.TryWrite(error);
            await bearer.StartAsync(timeout.Token);
            var packet = TestSupport.MakePacket(61);
            var remoteA = await simulator.IncomingAsync(HdlcPacketFraming.Encode(packet), timeout.Token);
            var childA = await children.Reader.ReadAsync(timeout.Token);
            var remoteB = await simulator.IncomingAsync(ReadOnlyMemory<byte>.Empty, timeout.Token);
            var childB = await children.Reader.ReadAsync(timeout.Token);
            Assert.NotSame(childA, childB);
            Assert.False(childA.IsInitiator);
            Assert.Equal(8000, childA.Bitrate);
            Assert.Equal(8000, Convert.ToInt64(childA.GetDiagnostics()["bitrate"]));
            await using var receiverA = childA.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
            Assert.Equal(packet, (await TestSupport.Next(receiverA)).ToArray());
            remoteA.Dispose();
            Assert.False(await receiverA.MoveNextAsync().AsTask().WaitAsync(TestSupport.Patience));
            await TestSupport.WaitFor(() => childB.IsConnected);
            await childB.SendAsync(packet, timeout.Token);
            Assert.Equal(packet, await Parity6I2pSamSimulator.PacketAsync(remoteB, timeout.Token));
            Assert.Contains("observer failure", (await diagnostics.Reader.ReadAsync(timeout.Token)).Message);
            await TestSupport.WaitFor(() => bearer.Peers.Count == 1);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PersistedDestinationSurvivesRestartAndLegacyFileWins()
    {
        var directory = OwnedDirectory();
        try
        {
            await using var simulator = new Parity6I2pSamSimulator();
            using var timeout = new CancellationTokenSource(TestSupport.Patience);
            var options = Options(simulator, directory, true);
            string? firstAddress;
            await using (var first = new I2pPacketInterface(options))
            {
                await first.StartAsync(timeout.Token);
                await TestSupport.WaitFor(() => first.Base32Address is not null);
                firstAddress = first.Base32Address;
                Assert.Equal(1, simulator.Generated);
            }
            await using (var second = new I2pPacketInterface(options))
            {
                await second.StartAsync(timeout.Token);
                await TestSupport.WaitFor(() => second.Base32Address is not null);
                Assert.Equal(firstAddress, second.Base32Address);
                Assert.Equal(1, simulator.Generated);
            }
            var newPath = Path.Combine(directory, "i2p", I2pDestination.KeyFileName(options.Name, options.TransportIdentityHash.Span));
            var legacyPath = Path.Combine(directory, "i2p", I2pDestination.KeyFileName(options.Name, [], true));
            File.Move(newPath, legacyPath);
            await using (var legacy = new I2pPacketInterface(options))
            {
                await legacy.StartAsync(timeout.Token);
                await TestSupport.WaitFor(() => legacy.Base32Address is not null);
                Assert.Equal(firstAddress, legacy.Base32Address);
                Assert.Equal(1, simulator.Generated);
            }
            Assert.False(File.Exists(newPath));
            Assert.All(simulator.SessionDestinations, destination => Assert.Equal(simulator.PrivateDestination, destination));
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "i2p"), "*.new"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(legacyPath));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CorruptPersistedKeyIsReportedNeverReplacedAndNoSecretIsLogged()
    {
        var directory = OwnedDirectory();
        try
        {
            await using var simulator = new Parity6I2pSamSimulator();
            var options = Options(simulator, directory, true) with { MaxReconnectAttempts = 0 };
            var keyDirectory = Path.Combine(directory, "i2p");
            Directory.CreateDirectory(keyDirectory);
            var path = Path.Combine(keyDirectory, I2pDestination.KeyFileName(options.Name, options.TransportIdentityHash.Span));
            await File.WriteAllTextAsync(path, "not-a-private-key");
            var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var bearer = new I2pPacketInterface(options);
            bearer.Diagnostic += exception => error.TrySetResult(exception);
            await bearer.StartAsync(default);
            Assert.DoesNotContain("not-a-private-key", (await error.Task.WaitAsync(TestSupport.Patience)).Message);
            Assert.Equal("not-a-private-key", await File.ReadAllTextAsync(path));
            Assert.Equal(0, simulator.Generated);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SessionLossReconnectsSameOutgoingChildAndDoesNotLoseItsIdentity()
    {
        await using var simulator = new Parity6I2pSamSimulator();
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var ready = new TaskCompletionSource<I2pPacketPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bearer = new I2pPacketInterface(Options(simulator, ".") with { Peers = ["peer.i2p"] });
        bearer.ChildConnected += peer => ready.TrySetResult(Assert.IsType<I2pPacketPeer>(peer));
        await bearer.StartAsync(timeout.Token);
        var child = await ready.Task.WaitAsync(timeout.Token);
        _ = await simulator.OutgoingAsync(timeout.Token);
        await TestSupport.WaitFor(() => child.IsConnected);
        var firstSession = child.GetDiagnostics()["session_id"];
        var publicIdentity = child.GetDiagnostics()["local_i2p_b32"];
        simulator.DropSessions();
        var remote = await simulator.OutgoingAsync(timeout.Token);
        await TestSupport.WaitFor(() => child.IsConnected && simulator.SessionCount >= 2);
        Assert.Same(child, Assert.Single(bearer.Peers));
        var diagnostics = child.GetDiagnostics();
        Assert.Equal("connected", diagnostics["setup_phase"]);
        Assert.True(Convert.ToInt32(diagnostics["session_attempts"]) >= 2);
        Assert.True(Convert.ToInt32(diagnostics["stream_connect_attempts"]) >= 2);
        Assert.NotEqual(firstSession, diagnostics["session_id"]);
        Assert.Equal(publicIdentity, diagnostics["local_i2p_b32"]);
        Assert.Equal(2, simulator.SessionIds.Distinct().Count());
        await child.SendAsync(TestSupport.MakePacket(71), timeout.Token);
        Assert.Equal(71, TestSupport.TagOf(await Parity6I2pSamSimulator.PacketAsync(remote, timeout.Token)));
        Assert.Equal(1, simulator.Generated);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task StreamRetriesRetainSamSessionIdAndPublicIdentity(int failures, int timeouts)
    {
        await using var simulator = new Parity6I2pSamSimulator
        {
            FailConnectAttempts = failures, HangConnectAttempts = timeouts,
        };
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await using var bearer = new I2pPacketInterface(Options(simulator, ".") with
        {
            Peers = ["peer.i2p"], SetupTimeout = TimeSpan.FromSeconds(2), MaxReconnectAttempts = 3,
        });
        var ready = new TaskCompletionSource<I2pPacketPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        bearer.ChildConnected += peer => ready.TrySetResult(Assert.IsType<I2pPacketPeer>(peer));
        await bearer.StartAsync(timeout.Token);
        var child = await ready.Task.WaitAsync(timeout.Token);
        var first = await simulator.OutgoingAsync(timeout.Token);
        await TestSupport.WaitFor(() => child.IsConnected);
        var session = child.GetDiagnostics()["session_id"];
        var identity = child.GetDiagnostics()["local_i2p_b32"];
        Assert.Equal(I2pDestination.PublicBase32(Convert.FromBase64String(simulator.PublicDestination)) + ".b32.i2p", identity);
        first.Dispose();
        var replacement = await simulator.OutgoingAsync(timeout.Token);
        await TestSupport.WaitFor(() => child.IsConnected &&
            Convert.ToInt32(child.GetDiagnostics()["stream_connect_attempts"]) >= failures + timeouts + 2);
        Assert.Equal(session, child.GetDiagnostics()["session_id"]);
        Assert.Equal(identity, child.GetDiagnostics()["local_i2p_b32"]);
        Assert.Equal(1, simulator.SessionCount);
        Assert.Equal(1, simulator.ActiveSessionCount);
        Assert.Equal(1, simulator.Generated);
        Assert.All(simulator.StreamSessionIds, id => Assert.Equal(Assert.Single(simulator.SessionIds), id));
        Assert.Equal(failures + timeouts + 2, simulator.ConnectAttempts);
        await child.SendAsync(TestSupport.MakePacket(72), timeout.Token);
        Assert.Equal(72, TestSupport.TagOf(await Parity6I2pSamSimulator.PacketAsync(replacement, timeout.Token)));
    }

    [Fact]
    public async Task SessionControlLossInterruptsPendingStreamSetupAndCreatesNewSession()
    {
        await using var simulator = new Parity6I2pSamSimulator { HangConnect = true };
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await using var bearer = new I2pPacketInterface(Options(simulator, ".") with
        {
            Peers = ["peer.i2p"], MaxReconnectAttempts = 1,
        });
        var ready = new TaskCompletionSource<I2pPacketPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        bearer.ChildConnected += peer => ready.TrySetResult(Assert.IsType<I2pPacketPeer>(peer));
        await bearer.StartAsync(timeout.Token);
        var child = await ready.Task.WaitAsync(timeout.Token);
        await TestSupport.WaitFor(() => simulator.ConnectAttempts == 1);
        var firstSession = child.GetDiagnostics()["session_id"];
        var identity = child.GetDiagnostics()["local_i2p_b32"];
        simulator.DropSessions();
        await TestSupport.WaitFor(() => simulator.ConnectAttempts == 2);
        Assert.Equal("session_control", child.GetDiagnostics()["last_failure_phase"]);
        Assert.NotEqual(firstSession, child.GetDiagnostics()["session_id"]);
        Assert.Equal(identity, child.GetDiagnostics()["local_i2p_b32"]);
        Assert.Equal(2, simulator.SessionIds.Distinct().Count());
        Assert.Equal(1, simulator.Generated);
        Assert.False(child.IsConnected);
    }

    [Fact]
    public async Task ExplicitInvalidSessionIdRecreatesSessionWithoutRegeneratingPrivateIdentity()
    {
        await using var simulator = new Parity6I2pSamSimulator { InvalidIdConnectAttempts = 1 };
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await using var bearer = new I2pPacketInterface(Options(simulator, ".") with
        {
            Peers = ["peer.i2p"], MaxReconnectAttempts = 1,
        });
        await bearer.StartAsync(timeout.Token);
        _ = await simulator.OutgoingAsync(timeout.Token);
        var child = Assert.Single(bearer.Peers);
        await TestSupport.WaitFor(() => child.IsConnected);
        Assert.Equal(2, simulator.SessionIds.Distinct().Count());
        Assert.Equal(2, simulator.StreamSessionIds.Distinct().Count());
        Assert.Single(simulator.SessionDestinations.Distinct());
        Assert.Equal(1, simulator.Generated);
        Assert.Contains("INVALID_ID", child.GetDiagnostics()["last_error"]?.ToString() ?? "");
    }

    [Theory]
    [InlineData("HELLO REPLY RESULT=I2P_ERROR\n")]
    [InlineData("HELLO REPLY RESULT=OK VERSION=3.3\n")]
    [InlineData("HELLO REPLY VERSION=3.1\n")]
    [InlineData("unparseable\n")]
    public async Task InvalidHelloAndFailureResultsAreReportedWithoutTakingDownParent(string reply)
    {
        await using var simulator = new Parity6I2pSamSimulator { HelloReply = reply };
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bearer = new I2pPacketInterface(Options(simulator, ".") with { Peers = ["peer.i2p"], MaxReconnectAttempts = 0 });
        bearer.Diagnostic += exception => error.TrySetResult(exception);
        await bearer.StartAsync(default);
        _ = await error.Task.WaitAsync(TestSupport.Patience);
        await TestSupport.WaitFor(() => bearer.Peers.Count == 0);
        await bearer.SendAsync(TestSupport.MakePacket(1), default);
    }

    [Fact]
    public async Task OversizedSamReplyAndSetupTimeoutAreBounded()
    {
        await using (var simulator = new Parity6I2pSamSimulator { HelloReply = new string('x', 16385) + "\n" })
        {
            var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var bearer = new I2pPacketInterface(Options(simulator, ".") with { Peers = ["peer.i2p"], MaxReconnectAttempts = 0 });
            bearer.Diagnostic += exception => error.TrySetResult(exception);
            await bearer.StartAsync(default);
            Assert.IsType<InvalidDataException>(await error.Task.WaitAsync(TestSupport.Patience));
        }
        await using (var simulator = new Parity6I2pSamSimulator { HangHello = true })
        {
            var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var bearer = new I2pPacketInterface(Options(simulator, ".") with
            {
                Peers = ["peer.i2p"], MaxReconnectAttempts = 0, SetupTimeout = TimeSpan.FromMilliseconds(80),
            });
            bearer.Diagnostic += exception => error.TrySetResult(exception);
            await bearer.StartAsync(default);
            Assert.IsType<TimeoutException>(await error.Task.WaitAsync(TestSupport.Patience));
        }
    }

    [Fact]
    public async Task ConnectFailureRetriesExactlyConfiguredTimesAndReportsSamResult()
    {
        await using var simulator = new Parity6I2pSamSimulator { ConnectResult = "CANT_REACH_PEER" };
        await using var bearer = new I2pPacketInterface(Options(simulator, ".") with { Peers = ["peer.i2p"], MaxReconnectAttempts = 2 });
        I2pPacketPeer? child = null;
        bearer.ChildConnected += peer => child = Assert.IsType<I2pPacketPeer>(peer);
        var failures = new List<string>();
        bearer.Diagnostic += error => { lock (failures) failures.Add(error.Message); };
        await bearer.StartAsync(default);
        await TestSupport.WaitFor(() => simulator.ConnectAttempts == 3 && bearer.Peers.Count == 0);
        lock (failures) Assert.All(failures, failure => Assert.Contains("CANT_REACH_PEER", failure));
        Assert.Equal(1, simulator.Generated);
        var diagnostics = Assert.IsType<I2pPacketPeer>(child).GetDiagnostics();
        Assert.Equal("stopped", diagnostics["setup_phase"]);
        Assert.Equal("stream_connect", diagnostics["last_failure_phase"]);
        Assert.Contains("CANT_REACH_PEER", diagnostics["last_error"]?.ToString() ?? "");
        Assert.Equal(1, Convert.ToInt32(diagnostics["session_attempts"]));
        Assert.Equal(3, Convert.ToInt32(diagnostics["stream_connect_attempts"]));
        Assert.Single(simulator.SessionIds);
        Assert.Single(simulator.StreamSessionIds.Distinct());
    }

    [Fact]
    public async Task PendingStreamReplyKeepsSessionUntilSetupDeadlineAndReportsExactPhase()
    {
        await using var simulator = new Parity6I2pSamSimulator { HangConnect = true };
        await using var bearer = new I2pPacketInterface(Options(simulator, ".") with
        {
            Peers = ["peer.i2p"], MaxReconnectAttempts = 0, SetupTimeout = TimeSpan.FromSeconds(2),
        });
        var ready = new TaskCompletionSource<I2pPacketPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        bearer.ChildConnected += peer => ready.TrySetResult(Assert.IsType<I2pPacketPeer>(peer));
        bearer.Diagnostic += error => failure.TrySetResult(error);
        await bearer.StartAsync(default);
        var child = await ready.Task.WaitAsync(TestSupport.Patience);
        await TestSupport.WaitFor(() => simulator.ConnectAttempts == 1);
        Assert.Equal(1, simulator.ActiveSessionCount);
        Assert.False(child.IsConnected);
        Assert.Equal("stream_connect", child.GetDiagnostics()["setup_phase"]);
        var error = Assert.IsType<TimeoutException>(await failure.Task.WaitAsync(TestSupport.Patience));
        Assert.Contains("stream_connect", error.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        await TestSupport.WaitFor(() => bearer.Peers.Count == 0 && simulator.ActiveSessionCount == 0);
        var diagnostics = child.GetDiagnostics();
        Assert.Equal("stream_connect", diagnostics["last_failure_phase"]);
        Assert.Equal(1, Convert.ToInt32(diagnostics["session_attempts"]));
        Assert.Equal(1, Convert.ToInt32(diagnostics["stream_connect_attempts"]));
        Assert.Equal(1, simulator.Generated);
        Assert.DoesNotContain(simulator.PrivateDestination, diagnostics["last_error"]?.ToString() ?? "");
    }

    [Fact]
    public async Task DisposeCancelsHungSetupAndParentReceiveAndIsIdempotent()
    {
        await using var simulator = new Parity6I2pSamSimulator { HangHello = true };
        var bearer = new I2pPacketInterface(Options(simulator, ".") with { Peers = ["peer.i2p"] });
        await bearer.StartAsync(default);
        await using var empty = bearer.ReceiveAsync(default).GetAsyncEnumerator();
        var pending = empty.MoveNextAsync().AsTask();
        await TestSupport.WaitFor(() => simulator.ActiveConnections > 0);
        await bearer.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
        await bearer.DisposeAsync();
        Assert.False(await pending.WaitAsync(TestSupport.Patience));
        Assert.Throws<ObjectDisposedException>(() => bearer.ReceiveAsync(default));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bearer.StartAsync(default).AsTask());
    }

    [Fact]
    public async Task ReceiveBackpressureCancellationKeepaliveAndMalformedFramesRemainBounded()
    {
        await using var simulator = new Parity6I2pSamSimulator();
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var ready = new TaskCompletionSource<I2pPacketPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bearer = new I2pPacketInterface(Options(simulator, ".") with
        {
            Peers = ["peer.i2p"], ReceiveQueueCapacity = 1, KeepAliveInterval = TimeSpan.FromMilliseconds(30),
        });
        bearer.ChildConnected += peer => ready.TrySetResult(Assert.IsType<I2pPacketPeer>(peer));
        await bearer.StartAsync(timeout.Token);
        var remote = await simulator.OutgoingAsync(timeout.Token);
        var child = await ready.Task.WaitAsync(timeout.Token);
        var keepalive = new byte[2];
        await remote.ReadExactlyAsync(keepalive, timeout.Token);
        Assert.Equal(new byte[] { 0x7e, 0x7e }, keepalive);
        var oversized = new byte[] { 0x7e }.Concat(Enumerable.Repeat((byte)1, 1065)).Append((byte)0x7e).ToArray();
        await remote.WriteAsync(oversized, timeout.Token);
        await remote.WriteAsync(new byte[] { 0x7e, 1, 2, 0x7d, 0x01, 0x7e, 0x7e, 0x7e }, timeout.Token);
        var frames = Enumerable.Range(0, 8).SelectMany(i => HdlcPacketFraming.Encode(TestSupport.MakePacket(i))).ToArray();
        await remote.WriteAsync(frames, timeout.Token);
        await TestSupport.WaitFor(() => Convert.ToInt64(child.GetDiagnostics()["rxb"]) >= TestSupport.MakePacket(0).Length * 2);
        using var receiveCancellation = new CancellationTokenSource();
        await using var receiver = child.ReceiveAsync(receiveCancellation.Token).GetAsyncEnumerator();
        for (var i = 0; i < 8; i++) Assert.Equal(i, TestSupport.TagOf(await TestSupport.Next(receiver)));
        receiveCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.MoveNextAsync().AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => child.SendAsync(new byte[1065], default).AsTask());
        await bearer.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
    }

    [Fact]
    public async Task IncomingPeerCapRejectsAdditionalStreamsAndDisposalEndsAccept()
    {
        var directory = OwnedDirectory();
        try
        {
            await using var simulator = new Parity6I2pSamSimulator();
            using var timeout = new CancellationTokenSource(TestSupport.Patience);
            await using var bearer = new I2pPacketInterface(Options(simulator, directory, true) with { MaxIncomingPeers = 1 });
            await bearer.StartAsync(timeout.Token);
            _ = await simulator.IncomingAsync(ReadOnlyMemory<byte>.Empty, timeout.Token);
            await TestSupport.WaitFor(() => bearer.Peers.Count == 1);
            _ = await simulator.IncomingAsync(ReadOnlyMemory<byte>.Empty, timeout.Token);
            await TestSupport.WaitFor(() => Convert.ToInt64(bearer.GetDiagnostics()["rejected_clients"]) == 1);
            Assert.Single(bearer.Peers);
            await bearer.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
            Assert.Empty(bearer.Peers);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SessionFailureDoesNotExposePrivateReplyAndParentDisposalCancelsPendingAccept()
    {
        var directory = OwnedDirectory();
        try
        {
            await using var simulator = new Parity6I2pSamSimulator { SessionResult = "DUPLICATED_DEST" };
            var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var bearer = new I2pPacketInterface(Options(simulator, directory, true) with { MaxReconnectAttempts = 0 });
            bearer.Diagnostic += failure => error.TrySetResult(failure);
            await bearer.StartAsync(default);
            var failure = await error.Task.WaitAsync(TestSupport.Patience);
            Assert.Contains("DUPLICATED_DEST", failure.Message);
            Assert.DoesNotContain(simulator.PrivateDestination, failure.Message);
            Assert.DoesNotContain(simulator.PrivateDestination, bearer.GetDiagnostics()["last_error"]?.ToString() ?? "");
            await bearer.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("not-valid-base64")]
    [InlineData("!!!!")]
    [InlineData("AAAA")]
    [InlineData("bad peer token")]
    [InlineData("")]
    public async Task MalformedAcceptedDestinationIsIsolatedAndNoSubscriberPathStillProcessesPackets(string malformedDestination)
    {
        var directory = OwnedDirectory();
        try
        {
            await using var simulator = new Parity6I2pSamSimulator();
            using var timeout = new CancellationTokenSource(TestSupport.Patience);
            var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var bearer = new I2pPacketInterface(Options(simulator, directory, true));
            bearer.Diagnostic += failure => error.TrySetResult(failure);
            await bearer.StartAsync(timeout.Token);
            _ = await simulator.IncomingAsync(ReadOnlyMemory<byte>.Empty, timeout.Token, malformedDestination);
            var failure = Assert.IsType<InvalidDataException>(await error.Task.WaitAsync(timeout.Token));
            Assert.Equal("SAM supplied an invalid peer destination.", failure.Message);
            Assert.True(failure.InnerException is ArgumentException or FormatException);
            var packet = TestSupport.MakePacket(89);
            var remote = await simulator.IncomingAsync(HdlcPacketFraming.Encode(packet), timeout.Token);
            await TestSupport.WaitFor(() => bearer.Peers.Count == 1);
            var peer = Assert.Single(bearer.Peers);
            await using var received = peer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
            Assert.Equal(packet, (await TestSupport.Next(received)).ToArray());
            await peer.SendAsync(packet, timeout.Token);
            Assert.Equal(packet, await Parity6I2pSamSimulator.PacketAsync(remote, timeout.Token));
            Assert.Equal(packet.Length, Convert.ToInt32(bearer.GetDiagnostics()["rxb"]));
            Assert.True(Convert.ToInt64(bearer.GetDiagnostics()["txb"]) > packet.Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SilentPeerBecomesStaleThenReconnectsAfterReadTimeout()
    {
        await using var simulator = new Parity6I2pSamSimulator();
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var ready = new TaskCompletionSource<I2pPacketPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleAfter = TimeSpan.FromMilliseconds(80);
        await using var bearer = new I2pPacketInterface(Options(simulator, ".") with
        {
            Peers = ["peer.i2p"], KeepAliveInterval = TimeSpan.FromMilliseconds(20),
            StaleAfter = staleAfter, ReadTimeout = TimeSpan.FromMilliseconds(400),
        });
        bearer.ChildConnected += peer => ready.TrySetResult(Assert.IsType<I2pPacketPeer>(peer));
        await bearer.StartAsync(timeout.Token);
        var child = await ready.Task.WaitAsync(timeout.Token);
        _ = await simulator.OutgoingAsync(timeout.Token);
        await TestSupport.WaitFor(() => Equals("stale", child.GetDiagnostics()["i2p_tunnel_state"]));
        _ = await simulator.OutgoingAsync(timeout.Token);
        Assert.Same(child, Assert.Single(bearer.Peers));
        Assert.Equal(1, simulator.SessionCount);
        Assert.Single(simulator.StreamSessionIds.Distinct());
    }

    [Fact]
    public async Task CancelledStartCanBeRetriedAndReceiveAndSendHonorCallerCancellation()
    {
        await using var simulator = new Parity6I2pSamSimulator();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await using var bearer = new I2pPacketInterface(Options(simulator, ".") with { Peers = ["peer.i2p"] });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bearer.StartAsync(canceled.Token).AsTask());
        await bearer.StartAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bearer.StartAsync(default).AsTask());
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var remote = await simulator.OutgoingAsync(timeout.Token);
        var child = Assert.Single(bearer.Peers);
        await TestSupport.WaitFor(() => child.IsConnected);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child.SendAsync(TestSupport.MakePacket(1), canceled.Token).AsTask());
        await child.SendAsync(TestSupport.MakePacket(2), timeout.Token);
        Assert.Equal(2, TestSupport.TagOf(await Parity6I2pSamSimulator.PacketAsync(remote, timeout.Token)));
        await using var receiver = child.ReceiveAsync(canceled.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.MoveNextAsync().AsTask());
    }

    [Fact]
    public void InvalidOptionsAndDestinationStructuresAreRejectedWithoutIo()
    {
        var basis = new I2pPacketInterfaceOptions { Name = "test", StoragePath = ".", SamHost = "127.0.0.1" };
        Assert.Throws<ArgumentException>(() => new I2pPacketInterface(basis with { Connectable = true }));
        Assert.Throws<ArgumentException>(() => new I2pPacketInterface(basis with { Peers = ["peer.i2p\nDEST GENERATE"] }));
        Assert.Throws<ArgumentException>(() => new I2pPacketInterface(basis with { MaxOutgoingPeers = 1, Peers = ["a", "b"] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new I2pPacketInterface(basis with { SamPort = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new I2pPacketInterface(basis with { ReceiveQueueCapacity = 0 }));
        Assert.Throws<ArgumentException>(() => I2pDestination.PublicBase32(new byte[386]));
        Assert.Throws<ArgumentException>(() => I2pDestination.KeyFileName("test", new byte[32]));
    }

    [Fact]
    public void RemoteSamDestinationBoundaryPreservesStructuralCauseWithoutChangingCallerArgumentContract()
    {
        var malformed = new byte[387];
        malformed[386] = 1; // Certificate declares one byte not present in the structure.
        var error = Assert.Throws<InvalidDataException>(() =>
            I2pDestination.Base32FromSamPublic(Convert.ToBase64String(malformed)));
        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Throws<ArgumentException>(() => I2pDestination.PublicBase32(malformed));
    }

    [Theory]
    [InlineData(null, 256000)]
    [InlineData(4L, 256000)]
    [InlineData(5L, 5)]
    [InlineData(1000000L, 1000000)]
    public async Task GenericConfiguredBitrateThresholdMatchesSourceWithoutChangingFixedMtu(long? configured, long expected)
    {
        using var fixture = Vectors();
        Assert.Equal(5, fixture.RootElement.GetProperty("minimumConfiguredBitrate").GetInt32());
        await using var bearer = new I2pPacketInterface(new I2pPacketInterfaceOptions
        {
            Name = "test", StoragePath = ".", SamHost = "127.0.0.1", ConfiguredBitrate = configured,
        });
        Assert.Equal(expected, bearer.Bitrate);
        Assert.Equal(expected, Convert.ToInt64(bearer.GetDiagnostics()["bitrate"]));
        Assert.Equal(1064, bearer.HardwareMtu);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task ChildPolicyExposesSourceOutgoingAndParentAccountingExceptions(
        bool initiator, bool inheritParentOutgoing, bool countParentTraffic)
    {
        var options = new I2pPacketInterfaceOptions { Name = "policy", StoragePath = ".", SamHost = "127.0.0.1" };
        await using var parent = new I2pPacketInterface(options);
        await using var peer = new I2pPacketPeer(parent, options, "policy-peer.i2p", initiator);
        var policy = Assert.IsAssignableFrom<IPacketInterfaceChildPolicy>(peer);
        Assert.Equal(initiator, peer.IsInitiator);
        Assert.Equal(inheritParentOutgoing, policy.InheritParentOutgoing);
        Assert.True(policy.Outgoing);
        Assert.Equal(countParentTraffic, policy.CountParentTraffic);
    }
}
