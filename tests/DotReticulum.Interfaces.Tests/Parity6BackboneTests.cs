using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces.Tests;

// Source-backed by GenerateParity6BackbonePipeVectors.py at a29bfb51c468945c96a1ee22cae0114e993e2391.
// Internal endpoint socket tests are software loopback, not a supported Windows Backbone or live RNS claim.
[Trait("Category", "Integration")]
public sealed class Parity6BackboneTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-backbone-pipe.json")));

    [Fact]
    public void FramingAndFragmentedDecodeMatchPinnedReference()
    {
        using var vectors = Vectors();
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", vectors.RootElement.GetProperty("revision").GetString());
        Assert.Equal("1.5.7", vectors.RootElement.GetProperty("version").GetString());
        foreach (var item in vectors.RootElement.GetProperty("encode").EnumerateArray())
            Assert.Equal(Convert.FromHexString(item.GetProperty("backbone").GetString()!),
                HdlcPacketFraming.Encode(Convert.FromHexString(item.GetProperty("raw").GetString()!), 1064, false));
        foreach (var item in vectors.RootElement.GetProperty("decode").EnumerateArray())
        {
            var wire = Convert.FromHexString(item.GetProperty("wire").GetString()!);
            var expected = item.GetProperty("backbone").EnumerateArray().Select(frame => frame.GetString()!).ToArray();
            foreach (var chunk in new[] { 1, 7, 32768 })
            {
                var decoder = new BackboneFrameDecoder(516);
                var frames = new List<byte[]>();
                for (var offset = 0; offset < wire.Length; offset += chunk)
                    decoder.Feed(wire.AsSpan(offset, Math.Min(chunk, wire.Length - offset)), frames);
                Assert.Equal(expected, frames.Select(frame => Convert.ToHexStringLower(frame)).ToArray());
            }
        }
    }

    [Fact]
    public void OversizedAndTruncatedFramesRecoverAtNextDelimiter()
    {
        var decoder = new BackboneFrameDecoder(500);
        var frames = new List<byte[]>();
        decoder.Feed(new byte[] { 0x7e }.Concat(Enumerable.Repeat((byte)1, 10001)).ToArray(), frames);
        Assert.Empty(frames);
        var valid = TestSupport.MakePacket(7);
        decoder.Feed(HdlcPacketFraming.Encode(valid), frames);
        Assert.Single(frames);
        Assert.Equal(valid, frames[0]);
    }

    [Fact]
    public async Task TransmitRetainsOffsetsAcrossPartialZeroInterruptedAndWouldBlockWrites()
    {
        var buffer = new BackboneTransmitBuffer(8, 2);
        Assert.True(buffer.Append([1, 2, 3, 4]));
        Assert.True(buffer.Append([5, 6, 7, 8]));
        Assert.False(buffer.Append([9]));
        var script = new Queue<int>([2, 0, -1, -2, 2, 4]);
        var received = new List<byte>();
        ValueTask<int> Send(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            var next = script.Dequeue();
            if (next == -1) throw new SocketException((int)SocketError.Interrupted);
            if (next == -2) throw new SocketException((int)SocketError.WouldBlock);
            received.AddRange(bytes.Span[..next].ToArray());
            return ValueTask.FromResult(next);
        }
        for (var i = 0; i < 4; i++) await buffer.DrainAsync(Send, default);
        Assert.Equal(6, buffer.Buffered);
        Assert.Equal(2, buffer.Sent);
        Assert.Equal(2, buffer.Frames);
        await buffer.DrainAsync(Send, default);
        Assert.Equal(1, buffer.Frames);
        await buffer.DrainAsync(Send, default);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, received);
        Assert.Equal(0, buffer.Buffered);
        Assert.Equal(8, buffer.Sent);
        Assert.True(buffer.Append([9]));
    }

    [Fact]
    public async Task InvalidWriteCountsAndCancellationDoNotAdvanceAccounting()
    {
        var buffer = new BackboneTransmitBuffer(100, 1);
        buffer.Append([1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() =>
            buffer.DrainAsync((_, _) => ValueTask.FromResult(4), default).AsTask());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buffer.DrainAsync(
            (_, token) => ValueTask.FromCanceled<int>(token), canceled.Token).AsTask());
        Assert.Equal(3, buffer.Buffered);
        Assert.Equal(0, buffer.Sent);
    }

    [Fact]
    public void EgressWatermarksAndRecoveryMatchReference()
    {
        using var vectors = Vectors();
        var control = new BackboneEgressControl(0);
        foreach (var state in vectors.RootElement.GetProperty("egress").EnumerateArray())
        {
            Assert.Equal(state.GetProperty("dead").GetBoolean(), control.Evaluate(
                state.GetProperty("buffered").GetInt32(), state.GetProperty("sent").GetInt64(), state.GetProperty("now").GetDouble()));
            Assert.Equal(state.GetProperty("stalled").GetBoolean(), control.Stalled);
        }
        var stalled = new BackboneEgressControl(0);
        Assert.False(stalled.Evaluate(200000, 0, 11.99));
        Assert.True(stalled.Evaluate(200000, 1, 12)); // Upstream tests dead-time before observing this tick's drain.
        var boundary = new BackboneEgressControl(0);
        for (var tick = 1; tick < 10; tick++) Assert.False(boundary.Evaluate(128 * 1024, 0, tick));
        Assert.False(boundary.Stalled);
    }

    [Fact]
    public void FastFlapGraceExpiryThresholdAndBoundedTableAreExplicit()
    {
        var options = new BackbonePacketInterfaceOptions { FastFlapGrace = 1, MaximumFlapEntries = 1 };
        var table = new BackboneFlapTable(options);
        table.Disconnected("a", 20, 0);
        Assert.False(table.IsBlocked("a", 0));
        table.Disconnected("a", 19.99, 0);
        Assert.False(table.IsBlocked("a", 0));
        Assert.True(table.IsBlocked("b", 0)); // Bounded fail-closed table, not eviction of existing offenders.
        table.Disconnected("a", 1, 1);
        Assert.True(table.IsBlocked("a", 1));
        Assert.Equal(1, table.BlockedCount(43201));
        Assert.False(table.IsBlocked("a", 43201.01));
        var disabled = new BackboneFlapTable(options with { BlockFastFlapping = false });
        disabled.Disconnected("a", 1, 1);
        Assert.False(disabled.IsBlocked("a", 1));
    }

    [Fact]
    public async Task InternalEndpointReceivesWithoutObserversAndCountsWrittenWireBytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var accepted = await listener.AcceptSocketAsync();
        long received = 0, sent = 0;
        var endpoint = new BackbonePeerEndpoint(accepted, new() { HardwareMtu = 500 },
            (count, output) => { if (output) Interlocked.Add(ref sent, count); else Interlocked.Add(ref received, count); }, () => { });
        endpoint.Start();
        try
        {
            using var timeout = new CancellationTokenSource(TestSupport.Patience);
            var packet = TestSupport.MakePacket(42);
            var wire = HdlcPacketFraming.Encode(packet);
            await client.SendAsync(wire, SocketFlags.None, timeout.Token);
            await using var reader = endpoint.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
            Assert.Equal(packet, (await TestSupport.Next(reader)).ToArray());
            Assert.Equal(packet.Length, Interlocked.Read(ref received));
            await endpoint.SendAsync(packet, timeout.Token);
            var output = new byte[wire.Length];
            var count = 0;
            while (count < output.Length)
                count += await client.ReceiveAsync(output.AsMemory(count), SocketFlags.None, timeout.Token);
            Assert.Equal(wire, output);
            await TestSupport.WaitFor(() => Interlocked.Read(ref sent) == wire.Length);
        }
        finally { await endpoint.DisposeAsync(); }
        await endpoint.DisposeAsync();
    }

    [Fact]
    public async Task InternalEndpointDisposalUnblocksFullReceiveQueue()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var endpoint = new BackbonePeerEndpoint(await listener.AcceptSocketAsync(),
            new() { HardwareMtu = 500, ReceiveQueueFrames = 1 }, (_, _) => { }, () => { });
        endpoint.Start();
        var wire = HdlcPacketFraming.Encode(TestSupport.MakePacket(1));
        await client.SendAsync(wire.Concat(wire).Concat(wire).ToArray(), SocketFlags.None);
        await TestSupport.WaitFor(() => (int)endpoint.GetDiagnostics()["receive_queue_frames"]! == 1);
        await endpoint.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
        await Assert.ThrowsAsync<IOException>(() => endpoint.SendAsync(TestSupport.MakePacket(1), default).AsTask());
    }

    [UnsupportedBackboneFact]
    public async Task UnsupportedPlatformFailsExplicitly()
    {
        await using var listener = new BackbonePacketInterface(new(IPAddress.Loopback, 0));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => listener.StartAsync(default).AsTask());
        await using var client = new BackboneClientPacketInterface("127.0.0.1", 1);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => client.StartAsync(default).AsTask());
    }

    [LinuxBackboneFact]
    public async Task DistinctPeersDisconnectIsolation()
    {
        var children = Channel.CreateBounded<IPacketInterface>(4);
        var options = new BackbonePacketInterfaceOptions { HardwareMtu = 500, ReconnectDelay = TimeSpan.FromMilliseconds(30) };
        await using var listener = new BackbonePacketInterface(new(IPAddress.Loopback, 0), options);
        listener.ChildConnected += child => children.Writer.TryWrite(child);
        await listener.StartAsync(default);
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await using var first = new BackboneClientPacketInterface("127.0.0.1", listener.LocalEndpoint!.Port, options);
        await using var second = new BackboneClientPacketInterface("127.0.0.1", listener.LocalEndpoint.Port, options);
        await first.StartAsync(timeout.Token);
        var one = await children.Reader.ReadAsync(timeout.Token);
        await second.StartAsync(timeout.Token);
        var two = await children.Reader.ReadAsync(timeout.Token);
        Assert.NotSame(one, two);
        await using var reader = two.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        await first.DisposeAsync();
        await second.SendAsync(TestSupport.MakePacket(2), timeout.Token);
        Assert.Equal(2, TestSupport.TagOf(await TestSupport.Next(reader)));
        await Assert.ThrowsAsync<NotSupportedException>(() => listener.SendAsync(TestSupport.MakePacket(1), default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => listener.StartAsync(default).AsTask());
    }

    [LinuxBackboneFact]
    public async Task InitiatorReconnectsWithStableIdentityAfterListenerRestart()
    {
        var options = new BackbonePacketInterfaceOptions
        {
            HardwareMtu = 500, ReconnectDelay = TimeSpan.FromMilliseconds(30), ConnectTimeout = TimeSpan.FromSeconds(1),
        };
        var accepted = Channel.CreateBounded<IPacketInterface>(4);
        await using var original = new BackbonePacketInterface(new(IPAddress.Loopback, 0), options);
        original.ChildConnected += child => accepted.Writer.TryWrite(child);
        await original.StartAsync(default);
        var address = original.LocalEndpoint!;
        await using var client = new BackboneClientPacketInterface("127.0.0.1", address.Port, options);
        var connections = 0;
        client.Connected += () => Interlocked.Increment(ref connections);
        client.Connected += () => throw new InvalidOperationException("optional observer");
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await client.StartAsync(timeout.Token);
        _ = await accepted.Reader.ReadAsync(timeout.Token);
        await original.DisposeAsync();
        await TestSupport.WaitFor(() => !client.IsConnected);
        await using var replacement = new BackbonePacketInterface(address, options);
        replacement.ChildConnected += child => accepted.Writer.TryWrite(child);
        await replacement.StartAsync(timeout.Token);
        var child = await accepted.Reader.ReadAsync(timeout.Token);
        await TestSupport.WaitFor(() => Volatile.Read(ref connections) >= 2);
        await using var reader = child.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        await client.SendAsync(TestSupport.MakePacket(99), timeout.Token);
        Assert.Equal(99, TestSupport.TagOf(await TestSupport.Next(reader)));
    }

    [LinuxBackboneFact]
    public async Task ListenerBindFailureAndCanceledStartRemainObservable()
    {
        await using var first = new BackbonePacketInterface(new(IPAddress.Loopback, 0));
        await first.StartAsync(default);
        await using var second = new BackbonePacketInterface(first.LocalEndpoint!);
        await Assert.ThrowsAsync<SocketException>(() => second.StartAsync(default).AsTask());
        await using var canceled = new BackbonePacketInterface(new(IPAddress.Loopback, 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.StartAsync(cancellation.Token).AsTask());
    }

    [LinuxBackboneFact]
    public void NativeSocketOptionsMatchReferenceWithoutEnablingPortSharing()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        BackboneSocketOptions.ConfigureUserTimeout(socket);
        Assert.Equal(24000, BackboneSocketOptions.GetNativeInt(socket,
            BackboneSocketOptions.TcpLevel, BackboneSocketOptions.LinuxTcpUserTimeout));
        BackboneSocketOptions.ConfigureListener(socket);
        Assert.Equal(1, BackboneSocketOptions.GetNativeInt(socket,
            BackboneSocketOptions.LinuxSocketLevel, BackboneSocketOptions.LinuxReuseAddress));
        Assert.Equal(0, BackboneSocketOptions.GetNativeInt(socket,
            BackboneSocketOptions.LinuxSocketLevel, BackboneSocketOptions.LinuxReusePort));
    }

    [LinuxBackboneFact]
    public async Task RetryExhaustionFaultsInsteadOfClaimingConnected()
    {
        using var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        await using var client = new BackboneClientPacketInterface("127.0.0.1", port, new()
        {
            MaximumReconnectAttempts = 1, ReconnectDelay = TimeSpan.FromMilliseconds(20), ConnectTimeout = TimeSpan.FromSeconds(1),
        });
        await client.StartAsync(default);
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        await using var reader = client.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        await Assert.ThrowsAsync<IOException>(() => reader.MoveNextAsync().AsTask());
        Assert.False(client.IsConnected);
        Assert.Equal(1, client.GetDiagnostics()["reconnect_attempts"]);
    }

    [Fact]
    public async Task InternalIngressGateAndCancellationAreOwnedAndDiagnosed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var endpoint = new BackbonePeerEndpoint(await listener.AcceptSocketAsync(),
            new() { HardwareMtu = 500 }, (_, _) => { }, () => { });
        endpoint.SetIngressGate(true, BackbonePeerEndpoint.Now + 30);
        endpoint.Start();
        try
        {
            var wire = HdlcPacketFraming.Encode(TestSupport.MakePacket(4));
            await client.SendAsync(wire, SocketFlags.None);
            Assert.True(endpoint.IngressGated);
            Assert.Equal(1L, endpoint.GetDiagnostics()["ingress_throttle_count"]);
            using var timeout = new CancellationTokenSource(TestSupport.Patience);
            await using var reader = endpoint.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
            endpoint.SetIngressGate(false);
            Assert.Equal(4, TestSupport.TagOf(await TestSupport.Next(reader)));
            var sample = endpoint.TakeIngressSample();
            Assert.Equal(wire.Length, sample.Bytes);
            Assert.Equal(1, sample.Frames);
            Assert.Equal((0L, 0L), endpoint.TakeIngressSample());
            endpoint.SetIngressGate(true);
            await endpoint.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
        }
        finally { await endpoint.DisposeAsync(); }
    }

    [Fact]
    public async Task HostAndDeviceConstructorsPerformNoResolutionAndMissingBindIsRejected()
    {
        await using var unresolved = new BackbonePacketInterface("not-resolved-in-a-constructor.invalid", 0);
        Assert.Null(unresolved.LocalEndpoint);
        await using var missingDevice = new BackbonePacketInterface(null, 0,
            new() { Device = "dotreticulum-nonexistent-device-" + Guid.NewGuid().ToString("N") });
        Assert.Null(missingDevice.LocalEndpoint);
        Assert.Throws<ArgumentNullException>(() => new BackbonePacketInterface((string?)null, 0));
        Assert.Throws<ArgumentException>(() => new BackbonePacketInterface("", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackbonePacketInterface("localhost", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackbonePacketInterface("localhost", 65536));
    }

    [Fact]
    public void BindFamilySelectionFollowsSourcePreferenceAndFirstAddressWithinFamily()
    {
        var v4 = IPAddress.Parse("192.0.2.1");
        var secondV4 = IPAddress.Parse("192.0.2.2");
        var v6 = IPAddress.Parse("2001:db8::1");
        Assert.Same(v4, BackboneBindResolver.SelectAddress([v6, v4, secondV4], false));
        Assert.Same(v6, BackboneBindResolver.SelectAddress([v4, v6], true));
        Assert.Same(v4, BackboneBindResolver.SelectAddress([v4], true));
        Assert.Same(v6, BackboneBindResolver.SelectAddress([v6], false));
        Assert.Throws<IOException>(() => BackboneBindResolver.SelectAddress([], false));
    }

    [LinuxBackboneFact]
    public async Task HostBindResolvesAtStartAndInvalidDeviceDoesNotFallBackToListenHost()
    {
        await using var host = new BackbonePacketInterface("localhost", 0);
        await host.StartAsync(default);
        Assert.NotNull(host.LocalEndpoint);
        Assert.True(IPAddress.IsLoopback(host.LocalEndpoint!.Address));
        await using var invalidDevice = new BackbonePacketInterface("127.0.0.1", 0,
            new() { Device = "dotreticulum-nonexistent-device-" + Guid.NewGuid().ToString("N") });
        await Assert.ThrowsAsync<IOException>(() => invalidDevice.StartAsync(default).AsTask());
        Assert.Null(invalidDevice.LocalEndpoint);
    }

    [LinuxBackboneFact]
    public async Task DisposalOwnsConcurrentHostResolutionAndCanceledBindDoesNotOpenListener()
    {
        await using var host = new BackbonePacketInterface("localhost", 0);
        var starting = host.StartAsync(default).AsTask();
        await host.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
        try { await starting; }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.StartAsync(default).AsTask());
        await using var canceled = new BackbonePacketInterface("localhost", 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.StartAsync(cancellation.Token).AsTask());
        Assert.Null(canceled.LocalEndpoint);
    }
}

internal sealed class LinuxBackboneFactAttribute : FactAttribute
{
    public LinuxBackboneFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
            Skip = "Pinned upstream Backbone supports only Linux/Android; no supported Backbone claim on this OS.";
    }
}

internal sealed class UnsupportedBackboneFactAttribute : FactAttribute
{
    public UnsupportedBackboneFactAttribute()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            Skip = "This assertion checks the unsupported-platform rejection; supported startup has separate Linux/Android tests.";
    }
}
