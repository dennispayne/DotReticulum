using System.Runtime.CompilerServices;
using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// GenerateMediumTimingVectors.py executes RNS a29bfb51c468945c96a1ee22cae0114e993e2391:
// Transport.prioritize_interfaces, medium_path_timeout and first_hop_timeout.
[Trait("Category", "Unit")]
public sealed class MediumTimingTests
{
    private static JsonElement Vectors => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "medium-timing-vectors.json"))).RootElement;

    [Fact]
    public async Task OnlineMediumSelectionAndMinimumBitrateClampMatchExecutedReference()
    {
        foreach (var vector in Vectors.GetProperty("cases").EnumerateArray())
        {
            var interfaces = vector.GetProperty("interfaces").EnumerateArray()
                .Select(value => new Probe(value.GetProperty("bitrate").GetInt64(), value.GetProperty("online").GetBoolean())).ToArray();
            await using var manager = new PacketInterfaceManager(interfaces);
            await using var router = new TransportRouter(manager);
            var expected = vector.GetProperty("lowest");
            Assert.Equal(expected.ValueKind == JsonValueKind.Null ? null : expected.GetDouble(), router.GetLowestInterfaceBitrate());
            Assert.Equal(vector.GetProperty("medium").GetDouble(), router.GetMediumPathTimeout(), 10);
        }
    }

    [Fact]
    public async Task FirstHopTimeoutUsesKnownPathButNotUnrelatedSlowestMedium()
    {
        foreach (var vector in Vectors.GetProperty("firstHops").EnumerateArray())
        {
            var rate = vector.GetProperty("bitrate");
            var iface = new Probe(rate.ValueKind == JsonValueKind.Null ? 0 : rate.GetInt64(), true);
            await using var manager = new PacketInterfaceManager([iface, new Probe(1, true)]);
            await using var router = new TransportRouter(manager);
            using var identity = Identity.Generate();
            var destination = Destination.Create(DestinationType.Single, "medium", ["timing"], identity);
            if (rate.ValueKind != JsonValueKind.Null)
            {
                var announce = Announce.Create(identity, destination);
                await router.HandleAsync(new(announce.ToArray(), announce, iface));
            }
            Assert.Equal(vector.GetProperty("timeout").GetDouble(), router.FirstHopTimeout(destination.Hash), 10);
        }
    }

    [Fact]
    public async Task DynamicOnlineAndBitrateChangesAreObservedWithoutRestart()
    {
        var radio = new Probe(1200, true);
        var fast = new Probe(10000000, true);
        await using var manager = new PacketInterfaceManager([radio, fast]);
        await using var router = new TransportRouter(manager);
        Assert.Equal(1200, router.GetLowestInterfaceBitrate());
        radio.Online = false;
        Assert.Equal(10000000, router.GetLowestInterfaceBitrate());
        fast.Bitrate = 5;
        Assert.Equal(1606, router.GetMediumPathTimeout());
        fast.Online = false;
        Assert.Null(router.GetLowestInterfaceBitrate());
        Assert.Equal(0, router.GetMediumPathTimeout());
    }

    private sealed class Probe(long bitrate, bool online) : IPacketInterface, IPacketInterfaceDiagnostics
    {
        public long Bitrate { get; set; } = bitrate;
        public bool Online { get; set; } = online;
        public IReadOnlyDictionary<string, object?> GetDiagnostics() => new Dictionary<string, object?> { ["status"] = Online };
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
