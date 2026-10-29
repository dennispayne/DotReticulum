using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;

namespace DotReticulum.Interfaces.Tests;

/// <summary>Vectors execute RNS a29bfb51 (1.5.7); simulator tests are not physical-radio interoperability.</summary>
[Trait("Category", "Integration")]
public sealed class Parity6RNodeTests
{
    private static RNodeRadioOptions Radio(bool flow = false) => new()
    {
        Frequency = 868000000, Bandwidth = 125000, TxPower = 7, SpreadingFactor = 7, CodingRate = 5,
        AirtimeLimitShort = 1.25, AirtimeLimitLong = 50, FlowControl = flow,
    };

    private static RNodeOptions Options(bool flow = false) => new()
    {
        Radio = Radio(flow), DeviceSettleDelay = TimeSpan.Zero, ReconnectDelay = TimeSpan.FromMilliseconds(10),
        ResponseTimeout = TimeSpan.FromSeconds(1),
    };

    private static RNodeMultiOptions MultiOptions(bool flow = false) => new()
    {
        DeviceSettleDelay = TimeSpan.Zero, ReconnectDelay = TimeSpan.FromMilliseconds(10),
        ResponseTimeout = TimeSpan.FromSeconds(1),
        Subinterfaces =
        [
            new() { Name = "first", VirtualPort = 0, Enabled = true, Frequency = 868000000, Bandwidth = 125000,
                TxPower = -9, SpreadingFactor = 7, CodingRate = 5, FlowControl = flow },
            new() { Name = "second", VirtualPort = 2, Enabled = true, Frequency = 868000000, Bandwidth = 125000,
                TxPower = -9, SpreadingFactor = 7, CodingRate = 5, FlowControl = flow },
        ],
    };

    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-rnode.json")));
    private static byte[] Hex(JsonElement value) => Convert.FromHexString(value.GetString()!);

    [Fact]
    public void SerialControlLinesMatchReferenceDriverDefaults()
    {
        using var vectors = Vectors();
        var defaults = vectors.RootElement.GetProperty("serial_defaults");
        using var port = NativeSerialPort.Create("unopened", 115200, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
        Assert.Equal(defaults.GetProperty("dtr").GetBoolean(), port.DtrEnable);
        Assert.Equal(defaults.GetProperty("rts").GetBoolean(), port.RtsEnable);
        Assert.False(port.IsOpen);
    }

    [Fact]
    public void ControlAndDataMatchActualPinnedPython()
    {
        using var vectors = Vectors();
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", vectors.RootElement.GetProperty("revision").GetString());
        var single = vectors.RootElement.GetProperty("single");
        Assert.Equal(Hex(single.GetProperty("detect")), RNodeProtocol.Detect());
        var expected = single.GetProperty("configuration").EnumerateArray().Select(Hex).ToArray();
        var actual = RNodeProtocol.Configuration(Radio()).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++) Assert.Equal(expected[i], actual[i]);
        foreach (var value in single.GetProperty("data").EnumerateArray())
        {
            var payload = Hex(value.GetProperty("payload"));
            var wire = Hex(value.GetProperty("wire"));
            Assert.Equal(wire, RNodeProtocol.Frame(0, payload));
            var decoder = new RNodeFrameDecoder();
            var decoded = new List<RNodeFrame>();
            foreach (var b in wire) decoded.AddRange(decoder.Feed([b]));
            Assert.Equal(payload, Assert.Single(decoded).Payload);
        }
        Assert.Equal(Hex(single.GetProperty("leave")), RNodeProtocol.Frame(0x0a, [0xff]));
        var multi = vectors.RootElement.GetProperty("multi");
        Assert.Equal(Hex(multi.GetProperty("detect")), RNodeProtocol.Detect(multi: true));
        foreach (var child in multi.GetProperty("children").EnumerateArray())
        {
            var port = child.GetProperty("index").GetInt32();
            var config = RNodeProtocol.Configuration(Radio() with { TxPower = -9 }, port);
            expected = child.GetProperty("configuration").EnumerateArray().Select(Hex).ToArray();
            for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], config[i]);
            Assert.Equal(Hex(child.GetProperty("data")), RNodeProtocol.SelectedFrame(port, 0, [1, 0xc0, 0xdb, 2]));
        }
    }

    [Fact]
    public void DecoderRejectsInvalidPartialAndOversizeFramesThenRecovers()
    {
        var decoder = new RNodeFrameDecoder();
        Assert.Empty(decoder.Feed([0xc0, 0, 1, 0xdb, 1, 0xc0]));
        Assert.Empty(decoder.Feed([0xc0, 0, 1, 0xdb, 0xc0]));
        Assert.Empty(decoder.Feed(RNodeProtocol.Frame(0, new byte[509])));
        Assert.Empty(decoder.Feed([0xc0, 0, 1, 0xdb]));
        decoder.Expire();
        Assert.Empty(decoder.Feed([0xdc, 3, 0xc0]));
        var recovered = Assert.Single(decoder.Feed(RNodeProtocol.Frame(0, [4, 5])));
        Assert.Equal(new byte[] { 4, 5 }, recovered.Payload);
        Assert.Equal(4, decoder.DroppedFrames);
        var large = new byte[2048];
        Assert.Empty(decoder.Feed(RNodeProtocol.Frame(0x26, large)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(136999999)]
    [InlineData(3000000001)]
    public void InvalidConfigurationDoesNotOpenFactory(long frequency)
    {
        var opened = false;
        Assert.Throws<ArgumentOutOfRangeException>(() => new RNodePacketInterface(_ =>
        {
            opened = true;
            return ValueTask.FromResult<Stream>(Stream.Null);
        }, Options() with { Radio = Radio() with { Frequency = frequency } }));
        Assert.False(opened);
    }

    [Fact]
    public void AdditionalConfigurationBoundsAreEnforced()
    {
        foreach (var radio in new[]
        {
            Radio() with { Bandwidth = 7799 }, Radio() with { TxPower = -1 }, Radio() with { SpreadingFactor = 13 },
            Radio() with { CodingRate = 4 }, Radio() with { AirtimeLimitShort = double.NaN },
            Radio() with { AirtimeLimitLong = 100.01 },
        })
            Assert.Throws<ArgumentOutOfRangeException>(() => new RNodePacketInterface(Stream.Null, Options() with { Radio = radio }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RNodePacketInterface(Stream.Null,
            Options() with { IdCallsign = new string('é', 17) }));
        foreach (var ble in new[]
        {
            new RNodeOptions.RNodeBleOptions { ConnectTimeout = TimeSpan.Zero },
            new RNodeOptions.RNodeBleOptions { PartialFrameTimeout = TimeSpan.Zero },
            new RNodeOptions.RNodeBleOptions { ReceiveBufferBytes = 511 },
            new RNodeOptions.RNodeBleOptions { ReceiveBufferBytes = 1048577 },
        })
            Assert.Throws<ArgumentOutOfRangeException>(() => new RNodePacketInterface(Stream.Null, Options() with { Ble = ble }));
        Assert.Throws<ArgumentException>(() => new RNodeMultiPacketInterface(Stream.Null, MultiOptions() with { Subinterfaces = [] }));
    }

    [Fact]
    public async Task StartupUsesOracleConfigurationAndWorksWithoutObservers()
    {
        await using var simulator = new Parity6RNodeSimulator();
        await using var node = new RNodePacketInterface(simulator.Host, Options());
        Assert.Empty(simulator.HostWrites);
        await node.StartAsync(CancellationToken.None);
        Assert.True(node.IsConnected);
        using var vectors = Vectors();
        var expected = new[] { Hex(vectors.RootElement.GetProperty("single").GetProperty("detect")) }
            .Concat(vectors.RootElement.GetProperty("single").GetProperty("configuration").EnumerateArray().Select(Hex)).ToArray();
        Assert.Equal(expected.Length, simulator.HostWrites.Count);
        var actual = simulator.HostWrites.ToArray();
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
        Assert.Equal(5468, node.Bitrate);
        var payload = TestSupport.MakePacket(6);
        await node.SendAsync(payload, CancellationToken.None);
        Assert.Equal(payload, (await simulator.NextTransmissionAsync()).Payload);
        await using var receive = node.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        await simulator.IncomingAsync(0, payload);
        Assert.Equal(payload, (await TestSupport.Next(receive)).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.StartAsync(CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(1, 51, false)]
    [InlineData(0, 255, false)]
    [InlineData(1, 52, true)]
    [InlineData(2, 0, true)]
    public async Task MinimumSingleFirmwareIsEnforced(int major, int minor, bool success)
    {
        await using var simulator = new Parity6RNodeSimulator { FirmwareMajor = (byte)major, FirmwareMinor = (byte)minor };
        await using var node = new RNodePacketInterface(simulator.Host, Options());
        if (success) await node.StartAsync(CancellationToken.None);
        else
        {
            await Assert.ThrowsAsync<IOException>(() => node.StartAsync(CancellationToken.None).AsTask());
            Assert.True(simulator.HostDisposed);
            Assert.False(node.IsConnected);
        }
    }

    [Fact]
    public async Task MismatchedEchoAndMissingDetectionFailAndDisposeOwnedStream()
    {
        await using (var simulator = new Parity6RNodeSimulator { WrongBandwidth = true })
        {
            await using var node = new RNodePacketInterface(simulator.Host, Options());
            await Assert.ThrowsAsync<IOException>(() => node.StartAsync(CancellationToken.None).AsTask());
            Assert.True(simulator.HostDisposed);
        }
        await using (var simulator = new Parity6RNodeSimulator { DetectResponse = false })
        {
            await using var node = new RNodePacketInterface(simulator.Host, Options() with { ResponseTimeout = TimeSpan.FromMilliseconds(200) });
            await Assert.ThrowsAsync<TimeoutException>(() => node.StartAsync(CancellationToken.None).AsTask());
            Assert.True(simulator.HostDisposed);
        }
    }

    [Fact]
    public async Task FlowCreditsBoundedQueueAndCancellationDoNotInventReadiness()
    {
        await using var simulator = new Parity6RNodeSimulator();
        await using var node = new RNodePacketInterface(simulator.Host, Options(flow: true) with { SendQueueCapacity = 1 });
        await node.StartAsync(CancellationToken.None);
        await node.SendAsync(new byte[] { 1 }, CancellationToken.None);
        Assert.Equal(new byte[] { 1 }, (await simulator.NextTransmissionAsync()).Payload);
        using var canceled = new CancellationTokenSource();
        var waiting = node.SendAsync(new byte[] { 2 }, canceled.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        using var queueCanceled = new CancellationTokenSource();
        var blocked = node.SendAsync(new byte[] { 3 }, queueCanceled.Token).AsTask();
        Assert.False(blocked.IsCompleted);
        queueCanceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var next = node.SendAsync(new byte[] { 4 }, CancellationToken.None).AsTask();
        Assert.False(next.IsCompleted);
        await simulator.ReadyAsync();
        await next.WaitAsync(TestSupport.Patience);
        Assert.Equal(new byte[] { 4 }, (await simulator.NextTransmissionAsync()).Payload);
    }

    [Fact]
    public async Task MultiChildrenAreDistinctStableAndShareAtomicWireWithoutFlowHeadOfLineBlocking()
    {
        await using var simulator = new Parity6RNodeSimulator(multi: true);
        await using var node = new RNodeMultiPacketInterface(simulator.Host, MultiOptions(flow: true));
        var children = new List<IPacketInterface>();
        node.ChildConnected += _ => throw new InvalidOperationException("observer fixture");
        node.ChildConnected += children.Add;
        await node.StartAsync(CancellationToken.None);
        Assert.Equal(2, children.Count);
        Assert.NotSame(children[0], children[1]);
        var policy = Assert.IsAssignableFrom<IPacketInterfaceChildPolicy>(children[0]);
        Assert.False(policy.InheritParentOutgoing);
        Assert.True(policy.CountParentTraffic);
        Assert.Equal(2L, node.GetDiagnostics()["observer_errors"]);
        await children[0].SendAsync(new byte[] { 1 }, CancellationToken.None);
        var blocked = children[0].SendAsync(new byte[] { 2 }, CancellationToken.None).AsTask();
        Assert.False(blocked.IsCompleted);
        await children[1].SendAsync(new byte[] { 3 }, CancellationToken.None);
        var first = await simulator.NextTransmissionAsync();
        var second = await simulator.NextTransmissionAsync();
        Assert.Equal(0, first.Port);
        Assert.Equal(2, second.Port);
        await simulator.ReadyAsync();
        await blocked.WaitAsync(TestSupport.Patience);
        Assert.Equal(new byte[] { 2 }, (await simulator.NextTransmissionAsync()).Payload);
        await using var readerA = children[0].ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var readerB = children[1].ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        await simulator.IncomingAsync(0, [10]);
        await simulator.IncomingAsync(2, [20]);
        Assert.Equal(new byte[] { 10 }, (await TestSupport.Next(readerA)).ToArray());
        Assert.Equal(new byte[] { 20 }, (await TestSupport.Next(readerB)).ToArray());
        await children[0].DisposeAsync();
        await simulator.IncomingAsync(2, [21]);
        Assert.Equal(new byte[] { 21 }, (await TestSupport.Next(readerB)).ToArray());
        Assert.True(node.Children[1].IsConnected);
    }

    [Fact]
    public async Task ChildReceivePressureDoesNotPreventControlOrSiblingDelivery()
    {
        await using var simulator = new Parity6RNodeSimulator(multi: true);
        await using var node = new RNodeMultiPacketInterface(simulator.Host, MultiOptions() with { ReceiveQueueCapacity = 1 });
        await node.StartAsync(CancellationToken.None);
        await simulator.IncomingAsync(0, [1]);
        await simulator.IncomingAsync(0, [2]);
        await simulator.ControlAsync(0x23, [100], port: 2);
        await simulator.IncomingAsync(2, [3]);
        await using var reader = node.Children[1].ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.Equal(new byte[] { 3 }, (await TestSupport.Next(reader)).ToArray());
        Assert.Equal(-57, node.Children[1].GetDiagnostics()["rssi"]);
        Assert.Equal(1L, node.Children[0].GetDiagnostics()["dropped_packets"]);
    }

    [Fact]
    public async Task ReconnectRevalidatesFirmwareWithoutReplacingChildIdentity()
    {
        await using var first = new Parity6RNodeSimulator(multi: true);
        await using var second = new Parity6RNodeSimulator(multi: true);
        var attempts = 0;
        await using var node = new RNodeMultiPacketInterface(_ => ValueTask.FromResult(
            Interlocked.Increment(ref attempts) == 1 ? first.Host : second.Host), MultiOptions());
        var adopted = new List<IPacketInterface>();
        node.ChildConnected += adopted.Add;
        await node.StartAsync(CancellationToken.None);
        var child = node.Children[0];
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.Connected += () => reconnected.TrySetResult();
        await first.UnplugAsync();
        await reconnected.Task.WaitAsync(TestSupport.Patience);
        Assert.Same(child, node.Children[0]);
        Assert.Equal(2, adopted.Count);
        Assert.Equal(1L, node.GetDiagnostics()["reconnections"]);
        await child.SendAsync(new byte[] { 9 }, CancellationToken.None);
        Assert.Equal(new byte[] { 9 }, (await second.NextTransmissionAsync()).Payload);
    }

    [Fact]
    public async Task DisposingUnstartedAndFlowBlockedInterfaceIsIdempotentAndClosesStreams()
    {
        await using (var simulator = new Parity6RNodeSimulator())
        {
            var unstarted = new RNodePacketInterface(simulator.Host, Options());
            await unstarted.DisposeAsync();
            await unstarted.DisposeAsync();
            Assert.True(simulator.HostDisposed);
            Assert.Empty(simulator.HostWrites);
        }
        await using (var simulator = new Parity6RNodeSimulator())
        {
            var node = new RNodePacketInterface(simulator.Host, Options(flow: true));
            await node.StartAsync(CancellationToken.None);
            await node.SendAsync(new byte[] { 1 }, CancellationToken.None);
            var blocked = node.SendAsync(new byte[] { 2 }, CancellationToken.None).AsTask();
            await node.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            await node.DisposeAsync();
            Assert.True(simulator.HostDisposed);
            var writes = simulator.HostWrites.ToArray();
            Assert.Equal(RNodeProtocol.Frame(6, [0]), writes[^2]);
            Assert.Equal(RNodeProtocol.Frame(0x0a, [0xff]), writes[^1]);
        }
    }

    [Fact]
    public async Task StartupCancellationClosesNewlyOwnedDevice()
    {
        await using var simulator = new Parity6RNodeSimulator { DetectResponse = false };
        await using var node = new RNodePacketInterface(simulator.Host, Options());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.StartAsync(cancellation.Token).AsTask());
        Assert.True(simulator.HostDisposed);
    }

    [Fact]
    public async Task DiagnosticsDecodePhyAndNonfatalHardwareErrorsWithoutSubscribers()
    {
        await using var simulator = new Parity6RNodeSimulator();
        await using var node = new RNodePacketInterface(simulator.Host, Options());
        node.Connected += () => throw new InvalidOperationException("optional observer");
        await node.StartAsync(CancellationToken.None);
        await simulator.ControlAsync(0x23, [100]);
        await simulator.ControlAsync(0x24, [0xf0]);
        await simulator.ControlAsync(0x26, [4, 0, 3, 0xe8, 0, 8, 0, 16, 0, 7, 0, 9]);
        await simulator.ControlAsync(0x27, [2, 120]);
        await simulator.ControlAsync(0x29, [145]);
        await simulator.ControlAsync(0x90, [5]);
        await simulator.IncomingAsync(0, [0xaa]);
        await using var reader = node.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        await TestSupport.Next(reader);
        var diagnostics = node.GetDiagnostics();
        Assert.Equal(-57, diagnostics["rssi"]);
        Assert.Equal(-4.0, diagnostics["snr"]);
        Assert.Equal(33.3, diagnostics["quality"]);
        Assert.Equal(1.024, diagnostics["symbol_time_ms"]);
        Assert.Equal(9, diagnostics["csma_difs_ms"]);
        Assert.Equal(100, diagnostics["battery_percent"]);
        Assert.Equal(25, diagnostics["temperature"]);
        Assert.Equal(5, diagnostics["hardware_error"]);
        Assert.Equal(1L, diagnostics["observer_errors"]);
        Assert.True(node.IsConnected);
    }

    [Fact]
    public async Task ReceiveAndPhyStateMatchExecutedPythonReader()
    {
        using var vectors = Vectors();
        var receive = vectors.RootElement.GetProperty("single").GetProperty("receive");
        var observed = receive.GetProperty("observed");
        await using var simulator = new Parity6RNodeSimulator();
        await using var node = new RNodePacketInterface(simulator.Host, Options());
        await node.StartAsync(CancellationToken.None);
        await using var reader = node.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        await simulator.RawAsync(Hex(receive.GetProperty("wire")));
        Assert.Equal(Convert.FromHexString(observed.GetProperty("packets")[0].GetString()!),
            (await TestSupport.Next(reader)).ToArray());
        var diagnostics = node.GetDiagnostics();
        Assert.Equal(observed.GetProperty("r_stat_rssi").GetInt32(), diagnostics["rssi"]);
        Assert.Equal(observed.GetProperty("r_stat_snr").GetDouble(), diagnostics["snr"]);
        Assert.Equal(observed.GetProperty("r_stat_q").GetDouble(), diagnostics["quality"]);
        Assert.Equal(observed.GetProperty("r_symbol_time_ms").GetDouble(), diagnostics["symbol_time_ms"]);
        Assert.Equal(observed.GetProperty("r_symbol_rate").GetInt32(), diagnostics["symbol_rate"]);
        Assert.Equal(observed.GetProperty("r_preamble_symbols").GetInt32(), diagnostics["preamble_symbols"]);
        Assert.Equal(observed.GetProperty("r_premable_time_ms").GetInt32(), diagnostics["preamble_time_ms"]);
        Assert.Equal(observed.GetProperty("r_csma_slot_time_ms").GetInt32(), diagnostics["csma_slot_time_ms"]);
        Assert.Equal(observed.GetProperty("r_csma_difs_ms").GetInt32(), diagnostics["csma_difs_ms"]);
        Assert.Equal(observed.GetProperty("r_battery_percent").GetInt32(), diagnostics["battery_percent"]);
        Assert.Equal(observed.GetProperty("r_temperature").GetInt32(), diagnostics["temperature"]);
    }

    [Fact]
    public async Task MissingCodingRateAndMissingMultiVirtualPortFailClosed()
    {
        await using (var simulator = new Parity6RNodeSimulator { OmitCodingRate = true })
        {
            await using var node = new RNodePacketInterface(simulator.Host,
                Options() with { ResponseTimeout = TimeSpan.FromMilliseconds(200) });
            await Assert.ThrowsAsync<TimeoutException>(() => node.StartAsync(CancellationToken.None).AsTask());
        }
        await using (var simulator = new Parity6RNodeSimulator(multi: true) { Types = new Dictionary<int, byte> { [0] = 0 } })
        {
            await using var node = new RNodeMultiPacketInterface(simulator.Host,
                MultiOptions() with { ResponseTimeout = TimeSpan.FromMilliseconds(200) });
            await Assert.ThrowsAsync<TimeoutException>(() => node.StartAsync(CancellationToken.None).AsTask());
        }
    }

    [Fact]
    public async Task ParentEnableRuleAndOutgoingSelectionPolicyArePreservedWithoutBlockingDirectSends()
    {
        await using var simulator = new Parity6RNodeSimulator(multi: true);
        var options = MultiOptions();
        await using var node = new RNodeMultiPacketInterface(simulator.Host, options with
        {
            EnableAllChildren = true,
            Subinterfaces = [options.Subinterfaces[0] with { Enabled = false, Outgoing = false }],
        });
        await node.StartAsync(CancellationToken.None);
        var child = Assert.Single(node.Children);
        Assert.False(child.Outgoing);
        Assert.False(child.AllowOutgoingSelection);
        await child.SendAsync(new byte[] { 1 }, CancellationToken.None);
        Assert.Equal(new byte[] { 1 }, (await simulator.NextTransmissionAsync()).Payload);
        await using var reader = child.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        await simulator.IncomingAsync(0, [42]);
        Assert.Equal(new byte[] { 42 }, (await TestSupport.Next(reader)).ToArray());
    }

    [Theory]
    [InlineData(73, false)]
    [InlineData(74, true)]
    public async Task MultiRequiresFirmware174(int minor, bool success)
    {
        await using var simulator = new Parity6RNodeSimulator(multi: true) { FirmwareMinor = (byte)minor };
        await using var node = new RNodeMultiPacketInterface(simulator.Host, MultiOptions());
        if (success) await node.StartAsync(CancellationToken.None);
        else await Assert.ThrowsAsync<IOException>(() => node.StartAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task PartialIdleExpiryAndMalformedFramesDoNotDeliverTruncatedPackets()
    {
        await using var simulator = new Parity6RNodeSimulator();
        await using var node = new RNodePacketInterface(simulator.Host,
            Options() with { PartialFrameTimeout = TimeSpan.FromMilliseconds(20) });
        await node.StartAsync(CancellationToken.None);
        await simulator.RawAsync([0xc0, 0, 5, 0xdb]);
        await TestSupport.WaitFor(() => (long)node.GetDiagnostics()["malformed_frames"]! > 0);
        await simulator.RawAsync([0xdc, 3, 0xc0, 0, 1, 0xdb, 1, 0xc0]);
        await simulator.IncomingAsync(0, [9]);
        await using var reader = node.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.Equal(new byte[] { 9 }, (await TestSupport.Next(reader)).ToArray());
        Assert.Equal(2L, node.GetDiagnostics()["malformed_frames"]);
    }

    [Fact]
    public async Task Esp32ResetAndFailedReopenAreRetriedWithFreshConfiguration()
    {
        await using var first = new Parity6RNodeSimulator();
        await using var second = new Parity6RNodeSimulator();
        var attempts = 0;
        await using var node = new RNodePacketInterface(_ =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            return attempt switch
            {
                1 => ValueTask.FromResult(first.Host),
                2 => ValueTask.FromException<Stream>(new IOException("simulated unplug")),
                _ => ValueTask.FromResult(second.Host),
            };
        }, Options());
        await node.StartAsync(CancellationToken.None);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Connected += () => connected.TrySetResult();
        await first.ControlAsync(0x55, [0xf8]);
        await connected.Task.WaitAsync(TestSupport.Patience);
        Assert.Equal(3, attempts);
        Assert.True(first.HostDisposed);
        Assert.True(node.IsConnected);
        Assert.Equal(RNodeProtocol.Detect(), second.HostWrites.First());
    }

    [Fact]
    public async Task CallsignBeaconFollowsActivityOnEachOnlineRadioEvenWhenSelectionDisabled()
    {
        await using var simulator = new Parity6RNodeSimulator(multi: true);
        var options = MultiOptions();
        await using var node = new RNodeMultiPacketInterface(simulator.Host,
            options with
            {
                IdInterval = TimeSpan.FromMilliseconds(1), IdCallsign = "N0CALL",
                Subinterfaces = [options.Subinterfaces[0] with { Outgoing = false }, options.Subinterfaces[1]],
            });
        await node.StartAsync(CancellationToken.None);
        await node.Children[0].SendAsync(new byte[] { 1 }, CancellationToken.None);
        Assert.Equal(new byte[] { 1 }, (await simulator.NextTransmissionAsync()).Payload);
        var beaconA = await simulator.NextTransmissionAsync();
        var beaconB = await simulator.NextTransmissionAsync();
        Assert.Equal(new[] { 0, 2 }, new[] { beaconA.Port, beaconB.Port }.Order().ToArray());
        Assert.Equal("N0CALL"u8.ToArray(), beaconA.Payload);
        Assert.Equal("N0CALL"u8.ToArray(), beaconB.Payload);
    }

    [Fact]
    public void NestedDefaultsAndEnableAliasesMatchExecutedConstructor()
    {
        using var vectors = Vectors();
        var cases = vectors.RootElement.GetProperty("multi").GetProperty("nested_configuration");
        foreach (var test in cases.EnumerateArray().Take(2))
        {
            var child = Assert.Single(test.GetProperty("subinterfaces").EnumerateArray());
            Assert.Equal("radio-a", child[0].GetString());
            Assert.Equal("2", child[1].GetString());
            Assert.Equal(868000000, child[2].GetInt64());
            Assert.False(child[7].GetBoolean());
            Assert.Equal(JsonValueKind.Null, child[8].ValueKind);
            Assert.Equal(JsonValueKind.Null, child[9].ValueKind);
            Assert.False(child[10].GetBoolean());
            Assert.Equal(11, child.GetArrayLength());
        }
        Assert.Equal("No subinterfaces enabled for nested-oracle", cases[2].GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConfiguredBitrateAppliesToPostInitNotToChildConfigurationOrLaterRadioReports()
    {
        await using (var simulator = new Parity6RNodeSimulator())
        {
            await using var node = new RNodePacketInterface(simulator.Host, Options() with { ConfiguredBitrate = 9600 });
            await node.StartAsync(CancellationToken.None);
            Assert.Equal(9600, node.Bitrate);
            await simulator.ControlAsync(5, [8]);
            await simulator.IncomingAsync(0, [1]);
            await using var reader = node.ReceiveAsync(CancellationToken.None).GetAsyncEnumerator();
            await TestSupport.Next(reader);
            Assert.Equal(3417, node.Bitrate);
        }
        await using (var simulator = new Parity6RNodeSimulator(multi: true))
        {
            await using var node = new RNodeMultiPacketInterface(simulator.Host, MultiOptions() with { ConfiguredBitrate = 9600 });
            await node.StartAsync(CancellationToken.None);
            Assert.Equal(9600, node.Bitrate);
            Assert.All(node.Children, c => Assert.Equal(5468, c.Bitrate));
        }
    }

    [Fact]
    public async Task DisplayControlsAndResponsesMatchPinnedPythonWithoutHardware()
    {
        using var vectors = Vectors();
        var single = vectors.RootElement.GetProperty("single");
        var expected = single.GetProperty("display_commands").EnumerateArray().Select(Hex).ToArray();
        await using var simulator = new Parity6RNodeSimulator();
        await using var node = new RNodePacketInterface(simulator.Host, Options());
        await node.StartAsync(CancellationToken.None);
        Assert.True(node.DisplayAvailable);
        var before = simulator.HostWrites.Count;
        await node.EnableExternalFramebufferAsync();
        await node.DisableExternalFramebufferAsync();
        await node.WriteFramebufferLineAsync(0xc0, new byte[] { 0xc0, 0xdb, 1, 2, 3, 4, 5, 6 });
        var framebuffer = await node.ReadFramebufferAsync();
        var display = await node.ReadDisplayAsync();
        var writes = simulator.HostWrites.Skip(before).ToArray();
        Assert.Equal(5, writes.Length);
        for (var i = 0; i < writes.Length; i++) Assert.Equal(expected[i], writes[i]);
        var observed = single.GetProperty("receive").GetProperty("observed");
        Assert.Equal(Convert.FromHexString(observed.GetProperty("framebuffer").GetString()!), framebuffer.ToArray());
        Assert.Equal(Convert.FromHexString(observed.GetProperty("display").GetString()!), display.ToArray());
        Assert.Equal(framebuffer.ToArray(), node.LastFramebuffer.ToArray());
        Assert.Equal(display.ToArray(), node.LastDisplay.ToArray());
        await node.DisplayImageAsync(new byte[17]);
        Assert.Equal(2, simulator.HostWrites.Count - before - 5);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => node.WriteFramebufferLineAsync(256, new byte[8]).AsTask());
    }

    [Fact]
    public async Task TcpUriConnectsToLoopbackDeviceAndSendsActivityDetectionKeepalive()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var lifetime = new CancellationTokenSource(TestSupport.Patience);
        var accept = listener.AcceptTcpClientAsync(lifetime.Token).AsTask();
        await using var node = new RNodePacketInterface("TcP://127.0.0.1", Options() with
        {
            Tcp = new() { Port = port, ActivityKeepalive = TimeSpan.FromMilliseconds(30) },
        });
        var startup = node.StartAsync(lifetime.Token).AsTask();
        using var client = await accept;
        await using var simulator = new Parity6RNodeSimulator(client.GetStream());
        await startup;
        Assert.True(node.IsConnected);
        await node.SendAsync(new byte[] { 1, 0xc0, 0xdb }, lifetime.Token);
        Assert.Equal(new byte[] { 1, 0xc0, 0xdb }, (await simulator.NextTransmissionAsync()).Payload);
        await TestSupport.WaitFor(() => simulator.ReceivedCommands.Count(c => c.Command == 8) > 1);
        using var vectors = Vectors();
        var defaults = vectors.RootElement.GetProperty("tcp_defaults");
        Assert.Equal(defaults.GetProperty("TARGET_PORT").GetInt32(), new RNodeOptions.RNodeTcpOptions().Port);
        Assert.Equal(defaults.GetProperty("ACTIVITY_KEEPALIVE").GetDouble(), new RNodeOptions.RNodeTcpOptions().ActivityKeepalive.TotalSeconds);
    }

    [Fact]
    public async Task DisplayPollingIsCancelableAndTimeoutDoesNotBreakSubsequentReads()
    {
        await using var simulator = new Parity6RNodeSimulator { ReplyDisplays = false };
        await using var node = new RNodePacketInterface(simulator.Host,
            Options() with { ResponseTimeout = TimeSpan.FromMilliseconds(200) });
        await node.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => node.ReadDisplayAsync().AsTask());
        simulator.ReplyDisplays = true;
        using var cancel = new CancellationTokenSource();
        await using var updates = node.ReadDisplayUpdatesAsync(TimeSpan.FromMilliseconds(1), cancel.Token).GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal(1024, updates.Current.Length);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updates.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task AvrDisplayWritesAreNoOpsButExplicitResetUsesOracleCommand()
    {
        await using var simulator = new Parity6RNodeSimulator { Platform = 0x90 };
        await using var node = new RNodePacketInterface(simulator.Host, Options());
        await node.StartAsync(CancellationToken.None);
        Assert.False(node.DisplayAvailable);
        var before = simulator.HostWrites.Count;
        await node.EnableExternalFramebufferAsync();
        await node.WriteFramebufferLineAsync(0, new byte[8]);
        Assert.Equal(before, simulator.HostWrites.Count);
        using var cancellation = new CancellationTokenSource();
        var reset = node.HardResetAsync(cancellation.Token).AsTask();
        await TestSupport.WaitFor(() => simulator.HostWrites.Count > before);
        using var vectors = Vectors();
        var expected = vectors.RootElement.GetProperty("single").GetProperty("display_commands").EnumerateArray().Last();
        Assert.Equal(Hex(expected), simulator.HostWrites.Last());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
    }

    [Fact]
    public async Task LiveRadioControlsValidateEchoAndPersistParametersAcrossReconnect()
    {
        await using var first = new Parity6RNodeSimulator();
        await using var second = new Parity6RNodeSimulator();
        var attempts = 0;
        await using var node = new RNodePacketInterface(_ => ValueTask.FromResult(
            Interlocked.Increment(ref attempts) == 1 ? first.Host : second.Host), Options());
        await node.StartAsync(CancellationToken.None);
        var changed = Radio() with { Frequency = 868100000, TxPower = 10, SpreadingFactor = 8, CodingRate = 6 };
        await node.ConfigureRadioAsync(changed);
        Assert.Equal(2604, node.Bitrate);
        await node.SetRadioStateAsync(false);
        Assert.Equal(RNodeProtocol.Frame(6, [0]), first.HostWrites.Last());
        await node.RefreshDeviceInformationAsync();
        Assert.Equal(RNodeProtocol.Detect(), first.HostWrites.Last());
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Connected += () => connected.TrySetResult();
        await first.UnplugAsync();
        await connected.Task.WaitAsync(TestSupport.Patience);
        Assert.Equal(RNodeProtocol.Configuration(changed)[0], second.HostWrites.ToArray()[1]);
        Assert.Equal(2604, node.Bitrate);
        await Assert.ThrowsAsync<ArgumentException>(() => node.ConfigureRadioAsync(changed with { FlowControl = true }).AsTask());
    }

    [Fact]
    public async Task SimulatorUnplugCancelsAndJoinsBeforeCompletingOwnedReader()
    {
        var simulator = new Parity6RNodeSimulator();
        await Task.WhenAll(simulator.UnplugAsync().AsTask(), simulator.UnplugAsync().AsTask()).WaitAsync(TestSupport.Patience);
        Assert.Null(simulator.PumpFailure);
        await Task.WhenAll(simulator.DisposeAsync().AsTask(), simulator.DisposeAsync().AsTask()).WaitAsync(TestSupport.Patience);
        Assert.True(simulator.HostDisposed);
    }

    [Fact]
    public async Task SimulatorPreservesUnexpectedPumpFailuresWhileStillDisposingDevice()
    {
        var device = new FailingSimulatorStream();
        var simulator = new Parity6RNodeSimulator(device);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => simulator.DisposeAsync().AsTask());
        Assert.Equal("unexpected simulator read failure", error.Message);
        Assert.Same(error, simulator.PumpFailure);
        Assert.True(device.Disposed);
    }

    private sealed class FailingSimulatorStream : MemoryStream
    {
        public bool Disposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new InvalidOperationException("unexpected simulator read failure"));
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
