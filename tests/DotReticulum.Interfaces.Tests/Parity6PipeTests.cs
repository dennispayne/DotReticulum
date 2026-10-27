using System.Diagnostics;
using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Interfaces.Tests;

// Reference: a29bfb51c468945c96a1ee22cae0114e993e2391; independent generator records actual imports/source hashes.
[Trait("Category", "Integration")]
public sealed class Parity6PipeTests
{
    private static string Fixture()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "DotReticulum.Interfaces.Tests", "Parity6PipeChild.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Run these source-tree tests from the repository containing Parity6PipeChild.ps1.");
    }

    private static PipePacketInterface Child(string mode = "echo", string? hex = null, PipePacketInterfaceOptions? options = null)
    {
        var arguments = new List<string> { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Fixture(), "-Mode", mode };
        if (hex is not null) arguments.AddRange(["-Hex", hex]);
        return new("pwsh", arguments, options);
    }

    [Fact]
    public void FramingMatchesReferenceAndDecoderHardeningIsExplicit()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-backbone-pipe.json")));
        foreach (var item in vectors.RootElement.GetProperty("encode").EnumerateArray())
        {
            var raw = Convert.FromHexString(item.GetProperty("raw").GetString()!);
            var framed = HdlcPacketFraming.Encode(raw, 1064, false);
            Assert.Equal(Convert.FromHexString(item.GetProperty("pipe").GetString()!), framed);
            Assert.Equal(framed.Length, item.GetProperty("pipe_txb").GetInt64());
        }
        foreach (var item in vectors.RootElement.GetProperty("decode").EnumerateArray())
        {
            var wire = Convert.FromHexString(item.GetProperty("wire").GetString()!);
            foreach (var chunk in new[] { 1, 7, 32768 })
            {
                var decoder = new PipeFrameDecoder(1064);
                var frames = new List<byte[]>();
                for (var offset = 0; offset < wire.Length; offset += chunk)
                    decoder.Feed(wire.AsSpan(offset, Math.Min(chunk, wire.Length - offset)), frames);
                if (item.GetProperty("pipe_hardening").GetBoolean()) Assert.Empty(frames);
                else Assert.Equal(item.GetProperty("pipe").EnumerateArray().Select(frame => frame.GetString()),
                    frames.Select(frame => Convert.ToHexStringLower(frame)));
            }
        }
    }

    [Fact]
    public void EmptyTruncatedBadEscapeAndOversizedFramesAreDiscardedAndDecoderRecovers()
    {
        var decoder = new PipeFrameDecoder(20);
        var frames = new List<byte[]>();
        decoder.Feed([0x7e, 0x7e, 0x7e, 1, 0x7d, 0x7e], frames);
        Assert.Empty(frames);
        decoder.Feed([0x7e, 1, 0x7d, 0x01, 0x7e], frames);
        decoder.Feed(new byte[] { 0x7e }.Concat(Enumerable.Repeat((byte)1, 21)).Append((byte)0x7e).ToArray(), frames);
        Assert.Empty(frames);
        var boundary = Enumerable.Repeat((byte)0x7e, 20).ToArray();
        decoder.Feed(HdlcPacketFraming.Encode(boundary, 20, false), frames);
        Assert.Equal(boundary, Assert.Single(frames));
        decoder.Feed([0x7e, 1], frames);
        Assert.Single(frames); // No completion delimiter: no success-shaped partial frame.
    }

    [Theory]
    [InlineData("echo")]
    [InlineData("stderr-and-echo")]
    public async Task OwnedChildEchoesBinaryStdioWithoutObserversAndAccountsWireBytes(string mode)
    {
        await using var pipe = Child(mode);
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await pipe.StartAsync(timeout.Token);
        var packet = TestSupport.MakePacket(17);
        await pipe.SendAsync(packet, timeout.Token);
        await using var reader = pipe.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.Equal(packet, (await TestSupport.Next(reader)).ToArray());
        Assert.Equal(packet.Length, (long)pipe.GetDiagnostics()["rxb"]!);
        Assert.Equal(HdlcPacketFraming.Encode(packet).Length, (long)pipe.GetDiagnostics()["txb"]!);
        Assert.True(pipe.IsSharedMedium);
        Assert.False(pipe.SupportsMtuDiscovery);
    }

    [Fact]
    public async Task ExecutableArgumentsAreNotShellExpandedOrResplit()
    {
        await using var pipe = new PipePacketInterface("pwsh",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", Fixture(), "-ExpectedArgument", "a b \"c\""]);
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await pipe.StartAsync(timeout.Token);
        await using var reader = pipe.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        var packet = TestSupport.MakePacket(3);
        await pipe.SendAsync(packet, timeout.Token);
        Assert.Equal(packet, (await TestSupport.Next(reader)).ToArray());
    }

    [Fact]
    public async Task ConcurrentSendsRemainWholeFramesAtMtuBoundary()
    {
        await using var pipe = Child();
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await pipe.StartAsync(timeout.Token);
        await using var reader = pipe.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        var packets = Enumerable.Range(0, 8).Select(i =>
        {
            var payload = new byte[pipe.HardwareMtu - Packet.Header1Size];
            payload[0] = (byte)i;
            payload[1] = (byte)(i >> 8);
            payload[2] = 0x7e;
            payload[3] = 0x7d;
            return Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], payload,
                mtu: pipe.HardwareMtu).ToArray();
        }).ToArray();
        Assert.All(packets, packet => Assert.Equal(1064, packet.Length));
        var sends = packets.Select(packet => pipe.SendAsync(packet, timeout.Token).AsTask()).ToArray();
        await Task.WhenAll(sends);
        var tags = new HashSet<int>();
        for (var i = 0; i < packets.Length; i++)
        {
            var received = await TestSupport.Next(reader);
            Assert.Equal(1064, received.Length);
            var payload = Packet.Parse(received, mtu: pipe.HardwareMtu).Payload;
            var tag = payload.Span[0] | (payload.Span[1] << 8);
            Assert.InRange(tag, 0, packets.Length - 1);
            Assert.Equal(packets[tag], received.ToArray());
            tags.Add(tag);
        }
        Assert.Equal(Enumerable.Range(0, 8), tags.OrderBy(tag => tag));
        await Assert.ThrowsAsync<ArgumentException>(() => pipe.SendAsync(new byte[1065], default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => pipe.SendAsync(ReadOnlyMemory<byte>.Empty, default).AsTask());
    }

    [Fact]
    public async Task ActualStdoutSkipsMalformedFramesThenReceivesValidFrame()
    {
        var packet = TestSupport.MakePacket(71);
        var malformed = new byte[] { 0x7e, 1, 0x7d, 0x01, 0x7e };
        var output = malformed.Concat(HdlcPacketFraming.Encode(packet)).ToArray();
        await using var pipe = Child("emit-hex-and-wait", Convert.ToHexString(output));
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await pipe.StartAsync(timeout.Token);
        await using var reader = pipe.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.Equal(packet, (await TestSupport.Next(reader)).ToArray());
    }

    [Fact]
    public async Task ExitIsDiagnosedRespawnedAndDisposalStopsFurtherRespawns()
    {
        var options = new PipePacketInterfaceOptions { RespawnDelay = TimeSpan.FromMilliseconds(30) };
        await using var pipe = Child("exit-immediately", options: options);
        var connected = 0;
        var disconnected = 0;
        pipe.Connected += () => throw new InvalidOperationException("optional observer");
        pipe.Connected += () => Interlocked.Increment(ref connected);
        pipe.Disconnected += () => Interlocked.Increment(ref disconnected);
        await pipe.StartAsync(default);
        await TestSupport.WaitFor(() => Volatile.Read(ref connected) >= 2 && Volatile.Read(ref disconnected) >= 1);
        Assert.Equal(23, pipe.GetDiagnostics()["last_exit_code"]);
        await pipe.DisposeAsync();
        Assert.NotNull(pipe.GetDiagnostics()["last_error"]);
        Assert.NotNull(pipe.GetDiagnostics()["last_exit_code"]);
        var respawns = (int)pipe.GetDiagnostics()["respawn_attempts"]!;
        await Task.Delay(100);
        Assert.Equal(respawns, (int)pipe.GetDiagnostics()["respawn_attempts"]!);
        Assert.False(pipe.IsConnected);
    }

    [Fact]
    public async Task InitialSpawnFailureAndCanceledStartDoNotPretendToConnect()
    {
        await using var missing = new PipePacketInterface(Path.Combine(Environment.CurrentDirectory, Guid.NewGuid() + ".missing"));
        await Assert.ThrowsAnyAsync<Exception>(() => missing.StartAsync(default).AsTask());
        Assert.False(missing.IsConnected);
        Assert.Equal(1L, missing.GetDiagnostics()["spawn_failures"]);
        await using var canceled = Child();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.StartAsync(cancellation.Token).AsTask());
        Assert.False(canceled.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => canceled.SendAsync(TestSupport.MakePacket(1), default).AsTask());
    }

    [Fact]
    public async Task RepeatedRespawnFailuresAreDiagnosedAndFiniteRetryExhaustionFaultsReceive()
    {
        var starts = 0;
        await using var pipe = new PipePacketInterface("pwsh",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", Fixture(), "-Mode", "exit-immediately"],
            new() { RespawnDelay = TimeSpan.FromMilliseconds(20), MaximumRespawnAttempts = 2 },
            info => Interlocked.Increment(ref starts) == 1
                ? Process.Start(info) ?? throw new IOException("Owned fixture failed to start.")
                : throw new IOException("Injected executable-unavailable failure before process creation."));
        await pipe.StartAsync(default);
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await using var reader = pipe.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        await Assert.ThrowsAsync<IOException>(() => reader.MoveNextAsync().AsTask());
        Assert.Equal(2L, pipe.GetDiagnostics()["spawn_failures"]);
        Assert.Equal(2, pipe.GetDiagnostics()["respawn_attempts"]);
        Assert.False(pipe.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => pipe.SendAsync(TestSupport.MakePacket(1), default).AsTask());
    }

    [Fact]
    public async Task CanceledSendDoesNotEmitOrCountAFrame()
    {
        await using var pipe = Child();
        await pipe.StartAsync(default);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipe.SendAsync(TestSupport.MakePacket(1), canceled.Token).AsTask());
        Assert.Equal(0L, pipe.GetDiagnostics()["txb"]);
        Assert.True(pipe.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipe.StartAsync(default).AsTask());
    }

    [Fact]
    public async Task ReceiveCancellationAndIdempotentDisposeCleanUpOnlyOwnedChild()
    {
        await using var pipe = Child();
        await pipe.StartAsync(default);
        var pid = (int)pipe.GetDiagnostics()["pid"]!;
        using var cancellation = new CancellationTokenSource();
        await using var reader = pipe.ReceiveAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Task.WhenAll(pipe.DisposeAsync().AsTask(), pipe.DisposeAsync().AsTask()).WaitAsync(TestSupport.Patience);
        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.HasExited);
        }
        catch (ArgumentException) { } // OS has already removed this known owned PID.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pipe.StartAsync(default).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pipe.SendAsync(TestSupport.MakePacket(1), default).AsTask());
    }

    [Fact]
    public async Task FullReceiveQueueDoesNotPreventDisposalOrLeaveProcessRunning()
    {
        var wire = HdlcPacketFraming.Encode(TestSupport.MakePacket(5));
        var output = wire.Concat(wire).Concat(wire).ToArray();
        await using var pipe = Child("emit-hex-and-wait", Convert.ToHexString(output),
            new() { ReceiveQueueFrames = 1 });
        await pipe.StartAsync(default);
        await TestSupport.WaitFor(() => (int)pipe.GetDiagnostics()["receive_queue_frames"]! == 1);
        await pipe.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
        Assert.False(pipe.IsConnected);
    }

    [Fact]
    public async Task DisposalCompletesActiveReceiveRatherThanLeavingItBlocked()
    {
        await using var pipe = Child();
        await pipe.StartAsync(default);
        await using var reader = pipe.ReceiveAsync(default).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        await pipe.DisposeAsync();
        Assert.False(await pending.WaitAsync(TestSupport.Patience));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidQueueBoundsAreRejected(int frames) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipePacketInterface("pwsh", options: new() { ReceiveQueueFrames = frames }));

    [Fact]
    public async Task GenericConfiguredBitrateOverridesOnlyTheEstimateNotTheFixedMtu()
    {
        await using var standard = new PipePacketInterface("pwsh");
        await using var configured = new PipePacketInterface("pwsh", options: new() { ConfiguredBitrate = 64000 });
        Assert.Equal(1_000_000, standard.Bitrate);
        Assert.Equal(64000, configured.Bitrate);
        Assert.Equal(1064, configured.HardwareMtu);
        Assert.False(configured.SupportsMtuDiscovery);
        Assert.Equal(64000L, configured.GetDiagnostics()["bitrate"]);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void NonPositiveConfiguredBitrateIsRejected(long bitrate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PipePacketInterface("pwsh", options: new() { ConfiguredBitrate = bitrate }));
}
