using System.Text.Json;
using System.Net;
using DotReticulum.Core;

namespace DotReticulum.Interfaces.Tests;

[Trait("Category", "Integration")]
public sealed class Parity8PhyTests
{
    [Fact]
    public async Task NonreportingTcpBearerLeavesPacketAndCacheMeasurementsAbsent()
    {
        var server = new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, 0));
        await using var manager = new PacketInterfaceManager([server]);
        await manager.StartAsync();
        await using var client = new TcpClientPacketInterface("127.0.0.1", server.LocalEndpoint!.Port);
        await client.StartAsync(CancellationToken.None);
        var raw = TestSupport.MakePacket(9);
        await client.SendAsync(raw, CancellationToken.None);
        await using var reader = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(raw, reader.Current.Raw.ToArray());
        Assert.Equal(default, reader.Current.Packet.PhysicalLayer);
        Assert.Equal(default, manager.GetPacketPhysicalLayer(reader.Current.Packet.CalculateHash()));
    }

    private static RNodeOptions Options() => new()
    {
        Radio = new() { Frequency = 868000000, Bandwidth = 125000, TxPower = 7, SpreadingFactor = 7, CodingRate = 5 },
        DeviceSettleDelay = TimeSpan.Zero, ReconnectDelay = TimeSpan.FromMilliseconds(10),
        ResponseTimeout = TimeSpan.FromSeconds(1),
    };

    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Parity8PhyVectors.json")));
    private static PacketPhysicalLayer Phy(JsonElement value) => new(value.GetProperty("rssi").GetDouble(),
        value.GetProperty("snr").GetDouble(), value.GetProperty("quality").GetDouble());
    private static byte[] Raw(JsonElement value) => Convert.FromHexString(value.GetProperty("raw").GetString()!);
    private static async Task Reading(Parity6RNodeSimulator simulator, int rssi, double snr, int port = 0)
    {
        await simulator.ControlAsync(0x23, [(byte)(rssi + 157)], port);
        await simulator.ControlAsync(0x24, [unchecked((byte)(sbyte)(snr * 4))], port);
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private static async Task<ReceivedFrame> Next(IAsyncEnumerator<ReceivedFrame> reader)
    {
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        return reader.Current;
    }

    [Fact]
    public async Task ReadingsAreCapturedAtDeviceFrameNotLaterQueueEnumeration()
    {
        using var vectors = Vectors();
        var values = vectors.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        await using var simulator = new Parity6RNodeSimulator();
        await using var node = new RNodePacketInterface(simulator.Host, Options());
        await node.StartAsync(CancellationToken.None);
        await Reading(simulator, -91, 4.25);
        await simulator.IncomingAsync(0, Raw(values[0]));
        await Reading(simulator, -113, -7.5);
        await simulator.IncomingAsync(0, Raw(values[1]));
        // Wait until the later reading has actually been decoded before reading either queued frame.
        await Until(() => Equals(node.GetDiagnostics().GetValueOrDefault("snr"), -7.5));
        await using var reader = node.ReceiveFramesAsync(CancellationToken.None).GetAsyncEnumerator();
        var first = await Next(reader);
        var second = await Next(reader);
        Assert.Equal(Phy(values[0]), first.PhysicalLayer);
        Assert.Equal(Phy(values[1]), second.PhysicalLayer);
        Assert.Equal(Raw(values[0]), first.Data.ToArray());
        Assert.Equal(Raw(values[1]), second.Data.ToArray());
    }

    [Fact]
    public async Task ReconnectClearsDeviceMeasurementsButNotEarlierPacketSnapshots()
    {
        await using var first = new Parity6RNodeSimulator();
        await using var second = new Parity6RNodeSimulator();
        var opened = 0;
        await using var node = new RNodePacketInterface(_ =>
            ValueTask.FromResult(Interlocked.Increment(ref opened) == 1 ? first.Host : second.Host), Options());
        await node.StartAsync(CancellationToken.None);
        await using var reader = node.ReceiveFramesAsync(CancellationToken.None).GetAsyncEnumerator();
        await Reading(first, -91, 4.25);
        await first.IncomingAsync(0, TestSupport.MakePacket(1));
        var old = await Next(reader);
        await first.UnplugAsync();
        await Until(() => Volatile.Read(ref opened) == 2 && node.IsConnected);
        Assert.Null(node.GetDiagnostics().GetValueOrDefault("rssi"));
        await second.IncomingAsync(0, TestSupport.MakePacket(2));
        Assert.Equal(default, (await Next(reader)).PhysicalLayer);
        Assert.Equal(new(-91, 4.25, 88.3), old.PhysicalLayer);
    }

    [Fact]
    public async Task ExplicitDeviceResetClearsMeasurementsWithoutRewritingAlreadyQueuedFrames()
    {
        await using var simulator = new Parity6RNodeSimulator();
        await using var node = new RNodePacketInterface(simulator.Host, Options());
        await node.StartAsync(CancellationToken.None);
        await Reading(simulator, -91, 4.25);
        var first = TestSupport.MakePacket(1);
        await simulator.IncomingAsync(0, first);
        await Until(() => node.GetDiagnostics().GetValueOrDefault("rxb") is long bytes && bytes >= first.Length);
        await node.HardResetAsync();
        await simulator.IncomingAsync(0, TestSupport.MakePacket(2));
        await using var reader = node.ReceiveFramesAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.Equal(new(-91, 4.25, 88.3), (await Next(reader)).PhysicalLayer);
        Assert.Equal(default, (await Next(reader)).PhysicalLayer);
    }

    [Fact]
    public async Task MultiRadioSelectionNeverAttributesAnotherChildsMeasurements()
    {
        await using var simulator = new Parity6RNodeSimulator(multi: true);
        await using var node = new RNodeMultiPacketInterface(simulator.Host, new()
        {
            DeviceSettleDelay = TimeSpan.Zero, ResponseTimeout = TimeSpan.FromSeconds(1),
            Subinterfaces =
            [
                new() { Name = "first", Enabled = true, VirtualPort = 0, Frequency = 868000000,
                    Bandwidth = 125000, TxPower = 7, SpreadingFactor = 7, CodingRate = 5 },
                new() { Name = "second", Enabled = true, VirtualPort = 2, Frequency = 868000000,
                    Bandwidth = 125000, TxPower = 7, SpreadingFactor = 7, CodingRate = 5 },
            ],
        });
        await using var manager = new PacketInterfaceManager([node], channelCapacity: 1);
        await manager.StartAsync();
        await using var reader = manager.ReceiveWithSourceAsync().GetAsyncEnumerator();
        await Reading(simulator, -91, 4.25, 0);
        await Reading(simulator, -113, -7.5, 2);
        await simulator.IncomingAsync(0, TestSupport.MakePacket(1));
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var first = reader.Current;
        await simulator.IncomingAsync(2, TestSupport.MakePacket(2));
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var second = reader.Current;
        Assert.Same(node.Children[0], first.Source);
        Assert.Same(node.Children[1], second.Source);
        Assert.Equal(new(-91, 4.25, 88.3), first.Packet.PhysicalLayer);
        Assert.Equal(new(-113, -7.5, 10), second.Packet.PhysicalLayer);
        Assert.Equal(first.Packet.PhysicalLayer, manager.GetPacketPhysicalLayer(first.Packet.CalculateHash()));
        Assert.Equal(second.Packet.PhysicalLayer, manager.GetPacketPhysicalLayer(second.Packet.CalculateHash()));
    }
}
