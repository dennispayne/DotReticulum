using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// Executed reference: a29bfb51c468945c96a1ee22cae0114e993e2391.
// GenerateParity4ReceiptLinkMtuVectors.py records imported source SHA-256 values.
// Link.py:148-212,306-310,365-432; Transport.py:2529-2565 clamps inbound link requests.
[Trait("Category", "Unit")]
public class Parity4LinkMtuTests
{
    internal static JsonElement Vectors { get; } = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity4-receipt-link-mtu-vectors.json"))).RootElement;

    internal static byte[] Hex(JsonElement value, string key) => Convert.FromHexString(value.GetProperty(key).GetString()!);
    internal static JsonElement MtuVector(int advertised) => Vectors.GetProperty("mtus").EnumerateArray()
        .Single(entry => entry.GetProperty("advertised").GetInt32() == advertised);
    internal static ReceivedPacket Received(Packet packet, IPacketInterface iface) => new(packet.ToArray(), packet, iface);

    internal sealed class Probe : IPacketInterface
    {
        private readonly Channel<ReadOnlyMemory<byte>> _inbound = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public ConcurrentQueue<Packet> Sent { get; } = new();
        public int HardwareMtu { get; init; } = Packet.Mtu;
        public bool SupportsMtuDiscovery { get; init; }
        public long Bitrate { get; init; }
        public Probe? Peer { get; set; }
        public Func<Packet, bool>? Drop { get; set; }
        public bool ThrowOnSend { get; set; }
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) => _inbound.Reader.ReadAllAsync(cancellationToken);
        public ValueTask SendAsync(ReadOnlyMemory<byte> raw, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnSend) throw new IOException("Owned test bearer failed.");
            var packet = Packet.Parse(raw, Math.Max(Packet.Mtu, HardwareMtu));
            Sent.Enqueue(packet);
            if (Drop?.Invoke(packet) != true) Peer?._inbound.Writer.TryWrite(raw.ToArray());
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Clock : TimeProvider
    {
        private long _ticks;
        public void Advance(double seconds) => _ticks += checked((long)Math.Round(seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero));
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1700000000).AddTicks(_ticks);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    internal sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private Task _run = Task.CompletedTask;
        public Probe Interface { get; }
        public PacketInterfaceManager Manager { get; }
        public TransportRouter Router { get; }
        private Harness(Probe iface, TimeProvider? clock)
        {
            Interface = iface;
            Manager = new PacketInterfaceManager([iface]);
            Router = new TransportRouter(Manager, new TransportRouterOptions { TimeProvider = clock });
        }
        public static async Task<Harness> Create(Probe? iface = null, bool run = false, TimeProvider? clock = null)
        {
            var result = new Harness(iface ?? new Probe(), clock);
            await result.Manager.StartAsync();
            if (run) result._run = result.Router.RunAsync(result._cancellation.Token);
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            try { await _run; } catch (OperationCanceledException) { }
            await Router.DisposeAsync();
            await Manager.DisposeAsync();
            _cancellation.Dispose();
        }
    }

    internal static Link FixedInitiator(Harness node)
    {
        using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
        var link = Link.CreateInitiator(node.Router.Links, Identity.FromPublicKey(identity.ExportPublicKey()),
            Hex(Vectors, "destinationHash"), 1, 30, Hex(Vectors, "initiatorX25519Private"), Hex(Vectors, "initiatorSigSeed"));
        link.RequestTime = node.Router.Links.Now - 0.25;
        Assert.True(link.ValidateProof(Received(Packet.Parse(Hex(MtuVector(500), "proofRaw")), node.Interface)));
        return link;
    }

    [Fact]
    public async Task EveryReferenceMtuHasIdenticalStaticProofCodecAndOperationalEndpointsHonorBoundedBearer()
    {
        foreach (var vector in Vectors.GetProperty("mtus").EnumerateArray())
        {
            var mtu = vector.GetProperty("mtu").GetInt32();
            await using var node = await Harness.Create(new Probe { HardwareMtu = mtu, SupportsMtuDiscovery = true });
            using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
            var owner = new LocalDestination(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["parity4", "link"], identity)) { AcceptLinks = true };
            var request = Packet.Parse(Hex(vector, "requestRaw"));
            var referenceProof = Packet.Parse(Hex(vector, "proofRaw"));
            Assert.True(Link.TryValidateProofData(Hex(vector, "linkId"), referenceProof.Payload.Span, identity, out var decodedMtu, out var ephemeral));
            Assert.Equal(mtu, decodedMtu);
            Assert.Equal(vector.GetProperty("mdu").GetInt32(), Link.ComputeMdu(decodedMtu));
            Assert.Equal(referenceProof.ToArray(), Link.BuildProofPacket(Hex(vector, "linkId"), ephemeral, identity, decodedMtu).ToArray());
            var responder = Link.ValidateRequest(node.Router.Links, owner, Received(request, node.Interface), Hex(Vectors, "responderX25519Private"));
            Assert.NotNull(responder);
            var operationalMtu = Math.Min(mtu, Link.MaximumOperationalMtu);
            Assert.Equal(operationalMtu, responder.Mtu);
            Assert.Equal(Link.ComputeMdu(operationalMtu), responder.Mdu);
            Assert.Equal(Hex(vector, "derivedKey"), responder.DerivedKeyForTests);
            await LinkTests.Until(() => node.Interface.Sent.Any(packet => packet.Context == LinkContext.LrProof));
            Assert.Equal(Hex(MtuVector(operationalMtu), "proofRaw"), node.Interface.Sent.First(packet => packet.Context == LinkContext.LrProof).ToArray());

            var initiator = FixedInitiator(node);
            Assert.Equal(500, initiator.Mtu);
            Assert.Equal(431, initiator.Mdu);
            Assert.Equal(Hex(vector, "derivedKey"), initiator.DerivedKeyForTests);
            responder.Teardown();
        }
    }

    [Theory]
    [InlineData(16384, true, 16384)]
    [InlineData(16384, false, 500)]
    [InlineData(0, true, 500)]
    public async Task OutgoingBearerCapabilityDeterminesOperationalOffer(int hardwareMtu, bool enabled, int expected)
    {
        await using var node = await Harness.Create(new Probe { HardwareMtu = hardwareMtu, SupportsMtuDiscovery = enabled });
        using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
        var destination = Destination.Create(DestinationType.Single, "dotreticulum", ["parity4", "link"], identity);
        await node.Router.HandleAsync(Received(Announce.Create(identity, destination), node.Interface));
        var link = Link.CreateInitiator(node.Router.Links, identity, destination.Hash, 1, 30,
            Hex(Vectors, "initiatorX25519Private"), Hex(Vectors, "initiatorSigSeed"));
        Assert.Equal(expected, Link.InterfaceMtu(node.Interface));
        Assert.Equal(expected, Link.MtuFromSignalling(link.RequestPacket!.Payload.Span[64..]));
        Assert.Equal(Hex(MtuVector(expected), "requestRaw"), link.RequestPacket.ToArray());
        link.Teardown();
    }

    [Theory]
    [InlineData(true, 1024, 1024)]
    [InlineData(false, 262144, 500)]
    public async Task ResponderClampsToReceivingBearerBeforeSigning(bool discovery, int hardwareMtu, int expected)
    {
        await using var node = await Harness.Create(new Probe { HardwareMtu = hardwareMtu, SupportsMtuDiscovery = discovery });
        using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
        var owner = new LocalDestination(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["parity4", "link"], identity));
        var request = Packet.Parse(Hex(MtuVector(262144), "requestRaw"));
        var responder = Link.ValidateRequest(node.Router.Links, owner, Received(request, node.Interface), Hex(Vectors, "responderX25519Private"));
        Assert.NotNull(responder);
        Assert.Equal(expected, responder.Mtu);
        await LinkTests.Until(() => node.Interface.Sent.Any(packet => packet.Context == LinkContext.LrProof));
        Assert.Equal(Hex(MtuVector(expected), "proofRaw"), node.Interface.Sent.First(packet => packet.Context == LinkContext.LrProof).ToArray());
        responder.Teardown();
    }

    [Fact]
    public async Task InvalidProofDoesNotPoisonPendingHandshakeAndSignedMtuCannotBeRewritten()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
        var link = Link.CreateInitiator(node.Router.Links, identity, Hex(Vectors, "destinationHash"), 1, 30,
            Hex(Vectors, "initiatorX25519Private"), Hex(Vectors, "initiatorSigSeed"));
        var raw = Hex(MtuVector(16384), "proofRaw");
        raw[^1] ^= 1;
        Assert.False(link.ValidateProof(Received(Packet.Parse(raw), node.Interface)));
        Assert.Equal(LinkStatus.Pending, link.Status);
        raw = Hex(MtuVector(16384), "proofRaw");
        raw[Packet.Header1Size] ^= 1;
        Assert.False(link.ValidateProof(Received(Packet.Parse(raw), node.Interface)));
        Assert.Equal(LinkStatus.Pending, link.Status);
        var validButTooLarge = Packet.Parse(Hex(MtuVector(16384), "proofRaw"));
        Assert.True(Link.TryValidateProofData(link.LinkId, validButTooLarge.Payload.Span, identity, out var decodedMtu, out _));
        Assert.Equal(16384, decodedMtu);
        Assert.False(link.ValidateProof(Received(validButTooLarge, node.Interface)));
        Assert.Equal(LinkStatus.Pending, link.Status);
        Assert.True(link.ValidateProof(Received(Packet.Parse(Hex(MtuVector(500), "proofRaw")), node.Interface)));
        Assert.Equal(500, link.Mtu);
        Assert.Equal(431, link.Mdu);
        Assert.Throws<ArgumentException>(() => link.Send(new byte[500]));
        Assert.Throws<ArgumentException>(() => Link.MtuFromSignalling(new byte[2]));
        Assert.Throws<ArgumentException>(() => Link.ModeFromSignalling(new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Link.SignallingBytes(500, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(66)]
    [InlineData(68)]
    public async Task MalformedRequestLengthsAreRejected(int length)
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        var owner = new LocalDestination(identity, Destination.Create(DestinationType.Single, "mtu", [], identity));
        var request = Packet.Create(PacketType.LinkRequest, DestinationType.Single, owner.Destination.Hash, new byte[length]);
        Assert.Null(Link.ValidateRequest(node.Router.Links, owner, Received(request, node.Interface)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(95)]
    [InlineData(97)]
    [InlineData(98)]
    [InlineData(100)]
    public async Task MalformedProofLengthsLeaveTheInitiatorPending(int length)
    {
        await using var node = await Harness.Create();
        using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
        var link = Link.CreateInitiator(node.Router.Links, identity, Hex(Vectors, "destinationHash"), 1, 30,
            Hex(Vectors, "initiatorX25519Private"), Hex(Vectors, "initiatorSigSeed"));
        var proof = Packet.Create(PacketType.Proof, DestinationType.Link, link.LinkId, new byte[length], LinkContext.LrProof);
        Assert.False(link.ValidateProof(Received(proof, node.Interface)));
        Assert.Equal(LinkStatus.Pending, link.Status);
        link.Teardown();
    }

    [Fact]
    public async Task UnusableMtuUnsupportedModeAndInvalidKeysAreRejected()
    {
        await using var node = await Harness.Create(new Probe { HardwareMtu = 16384, SupportsMtuDiscovery = true });
        using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
        var owner = new LocalDestination(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["parity4", "link"], identity));
        var original = Packet.Parse(Hex(MtuVector(500), "requestRaw"));
        foreach (var bad in new[] { "mtu", "mode", "key" })
        {
            var payload = original.Payload.ToArray();
            if (bad == "mtu") Link.SignallingBytes(1, Link.ModeAes256Cbc).CopyTo(payload, 64);
            if (bad == "mode") payload[64] = (byte)((payload[64] & 0x1F) | 0x40);
            if (bad == "key") Array.Clear(payload, 0, 32);
            var request = Packet.Create(PacketType.LinkRequest, DestinationType.Single, original.DestinationHash, payload);
            Assert.Null(Link.ValidateRequest(node.Router.Links, owner, Received(request, node.Interface)));
        }
        Assert.Empty(node.Interface.Sent);
    }

    [Fact]
    public async Task ConnectHonorsCancellationViaTeardownAndFailsOwnedSendWithoutLeavingPendingLink()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        var canceled = node.Router.Links.Connect(identity, new byte[16]);
        canceled.Teardown();
        Assert.Equal(LinkStatus.Closed, canceled.Status);
        Assert.Equal(LinkTeardownReason.InitiatorClosed, canceled.TeardownReason);
        node.Interface.ThrowOnSend = true;
        var failed = node.Router.Links.Connect(identity, new byte[16]);
        await LinkTests.Until(() => failed.Status == LinkStatus.Closed);
        Assert.Equal(LinkTeardownReason.Timeout, failed.TeardownReason);
        Assert.Empty(node.Router.Links.Links);
        Assert.Throws<ArgumentOutOfRangeException>(() => node.Router.Links.Connect(identity, new byte[16], establishmentTimeout: -1));
    }

    [Fact]
    public async Task OnlyAnAuthenticatedLocalProofRebalancesLinkAndDestinationHops()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
        var destination = Destination.Create(DestinationType.Single, "dotreticulum", ["parity4", "link"], identity);
        var announce = Announce.Create(identity, destination);
        await node.Router.HandleAsync(Received(Packet.Create(PacketType.Announce, DestinationType.Single,
            destination.Hash, announce.Payload, hops: 4), node.Interface));
        Assert.Equal(5, node.Router.HopsTo(destination.Hash));
        var link = Link.CreateInitiator(node.Router.Links, identity, destination.Hash, 5, 30,
            Hex(Vectors, "initiatorX25519Private"), Hex(Vectors, "initiatorSigSeed"));
        var reference = Packet.Parse(Hex(MtuVector(500), "proofRaw"));
        var data = reference.Payload.ToArray();
        data[0] ^= 1;
        var invalid = Packet.Create(PacketType.Proof, DestinationType.Link, reference.DestinationHash, data, LinkContext.LrProof, hops: 2);
        Assert.False(link.ValidateProof(Received(invalid, node.Interface)));
        Assert.Equal(5, link.Hops);
        Assert.Equal(5, node.Router.HopsTo(destination.Hash));
        var valid = Packet.Create(PacketType.Proof, DestinationType.Link, reference.DestinationHash, reference.Payload, LinkContext.LrProof, hops: 2);
        Assert.True(link.ValidateProof(Received(valid, node.Interface)));
        Assert.Equal(3, link.Hops);
        Assert.Equal(3, node.Router.HopsTo(destination.Hash));
    }

    [Theory]
    [InlineData(16384, 16384, 16384, true, 16384)]
    [InlineData(16384, 1024, 16384, true, 1024)]
    [InlineData(16384, 16384, 1024, true, 1024)]
    [InlineData(1024, 16384, 16384, true, 1024)]
    [InlineData(16384, 16384, 16384, false, 500)]
    public async Task NegotiatedMtuHonorsMixedTransportPathBottleneck(int firstMtu, int transportReceiveMtu,
        int transportSendMtu, bool finalDiscovery, int expected)
    {
        var first = new Probe { HardwareMtu = firstMtu, SupportsMtuDiscovery = true };
        var ingress = new Probe { HardwareMtu = transportReceiveMtu, SupportsMtuDiscovery = true, Peer = first };
        first.Peer = ingress;
        var last = new Probe { HardwareMtu = 16384, SupportsMtuDiscovery = finalDiscovery };
        var egress = new Probe { HardwareMtu = transportSendMtu, SupportsMtuDiscovery = true, Peer = last };
        last.Peer = egress;
        await using var origin = await Harness.Create(first, run: true);
        await using var terminus = await Harness.Create(last, run: true);
        await using var transportManager = new PacketInterfaceManager([ingress, egress]);
        await transportManager.StartAsync();
        await using var transport = new TransportRouter(transportManager, new TransportRouterOptions { TransportEnabled = true });
        using var cancellation = new CancellationTokenSource();
        var running = transport.RunAsync(cancellation.Token);
        try
        {
            using var identity = Identity.Generate();
            var destination = new LocalDestination(identity, Destination.Create(DestinationType.Single, "parity4", ["mtupath"], identity)) { AcceptLinks = true };
            var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            destination.LinkEstablished = link => accepted.TrySetResult(link);
            terminus.Router.RegisterLocalDestination(destination);
            await terminus.Router.AnnounceAsync(destination);
            await LinkTests.Until(() => origin.Router.HasPath(destination.Destination.Hash));
            var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var initiated = origin.Router.Links.Connect(identity, destination.Destination.Hash, link => established.TrySetResult(link));
            await established.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var responded = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(expected, initiated.Mtu);
            Assert.Equal(expected, responded.Mtu);
            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            responded.PacketReceived = (data, _) => received.TrySetResult(data);
            var payload = Enumerable.Range(0, Math.Min(initiated.Mdu, 8193)).Select(i => (byte)i).ToArray();
            initiated.Send(payload, createReceipt: false);
            Assert.Equal(payload, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            if (expected > Packet.Mtu)
                Assert.Contains(first.Sent, packet => packet.DestinationType == DestinationType.Link && packet.Length > Packet.Mtu);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
        }
    }
}
