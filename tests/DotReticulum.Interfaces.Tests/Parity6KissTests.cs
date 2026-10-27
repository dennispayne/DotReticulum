using System.Text.Json;

namespace DotReticulum.Interfaces.Tests;

/// <summary>
/// Expected bytes execute RNS a29bfb51c468945c96a1ee22cae0114e993e2391 through GenerateParity6KissVectors.py.
/// Fixture source SHA-256 and generation adapters are recorded in parity6-kiss.json. No physical device evidence.
/// </summary>
[Trait("Category", "Unit")]
public sealed class Parity6KissTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-kiss.json")));
    private static byte[] Bytes(JsonElement item, string property) => Convert.FromHexString(item.GetProperty(property).GetString()!);
    private static KissOptions Fast() => new() { DeviceSettleDelay = TimeSpan.Zero, MaintenanceInterval = TimeSpan.FromMilliseconds(5) };
    private static long Metric(KissPacketInterface bearer, string key) => Convert.ToInt64(bearer.GetDiagnostics()[key]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericConfiguredBitrateOverridesBothBearersWithoutChangingSerialOrWireSettings(bool ax25)
    {
        foreach (var bitrate in new long?[] { null, 5, 9600, long.MaxValue })
        {
            var opened = false;
            Func<CancellationToken, ValueTask<Stream>> open = _ =>
            {
                opened = true;
                throw new InvalidOperationException("Bitrate inspection must not open a device.");
            };
            KissOptions options = ax25
                ? new Ax25KissOptions { Callsign = "N0CALL", Ssid = 0, ConfiguredBitrate = bitrate }
                : new KissOptions { ConfiguredBitrate = bitrate };
            await using KissPacketInterface bearer = ax25
                ? new Ax25KissPacketInterface(open, (Ax25KissOptions)options)
                : new KissPacketInterface(open, options);
            Assert.Equal(bitrate ?? 1200, bearer.Bitrate);
            Assert.Equal(bitrate ?? 1200, Metric(bearer, "bitrate"));
            Assert.Equal(bitrate is null ? "upstream_estimate" : "configured_estimate",
                bearer.GetDiagnostics()["dotreticulum_bitrate_kind"]);
            Assert.Equal(9600, options.BaudRate);
            Assert.Equal(564, bearer.HardwareMtu);
            Assert.Equal(KissPacketFraming.Configuration(new KissOptions()), KissPacketFraming.Configuration(options));
            Assert.False(opened);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredBitrateBelowUpstreamMinimumIsRejectedBeforeOpening(bool ax25)
    {
        foreach (var bitrate in new[] { long.MinValue, -1L, 0L, 4L })
        {
            var opened = false;
            Func<CancellationToken, ValueTask<Stream>> open = _ =>
            {
                opened = true;
                throw new InvalidOperationException("Invalid options must not open a device.");
            };
            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                if (ax25) _ = new Ax25KissPacketInterface(open, new Ax25KissOptions { Callsign = "N0CALL", Ssid = 0, ConfiguredBitrate = bitrate });
                else _ = new KissPacketInterface(open, new KissOptions { ConfiguredBitrate = bitrate });
            });
            Assert.Equal(nameof(KissOptions.ConfiguredBitrate), error.ParamName);
            Assert.False(opened);
        }
    }

    [Fact]
    public void IndependentTransmitAndConfigurationVectorsMatch()
    {
        using var vectors = Vectors();
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", vectors.RootElement.GetProperty("revision").GetString());
        foreach (var item in vectors.RootElement.GetProperty("outgoing").EnumerateArray())
        {
            var payload = Bytes(item, "payload");
            if (item.GetProperty("kind").GetString() != "kiss")
                payload = Ax25KissFraming.AddHeader(payload, item.GetProperty("callsign").GetString()!, item.GetProperty("ssid").GetInt32());
            Assert.Equal(Bytes(item, "wire"), KissPacketFraming.Encode(payload));
        }
        foreach (var item in vectors.RootElement.GetProperty("configuration").EnumerateArray())
        {
            var values = item.GetProperty("values").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var options = new KissOptions
            {
                PreambleMilliseconds = values[0], TxTailMilliseconds = values[1],
                Persistence = values[2], SlotTimeMilliseconds = values[3],
            };
            Assert.Equal(Bytes(item, "wire"), KissPacketFraming.Configuration(options));
            Assert.Equal(KissPacketFraming.Configuration(options), KissPacketFraming.Configuration(options with { FlowControl = true }));
        }
    }

    [Fact]
    public void IndependentReceiveVectorsMatchAtEveryChunkSize()
    {
        using var vectors = Vectors();
        foreach (var item in vectors.RootElement.GetProperty("incoming").EnumerateArray())
        {
            var wire = Bytes(item, "wire");
            var ax25 = item.GetProperty("kind").GetString() == "ax25";
            var expected = item.GetProperty("packets").EnumerateArray().Select(p => p.GetString()).ToArray();
            foreach (var chunk in new[] { 1, 2, 7, 4096 })
            {
                var decoder = new KissFrameDecoder(ax25 ? 580 : 564);
                var frames = new List<byte[]>();
                var ready = 0;
                for (var offset = 0; offset < wire.Length; offset += chunk)
                    ready += decoder.Feed(wire.AsSpan(offset, Math.Min(chunk, wire.Length - offset)), frames);
                var actual = frames.Where(f => !ax25 || f.Length > 16)
                    .Select(f => Convert.ToHexString(ax25 ? f.AsSpan(16) : f.AsSpan()).ToLowerInvariant()).ToArray();
                Assert.Equal(expected, actual);
                Assert.Equal(item.GetProperty("ready").GetInt32(), ready);
            }
        }
    }

    [Theory]
    [InlineData("AB", 0)]
    [InlineData("ABCDEFG", 0)]
    [InlineData("NØCALL", 0)]
    [InlineData("ABC", -1)]
    [InlineData("ABC", 16)]
    public void InvalidAx25AddressIsRejectedWithoutOpeningAnything(string call, int ssid)
    {
        var opened = false;
        Assert.ThrowsAny<ArgumentException>(() => new Ax25KissPacketInterface(_ =>
        {
            opened = true;
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }, new Ax25KissOptions { Callsign = call, Ssid = ssid }));
        Assert.False(opened);
    }

    [Fact]
    public void DecoderBoundsAndTimeoutRecoveryAreExplicit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KissFrameDecoder(581));
        Assert.Throws<ArgumentOutOfRangeException>(() => KissPacketFraming.Encode(new byte[581]));
        var decoder = new KissFrameDecoder();
        var frames = new List<byte[]>();
        decoder.Feed([0xc0, 0, 1, 0xdb], frames);
        Assert.True(decoder.HasPartialData);
        decoder.ExpirePartial();
        decoder.Feed([0xdc, 0xc0, 0xc0, 0, 2, 0xc0], frames);
        Assert.Equal(new byte[] { 2 }, Assert.Single(frames));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedStreamSendsAndReceivesReferenceFrames(bool ax25)
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var options = Fast();
        await using KissPacketInterface bearer = ax25
            ? new Ax25KissPacketInterface(local, new Ax25KissOptions { Callsign = "N0CALL", Ssid = 15, DeviceSettleDelay = TimeSpan.Zero })
            : new KissPacketInterface(local, options);
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(options), timeout.Token);
        Assert.Equal(1200, bearer.Bitrate);
        Assert.Equal(564, bearer.HardwareMtu);
        using var vectors = Vectors();
        var item = vectors.RootElement.GetProperty("outgoing").EnumerateArray()
            .First(v => v.GetProperty("kind").GetString() == (ax25 ? "ax25" : "kiss"));
        var send = bearer.SendAsync(Bytes(item, "payload"), timeout.Token).AsTask();
        await ReadExactAsync(peer, Bytes(item, "wire"), timeout.Token);
        await send;
        await peer.WriteAsync(Bytes(item, "wire"), timeout.Token);
        await using var receive = bearer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.Equal(Bytes(item, "payload"), (await TestSupport.Next(receive)).ToArray());
        Assert.Equal(Bytes(item, "payload").Length, Metric(bearer, "txb"));
        Assert.Equal(Bytes(item, "payload").Length + (ax25 ? 16 : 0), Metric(bearer, "rxb"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bearer.SendAsync(new byte[565], timeout.Token).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bearer.SendAsync(ReadOnlyMemory<byte>.Empty, timeout.Token).AsTask());
        Assert.Throws<InvalidOperationException>(() => bearer.ReceiveAsync(timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bearer.StartAsync(timeout.Token).AsTask());
    }

    [Fact]
    public async Task ReadyRunsWithoutSubscriberAndFullReceiveQueueCannotDisableFlowControl()
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var options = Fast() with { FlowControl = true, ReceiveQueueCapacity = 1 };
        await using var bearer = new KissPacketInterface(local, options);
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(options), timeout.Token);
        var first = bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask();
        await ReadExactAsync(peer, KissPacketFraming.Encode([1]), timeout.Token);
        await first;
        var second = bearer.SendAsync(new byte[] { 2 }, timeout.Token).AsTask();
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_send_pending") == 1);
        await peer.WriteAsync(new byte[] { 0xc0, 0, 8, 0xc0, 0xc0, 0, 9, 0xc0, 0xc0, 0x7f }, timeout.Token);
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_receive_drops") == 1);
        Assert.False(second.IsCompleted); // A READY command without its payload must not release the queue.
        await peer.WriteAsync(new byte[] { 1, 0xc0 }, timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Encode([2]), timeout.Token);
        await second;
        await using var receive = bearer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.Equal(new byte[] { 8 }, (await TestSupport.Next(receive)).ToArray());
    }

    [Fact]
    public async Task TransmitBackpressureIsBoundedAndAdmissionCancellationDoesNotTransmit()
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var options = Fast() with { FlowControl = true, SendQueueCapacity = 1 };
        await using var bearer = new KissPacketInterface(local, options);
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(options), timeout.Token);
        var first = bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask();
        await ReadExactAsync(peer, KissPacketFraming.Encode([1]), timeout.Token);
        await first;
        var blocked = bearer.SendAsync(new byte[] { 2 }, timeout.Token).AsTask();
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_send_pending") == 1);
        using var cancel = new CancellationTokenSource();
        var unadmitted = bearer.SendAsync(new byte[] { 3 }, cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unadmitted);
        Assert.False(blocked.IsCompleted);
        Assert.Equal(1, Metric(bearer, "kiss_send_pending"));
        await peer.WriteAsync(new byte[] { 0xc0, 0x0f, 1, 0xc0 }, timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Encode([2]), timeout.Token);
        await blocked;
        Assert.Equal(2, Metric(bearer, "txb"));
    }

    [Fact]
    public async Task EachReadyPayloadByteReleasesExactlyOneQueuedSendAcrossPortNibbles()
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var options = Fast() with { FlowControl = true };
        await using var bearer = new KissPacketInterface(local, options);
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(options), timeout.Token);
        var first = bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask();
        await ReadExactAsync(peer, KissPacketFraming.Encode([1]), timeout.Token);
        await first;
        var second = bearer.SendAsync(new byte[] { 2 }, timeout.Token).AsTask();
        var third = bearer.SendAsync(new byte[] { 3 }, timeout.Token).AsTask();
        var fourth = bearer.SendAsync(new byte[] { 4 }, timeout.Token).AsTask();
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_send_pending") == 3);
        await peer.WriteAsync(new byte[] { 0xc0, 0xff, 1, 2, 0xc0 }, timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Encode([2]), timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Encode([3]), timeout.Token);
        await Task.WhenAll(second, third);
        Assert.False(fourth.IsCompleted);
        await peer.WriteAsync(new byte[] { 0xc0, 0x1f, 1, 0xc0 }, timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Encode([4]), timeout.Token);
        await fourth;
    }

    [Fact]
    public async Task QueuedSendCancellationReleasesCapacityWithoutRequiringReady()
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var options = Fast() with { FlowControl = true, SendQueueCapacity = 1 };
        await using var bearer = new KissPacketInterface(local, options);
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(options), timeout.Token);
        var first = bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask();
        await ReadExactAsync(peer, KissPacketFraming.Encode([1]), timeout.Token);
        await first;
        using var cancel = new CancellationTokenSource();
        var cancelled = bearer.SendAsync(new byte[] { 2 }, cancel.Token).AsTask();
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_send_pending") == 1);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_send_pending") == 0);
        Assert.Equal(1, Metric(bearer, "txb"));
    }

    [Fact]
    public async Task SurplusReadyBytesLeaveOneIdlePermissionButCannotBankUnlimitedFutureSends()
    {
        using var vectors = Vectors();
        var reference = vectors.RootElement.GetProperty("flow").EnumerateArray()
            .Single(v => v.GetProperty("kind").GetString() == "kiss" && v.GetProperty("ready_count").GetInt32() == 3);
        Assert.True(reference.GetProperty("ready_after").GetBoolean());
        Assert.Equal(0, reference.GetProperty("queued_after").GetInt32());
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var options = Fast() with { FlowControl = true, TimeProvider = new ManualClock() };
        await using var bearer = new KissPacketInterface(local, options);
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(options), timeout.Token);
        var first = bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask();
        await ReadExactAsync(peer, KissPacketFraming.Encode([1]), timeout.Token);
        await first;
        var second = bearer.SendAsync(new byte[] { 2 }, timeout.Token).AsTask();
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_send_pending") == 1);
        await peer.WriteAsync(Bytes(reference, "incoming"), timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Encode([2]), timeout.Token);
        await second;
        var third = bearer.SendAsync(new byte[] { 3 }, timeout.Token).AsTask();
        await ReadExactAsync(peer, KissPacketFraming.Encode([3]), timeout.Token);
        await third;
        var fourth = bearer.SendAsync(new byte[] { 4 }, timeout.Token).AsTask();
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_send_pending") == 1);
        Assert.False(fourth.IsCompleted);
        await peer.WriteAsync(new byte[] { 0xc0, 0x0f, 1, 0xc0 }, timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Encode([4]), timeout.Token);
        await fourth;
    }

    [Fact]
    public async Task ManualClockExercisesFiveSecondFlowAndHundredMillisecondPartialTimeouts()
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var clock = new ManualClock();
        var options = Fast() with { TimeProvider = clock, FlowControl = true };
        await using var bearer = new KissPacketInterface(local, options);
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(options), timeout.Token);
        var first = bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask();
        await ReadExactAsync(peer, KissPacketFraming.Encode([1]), timeout.Token);
        await first;
        var second = bearer.SendAsync(new byte[] { 2 }, timeout.Token).AsTask();
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_send_pending") == 1);
        clock.Advance(TimeSpan.FromSeconds(6));
        await ReadExactAsync(peer, KissPacketFraming.Encode([2]), timeout.Token);
        await second;
        Assert.Equal(1, Metric(bearer, "kiss_flow_timeouts"));
        await peer.WriteAsync(new byte[] { 0xc0, 0, 1, 0xdb }, timeout.Token);
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_wire_read_bytes") >= 4);
        clock.Advance(TimeSpan.FromMilliseconds(101));
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_partial_expirations") == 1);
        await peer.WriteAsync(new byte[] { 0xdc, 0xc0, 0xc0, 0, 7, 0xc0 }, timeout.Token);
        await using var receive = bearer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.Equal(new byte[] { 7 }, (await TestSupport.Next(receive)).ToArray());
    }

    [Fact]
    public async Task ManualClockBeaconMatchesIndependentReadLoopVectorAndRearmsShortBeacon()
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var clock = new ManualClock();
        var options = Fast() with { TimeProvider = clock, BeaconInterval = TimeSpan.FromSeconds(1), BeaconCallsign = "ID" };
        await using var bearer = new KissPacketInterface(local, options);
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(options), timeout.Token);
        var send = bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask();
        await ReadExactAsync(peer, KissPacketFraming.Encode([1]), timeout.Token);
        await send;
        using var vectors = Vectors();
        var beacon = vectors.RootElement.GetProperty("beacon");
        clock.Advance(TimeSpan.FromSeconds(2));
        await ReadExactAsync(peer, Bytes(beacon, "wire"), timeout.Token);
        await TestSupport.WaitFor(() => Metric(bearer, "txb") == 16 && Metric(bearer, "kiss_send_pending") == 0);
        clock.Advance(TimeSpan.FromSeconds(2));
        await ReadExactAsync(peer, Bytes(beacon, "wire"), timeout.Token);
        await TestSupport.WaitFor(() => Metric(bearer, "txb") == 31);
    }

    [Fact]
    public async Task InitialFailureIsObservableAndDoesNotPretendToStartOffline()
    {
        var attempts = 0;
        await using var bearer = new KissPacketInterface(_ =>
        {
            attempts++;
            return ValueTask.FromException<Stream>(new IOException("simulated open failure"));
        }, Fast());
        var observerCalls = 0;
        bearer.Faulted += _ => throw new InvalidOperationException("observer");
        bearer.Faulted += _ => observerCalls++;
        await Assert.ThrowsAsync<IOException>(() => bearer.StartAsync(CancellationToken.None).AsTask());
        Assert.Equal(1, attempts);
        Assert.Equal(1, observerCalls);
        Assert.Equal(1, Metric(bearer, "kiss_observer_errors"));
        Assert.False(bearer.IsConnected);
        Assert.NotNull(bearer.GetDiagnostics()["kiss_last_error"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bearer.StartAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CancelledStartAndDisposeRaceCloseTheOwnedStreamExactlyOnce()
    {
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var owned = new TrackedStream(local);
        await using var bearer = new KissPacketInterface(owned, Fast() with { DeviceSettleDelay = TimeSpan.FromHours(1) });
        using var cancel = new CancellationTokenSource();
        var start = bearer.StartAsync(cancel.Token).AsTask();
        await TestSupport.WaitFor(() => owned.ReadabilityChecks > 1);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        await Task.WhenAll(bearer.DisposeAsync().AsTask(), bearer.DisposeAsync().AsTask());
        Assert.Equal(1, owned.DisposeCount);
        Assert.False(bearer.IsConnected);
    }

    [Fact]
    public async Task DisposeDuringAsynchronousOpenAwaitsAndClosesTheLateTransferredStream()
    {
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var owned = new TrackedStream(local);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bearer = new KissPacketInterface(_ =>
        {
            entered.TrySetResult();
            return new ValueTask<Stream>(release.Task);
        }, Fast());
        var start = bearer.StartAsync(CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TestSupport.Patience);
        var dispose = bearer.DisposeAsync().AsTask();
        release.SetResult(owned);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        await dispose.WaitAsync(TestSupport.Patience);
        Assert.Equal(1, owned.DisposeCount);
        await bearer.DisposeAsync();
    }

    [Fact]
    public async Task DisposingBeforeStartOwnsStreamAndDisposingActiveInterfaceEndsReceiveAndPendingSend()
    {
        var (unstarted, unusedPeer) = TestSupport.CreateDuplexPair();
        await using var unused = unusedPeer;
        var owned = new TrackedStream(unstarted);
        var neverStarted = new KissPacketInterface(owned, Fast());
        await neverStarted.DisposeAsync();
        await neverStarted.DisposeAsync();
        Assert.Equal(1, owned.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => neverStarted.StartAsync(CancellationToken.None).AsTask());

        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var options = Fast() with { FlowControl = true };
        var bearer = new KissPacketInterface(local, options);
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(options), timeout.Token);
        await using var receive = bearer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        var waitingReceive = receive.MoveNextAsync().AsTask();
        var first = bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask();
        await ReadExactAsync(peer, KissPacketFraming.Encode([1]), timeout.Token);
        await first;
        var blocked = bearer.SendAsync(new byte[] { 2 }, timeout.Token).AsTask();
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_send_pending") == 1);
        await bearer.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.False(await waitingReceive.WaitAsync(TestSupport.Patience));
        await bearer.DisposeAsync();
    }

    [Fact]
    public async Task FactoryReconnectsReconfiguresAndIsolatesObserversWithoutReplacingReceiveStream()
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (firstLocal, firstPeer) = TestSupport.CreateDuplexPair();
        var (secondLocal, secondPeer) = TestSupport.CreateDuplexPair();
        await using var remote1 = firstPeer;
        await using var remote2 = secondPeer;
        var streams = new Queue<Stream>([firstLocal, secondLocal]);
        var attempts = 0;
        var connected = 0;
        var disconnected = 0;
        var options = Fast() with { ReconnectDelay = TimeSpan.FromMilliseconds(5) };
        await using var bearer = new KissPacketInterface(_ =>
        {
            Interlocked.Increment(ref attempts);
            return ValueTask.FromResult(streams.Dequeue());
        }, options);
        bearer.Connected += () => throw new InvalidOperationException("optional connection observer");
        bearer.Connected += () => Interlocked.Increment(ref connected);
        bearer.Disconnected += () => Interlocked.Increment(ref disconnected);
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(firstPeer, KissPacketFraming.Configuration(options), timeout.Token);
        await using var receive = bearer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        await firstPeer.DisposeAsync();
        await ReadExactAsync(secondPeer, KissPacketFraming.Configuration(options), timeout.Token);
        await TestSupport.WaitFor(() => connected == 2);
        Assert.True(bearer.IsConnected);
        Assert.Equal(2, attempts);
        Assert.Equal(1, disconnected);
        Assert.Equal(1, Metric(bearer, "kiss_reconnections"));
        Assert.Equal(2, Metric(bearer, "kiss_observer_errors"));
        await secondPeer.WriteAsync(KissPacketFraming.Encode([7]), timeout.Token);
        Assert.Equal(new byte[] { 7 }, (await TestSupport.Next(receive)).ToArray());
        var send = bearer.SendAsync(new byte[] { 8 }, timeout.Token).AsTask();
        await ReadExactAsync(secondPeer, KissPacketFraming.Encode([8]), timeout.Token);
        await send;
    }

    [Fact]
    public async Task ReadyBlockedQueuedSendSurvivesReconnectInOrder()
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (firstLocal, firstPeer) = TestSupport.CreateDuplexPair();
        var (secondLocal, secondPeer) = TestSupport.CreateDuplexPair();
        await using var remote1 = firstPeer;
        await using var remote2 = secondPeer;
        var streams = new Queue<Stream>([firstLocal, secondLocal]);
        var options = Fast() with { FlowControl = true, ReconnectDelay = TimeSpan.FromMilliseconds(5) };
        await using var bearer = new KissPacketInterface(_ => ValueTask.FromResult(streams.Dequeue()), options);
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(firstPeer, KissPacketFraming.Configuration(options), timeout.Token);
        var first = bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask();
        await ReadExactAsync(firstPeer, KissPacketFraming.Encode([1]), timeout.Token);
        await first;
        var blocked = bearer.SendAsync(new byte[] { 2 }, timeout.Token).AsTask();
        var queued = bearer.SendAsync(new byte[] { 3 }, timeout.Token).AsTask();
        await TestSupport.WaitFor(() => Metric(bearer, "kiss_send_pending") == 2);
        await firstPeer.DisposeAsync();
        await ReadExactAsync(secondPeer, KissPacketFraming.Configuration(options), timeout.Token);
        await ReadExactAsync(secondPeer, KissPacketFraming.Encode([2]), timeout.Token);
        await blocked;
        Assert.False(queued.IsCompleted);
        await secondPeer.WriteAsync(new byte[] { 0xc0, 0x0f, 1, 0xc0 }, timeout.Token);
        await ReadExactAsync(secondPeer, KissPacketFraming.Encode([3]), timeout.Token);
        await queued;
    }

    [Fact]
    public async Task DataWriteFailureDoesNotCountOrReplayAnAmbiguousPacketAndClosesOwnedStream()
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        var owned = new FailingWriteStream(local);
        await using var bearer = new KissPacketInterface(owned, Fast());
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(Fast()), timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask());
        await TestSupport.WaitFor(() => !bearer.IsConnected && owned.DisposeCount == 1 && bearer.GetDiagnostics()["kiss_last_error"] is not null);
        Assert.Equal(0, Metric(bearer, "txb"));
        Assert.Equal(0, Metric(bearer, "kiss_send_pending"));
        Assert.NotNull(bearer.GetDiagnostics()["kiss_last_error"]);
    }

    [Fact]
    public async Task NonReopenableStreamFailureFaultsReceiveAndCompletesPendingSends()
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        await using var bearer = new KissPacketInterface(local, Fast());
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(Fast()), timeout.Token);
        await using var receive = bearer.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        var next = receive.MoveNextAsync().AsTask();
        await peer.DisposeAsync();
        await Assert.ThrowsAsync<EndOfStreamException>(() => next);
        await TestSupport.WaitFor(() => !bearer.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask());
    }

    [Fact]
    public async Task ReceiveCancellationDoesNotOwnInterfaceLifetime()
    {
        using var timeout = new CancellationTokenSource(TestSupport.Patience);
        var (local, peer) = TestSupport.CreateDuplexPair();
        await using var remote = peer;
        await using var bearer = new KissPacketInterface(local, Fast());
        await bearer.StartAsync(timeout.Token);
        await ReadExactAsync(peer, KissPacketFraming.Configuration(Fast()), timeout.Token);
        using var cancel = new CancellationTokenSource();
        await using var receive = bearer.ReceiveAsync(cancel.Token).GetAsyncEnumerator();
        var next = receive.MoveNextAsync().AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.True(bearer.IsConnected);
        var send = bearer.SendAsync(new byte[] { 1 }, timeout.Token).AsTask();
        await ReadExactAsync(peer, KissPacketFraming.Encode([1]), timeout.Token);
        await send;
    }

    [Fact]
    public void OptionsRejectUnboundedAndUnsupportedConfigurationsWithoutDeviceIo()
    {
        var invalid = new KissOptions[]
        {
            Fast() with { SendQueueCapacity = 0 }, Fast() with { ReceiveQueueCapacity = 65537 },
            Fast() with { FlowControlTimeout = TimeSpan.Zero }, Fast() with { MaintenanceInterval = TimeSpan.Zero },
            Fast() with { PartialFrameTimeout = TimeSpan.Zero }, Fast() with { ReconnectDelay = TimeSpan.Zero },
            Fast() with { BeaconCallsign = new string('x', 565) },
            Fast() with { BeaconCallsign = "\ud800" },
            Fast() with { Parity = System.IO.Ports.Parity.Mark },
            new Ax25KissOptions { Callsign = "ABC", Ssid = 0, BeaconInterval = TimeSpan.FromSeconds(1) },
        };
        foreach (var options in invalid)
            Assert.ThrowsAny<ArgumentException>(() => new KissPacketInterface(_ => throw new InvalidOperationException("Must not open"), options));
    }

    private static async Task ReadExactAsync(Stream stream, byte[] expected, CancellationToken cancellationToken)
    {
        var actual = new byte[expected.Length];
        await stream.ReadExactlyAsync(actual, cancellationToken);
        Assert.Equal(expected, actual);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Read(ref _ticks));
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private class TrackedStream(Stream inner) : Stream
    {
        private int _disposed;
        public int DisposeCount => Volatile.Read(ref _disposed);
        public int ReadabilityChecks;
        public override bool CanRead { get { Interlocked.Increment(ref ReadabilityChecks); return inner.CanRead; } }
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class FailingWriteStream(Stream inner) : TrackedStream(inner)
    {
        private int _writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _writes) > 5
                ? ValueTask.FromException(new IOException("simulated interrupted data write"))
                : base.WriteAsync(buffer, cancellationToken);
    }
}
