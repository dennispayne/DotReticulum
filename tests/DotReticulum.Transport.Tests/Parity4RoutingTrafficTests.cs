using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// Direct Interface.py execution at a29bfb51c468945c96a1ee22cae0114e993e2391,
// RNS1.5.7. Generator: GenerateParity4RoutingTrafficVectors.py. Not live interoperability.
public sealed class Parity4RoutingTrafficTests
{
    private const long T0 = 1_800_000_000;
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Parity4RoutingTraffic-vectors.json"))).RootElement;

    [Fact]
    public async Task ActualAnnounceReceivesDriveSourceBurstLifecycleWithoutOperatorDelegates()
    {
        await using var rig = await Rig.CreateAsync();
        using var identity = Identity.Generate();
        var index = 0;
        foreach (var step in Vectors.GetProperty("announceCases").EnumerateArray())
        {
            rig.Time.Seconds = step.GetProperty("seconds").GetDouble();
            var packet = Announcement(identity, index++);
            await rig.Receive(0, packet);
            Assert.Equal(!step.GetProperty("limited").GetBoolean(), rig.Router.HasPath(packet.DestinationHash.Span));
            var info = rig.Router.GetInterfaceTrafficControl(rig.Ifaces[0]);
            Assert.Equal(step.GetProperty("active").GetBoolean(), info.AnnounceBurstActive);
            Assert.Equal(step.GetProperty("received").GetInt64(), info.ReceivedAnnounces);
            Assert.Equal(step.GetProperty("samples").GetInt32(), info.AnnounceSamples);
            Assert.Equal(step.GetProperty("frequency").GetDouble(), info.IncomingAnnounceFrequency, precision: 5);
            Assert.True(info.ReceivedAnnounceBytes > 0);
        }
    }

    [Fact]
    public async Task ActualPathRequestsDriveSourceBurstCooldownWithoutOperatorDelegates()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Router.SetInterfaceMode(rig.Ifaces[0], InterfaceMode.Gateway);
        byte index = 1;
        foreach (var step in Vectors.GetProperty("requestCases").EnumerateArray())
        {
            rig.Time.Seconds = step.GetProperty("seconds").GetDouble();
            var before = rig.Ifaces[1].Sent.Count;
            await rig.Receive(0, Request(index++));
            Assert.Equal(!step.GetProperty("limited").GetBoolean(), rig.Ifaces[1].Sent.Count > before);
            var info = rig.Router.GetInterfaceTrafficControl(rig.Ifaces[0]);
            Assert.Equal(step.GetProperty("active").GetBoolean(), info.PathRequestBurstActive);
            Assert.Equal(step.GetProperty("received").GetInt64(), info.ReceivedPathRequests);
            Assert.Equal(step.GetProperty("samples").GetInt32(), info.PathRequestSamples);
            Assert.Equal(step.GetProperty("frequency").GetDouble(), info.IncomingPathRequestFrequency, precision: 5);
        }
    }

    [Fact]
    public async Task ActualTransmitsDrivePreemptiveEgressThresholdAndSamplesRemainBounded()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[0], new InterfaceAnnounceOptions
        { Ingress = new InterfaceIngressOptions { EgressEnabled = true } });
        byte index = 1;
        foreach (var step in Vectors.GetProperty("egressCases").EnumerateArray())
        {
            rig.Time.Seconds = step.GetProperty("seconds").GetDouble();
            if (step.GetProperty("send").GetBoolean())
                await rig.Router.RequestPathAsync(Enumerable.Repeat(index++, 16).ToArray(), rig.Ifaces[0]);
            Assert.Equal(step.GetProperty("limited").GetBoolean(), rig.Router.ShouldEgressLimitPathRequests(rig.Ifaces[0]));
            var info = rig.Router.GetInterfaceTrafficControl(rig.Ifaces[0]);
            Assert.Equal(step.GetProperty("sent").GetInt64(), info.SentPathRequests);
            Assert.Equal(step.GetProperty("samples").GetInt32(), info.OutgoingPathRequestSamples);
            Assert.Equal(step.GetProperty("frequency").GetDouble(), info.OutgoingPathRequestFrequency, precision: 5);
        }
        for (var count = 0; count < 100; count++)
            await rig.Router.RequestPathAsync(Enumerable.Repeat((byte)count, 16).ToArray(), rig.Ifaces[0]);
        Assert.Equal(Vectors.GetProperty("sampleBounds")[3].GetInt32(), rig.Router.GetInterfaceTrafficControl(rig.Ifaces[0]).OutgoingPathRequestSamples);
    }

    [Fact]
    public async Task HeldAnnouncesRespectBurstPenaltyReleaseIntervalClosestHopAndCapacity()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[0], new InterfaceAnnounceOptions
        {
            HeldAnnounceCapacity = 2,
            Ingress = new InterfaceIngressOptions
            {
                NewTime = TimeSpan.Zero, BurstFrequency = 2,
                BurstHold = TimeSpan.FromSeconds(1), BurstPenalty = TimeSpan.FromSeconds(3),
                HeldReleaseInterval = TimeSpan.FromSeconds(2),
            },
        });
        using var identity = Identity.Generate();
        await rig.Receive(0, Announcement(identity, 0));
        rig.Time.Seconds = .1;
        await rig.Receive(0, Announcement(identity, 1));
        rig.Time.Seconds = .2;
        var far = Announcement(identity, 2, hops: 5);
        await rig.Receive(0, far);
        rig.Time.Seconds = .3;
        var near = Announcement(identity, 3, hops: 1);
        await rig.Receive(0, near);
        rig.Time.Seconds = .4;
        await rig.Receive(0, Announcement(identity, 4));
        Assert.Equal(2, rig.Router.HeldIngressAnnounces(rig.Ifaces[0]));
        foreach (var step in Vectors.GetProperty("heldReleaseCases").EnumerateArray())
        {
            rig.Time.Seconds = step.GetProperty("seconds").GetDouble();
            await rig.Router.FlushAnnouncesAsync();
            Assert.Equal(step.GetProperty("held").GetInt32(), rig.Router.HeldIngressAnnounces(rig.Ifaces[0]));
            Assert.Equal(step.GetProperty("nearAccepted").GetBoolean(), rig.Router.HasPath(near.DestinationHash.Span));
            Assert.Equal(step.GetProperty("farAccepted").GetBoolean(), rig.Router.HasPath(far.DestinationHash.Span));
        }
    }

    [Fact]
    public async Task RecursivePrsEnablesFullModeAndDisabledIngressDoesNotHoldBursts()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Receive(0, Request(1));
        Assert.Empty(rig.Ifaces[1].Sent);
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[0], new InterfaceAnnounceOptions
        { RecursivePathRequests = true, Ingress = new InterfaceIngressOptions { Enabled = false } });
        await rig.Receive(0, Request(2));
        Assert.Single(rig.Ifaces[1].Sent);
        using var identity = Identity.Generate();
        for (var index = 0; index < 10; index++)
        {
            rig.Time.Seconds += .1;
            var packet = Announcement(identity, index);
            await rig.Receive(0, packet);
            Assert.True(rig.Router.HasPath(packet.DestinationHash.Span));
        }
        Assert.Equal(0, rig.Router.HeldIngressAnnounces(rig.Ifaces[0]));
        rig.Router.DetachInterface(rig.Ifaces[0]);
        Assert.Equal(0, rig.Router.GetInterfaceTrafficControl(rig.Ifaces[0]).ReceivedAnnounces);
    }

    [Fact]
    public async Task EarlyQueueClassificationCountsEachPacketOnceAndValidatesBeforeCounting()
    {
        await using var rig = await Rig.CreateAsync();
        using var identity = Identity.Generate();
        var first = Announcement(identity, 0);
        Assert.True(rig.Router.TryQueueInbound(Received(rig.Ifaces[0], first)));
        Assert.True(rig.Router.TryQueueInbound(Received(rig.Ifaces[0], Request(1))));
        await rig.Router.ProcessQueuedInboundAsync();
        var info = rig.Router.GetInterfaceTrafficControl(rig.Ifaces[0]);
        Assert.Equal(1, info.ReceivedAnnounces);
        Assert.Equal(1, info.ReceivedPathRequests);
        var invalid = first.ToArray();
        invalid[^1] ^= 1;
        Assert.False(rig.Router.TryQueueInbound(Received(rig.Ifaces[0], Packet.Parse(invalid))));
        Assert.False(rig.Router.TryQueueInbound(Received(rig.Ifaces[0], Request(1))));
        Assert.Equal(1, rig.Router.GetInterfaceTrafficControl(rig.Ifaces[0]).ReceivedAnnounces);
        Assert.Equal(1, rig.Router.GetInterfaceTrafficControl(rig.Ifaces[0]).ReceivedPathRequests);
    }

    private static Packet Announcement(Identity identity, int index, int hops = 0)
    {
        var packet = Announce.Create(identity, Destination.Create(DestinationType.Single, "traffic", [index.ToString(System.Globalization.CultureInfo.InvariantCulture)], identity), unixSeconds: T0);
        return Packet.Create(PacketType.Announce, DestinationType.Single, packet.DestinationHash, packet.Payload, hops: (byte)hops);
    }
    private static Packet Request(byte value)
    {
        var bytes = Enumerable.Repeat(value, 16).ToArray();
        return Packet.Create(PacketType.Data, DestinationType.Plain, TransportRouter.PathRequestHash, (byte[])[.. bytes, .. bytes]);
    }
    private static ReceivedPacket Received(IPacketInterface source, Packet packet) => new(packet.ToArray(), packet, source);
    private sealed class Clock : TimeProvider
    {
        public double Seconds { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(T0 * 1000 + (long)Math.Round(Seconds * 1000));
    }
    private sealed class Bearer : IPacketInterface
    {
        public List<byte[]> Sent { get; } = [];
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(packet.ToArray());
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Rig : IAsyncDisposable
    {
        public Clock Time { get; } = new();
        public Bearer[] Ifaces { get; } = [new(), new()];
        public PacketInterfaceManager Manager { get; private set; } = null!;
        public TransportRouter Router { get; private set; } = null!;
        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig();
            rig.Manager = new PacketInterfaceManager(rig.Ifaces);
            await rig.Manager.StartAsync();
            rig.Router = new TransportRouter(rig.Manager, new TransportRouterOptions { TransportEnabled = true, TimeProvider = rig.Time });
            return rig;
        }
        public ValueTask Receive(int iface, Packet packet) => Router.HandleAsync(Received(Ifaces[iface], packet));
        public async ValueTask DisposeAsync()
        {
            await Router.DisposeAsync();
            await Manager.DisposeAsync();
        }
    }
}
