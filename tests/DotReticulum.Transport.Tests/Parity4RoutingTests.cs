using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// RNS 1.5.7, authoritative hosted-source a29bfb51c468945c96a1ee22cae0114e993e2391.
// GenerateParity4RoutingVectors.py executes source decisions and actual queue methods.
// These deterministic protocol tests are not live-node interoperability evidence.
public sealed class Parity4RoutingTests
{
    private const long T0 = 1_800_000_000;
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Parity4Routing-vectors.json"))).RootElement;
    private static Packet ReferenceAnnounce => Packet.Parse(Convert.FromHexString(Vectors.GetProperty("announceRaw").GetString()!));
    private static Identity ReferenceIdentity() => Identity.FromPrivateKey(Convert.FromHexString(Vectors.GetProperty("identityPrivate").GetString()!));
    private static Destination ReferenceDestination(Identity identity) => Destination.Create(DestinationType.Single, "parity4", ["routing"], identity);
    private static Packet Hops(Packet packet, int hops) => Packet.Create(packet.PacketType, packet.DestinationType,
        packet.DestinationHash, packet.Payload, packet.Context, packet.HeaderType,
        packet.HeaderType == HeaderType.Header2 ? packet.TransportId : default, packet.TransportType, packet.ContextFlag, (byte)hops);

    public static IEnumerable<object[]> PathCases() => Vectors.GetProperty("pathCases").EnumerateArray()
        .Select(value => new object[] { value.GetProperty("label").GetString()! });
    public static IEnumerable<object[]> ModeCases() => Vectors.GetProperty("modeCases").EnumerateArray()
        .Select(value => new object[] { value.GetProperty("origin").GetInt32(), value.GetProperty("target").GetInt32(),
            value.GetProperty("local").GetBoolean(), value.GetProperty("transmit").GetBoolean() });

    [Theory]
    [MemberData(nameof(PathCases))]
    public void PathReplacement_MatchesExecutedReference(string label)
    {
        var vector = Vectors.GetProperty("pathCases").EnumerateArray().Single(value => value.GetProperty("label").GetString() == label);
        var time = new FakeTime(T0);
        var oldInterface = new RecordingInterface();
        var newInterface = new RecordingInterface();
        var engine = new AnnounceEngine(new AnnounceEngineOptions
        {
            GravityFor = source => ReferenceEquals(source, oldInterface) ? vector.GetProperty("currentGravity").GetInt32() : vector.GetProperty("newGravity").GetInt32(),
        }, timeProvider: time);
        var first = Hops(ReferenceAnnounce, vector.GetProperty("currentHops").GetInt32() - 1);
        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(first, oldInterface).Outcome);
        engine.SetPathResponsiveness(first.DestinationHash.Span,
            vector.GetProperty("unresponsive").GetBoolean() ? PathResponsiveness.Unresponsive : PathResponsiveness.Responsive);
        if (vector.GetProperty("expired").GetBoolean()) time.Advance((long)AnnounceEngine.PathExpiry.TotalSeconds + 1);
        using var identity = ReferenceIdentity();
        var candidate = vector.GetProperty("sameBlob").GetBoolean() ? ReferenceAnnounce
            : Announce.Create(identity, ReferenceDestination(identity), unixSeconds: T0 + vector.GetProperty("emissionDelta").GetInt32());
        var result = engine.Process(Hops(candidate, vector.GetProperty("newHops").GetInt32() - 1), newInterface);
        var accepted = vector.GetProperty("accepted").GetBoolean();
        Assert.Equal(accepted ? AnnounceOutcome.Accepted : AnnounceOutcome.Duplicate, result.Outcome);
        Assert.Same(accepted ? newInterface : oldInterface, engine.GetPath(first.DestinationHash.Span)!.Interface);
    }

    [Theory]
    [MemberData(nameof(ModeCases))]
    public async Task OriginAndEgressModeMatrix_MatchesExecutedReference(int origin, int target, bool local, bool transmit)
    {
        await using var rig = await Rig.CreateAsync();
        rig.Router.SetInterfaceMode(rig.Ifaces[0], (InterfaceMode)origin);
        rig.Router.SetInterfaceMode(rig.Ifaces[1], (InterfaceMode)target);
        if (local)
        {
            using var identity = ReferenceIdentity();
            await rig.Router.AnnounceAsync(new LocalDestination(identity, ReferenceDestination(identity)));
        }
        else
        {
            await rig.Receive(0, ReferenceAnnounce);
            rig.Time.Advance(1);
            await rig.Router.FlushAnnouncesAsync();
        }
        Assert.Equal(transmit, rig.Ifaces[1].Packets.Any(packet => packet.PacketType == PacketType.Announce));
    }

    [Fact]
    public void AnnounceRateState_MatchesExecutedReference()
    {
        var time = new FakeTime(T0);
        var source = new RecordingInterface();
        var engine = new AnnounceEngine(new AnnounceEngineOptions { RateLimitFor = _ => new AnnounceRateLimit(TimeSpan.FromSeconds(10), 1, TimeSpan.FromSeconds(5)) }, timeProvider: time);
        using var identity = ReferenceIdentity();
        var index = 0;
        foreach (var expected in Vectors.GetProperty("rateCases").EnumerateArray())
        {
            time.Unix = T0 + expected.GetProperty("seconds").GetInt32();
            var packet = Announce.Create(identity, ReferenceDestination(identity), unixSeconds: T0 + index++);
            var result = engine.Process(packet, source);
            Assert.Equal(expected.GetProperty("blocked").GetBoolean() ? AnnounceOutcome.AcceptedRateLimited : AnnounceOutcome.Accepted, result.Outcome);
            var state = Assert.Single(engine.Rates);
            Assert.Equal(expected.GetProperty("last").GetDouble(), state.Last);
            Assert.Equal(expected.GetProperty("violations").GetInt32(), state.RateViolations);
            Assert.Equal(expected.GetProperty("blockedUntil").GetDouble(), state.BlockedUntil);
        }
    }

    [Theory]
    [InlineData(InterfaceMode.AccessPoint, 86400)]
    [InlineData(InterfaceMode.Roaming, 21600)]
    [InlineData(InterfaceMode.Full, 604800)]
    [InlineData(InterfaceMode.Boundary, 604800)]
    public async Task ModeLifetimeControlsReplacementExpiryAndOutboundUseRefresh(InterfaceMode mode, int lifetime)
    {
        await using var rig = await Rig.CreateAsync();
        rig.Router.SetInterfaceMode(rig.Ifaces[0], mode);
        await rig.Receive(0, ReferenceAnnounce);
        Assert.Equal(T0 + lifetime, rig.Router.Engine.GetPath(ReferenceAnnounce.DestinationHash.Span)!.ExpiresUnix);
        rig.Time.Advance(lifetime - 1);
        var data = Packet.Create(PacketType.Data, DestinationType.Single, ReferenceAnnounce.DestinationHash, new byte[] { 1 });
        await rig.Router.SendAsync(data);
        rig.Time.Advance(2);
        rig.Router.Cull();
        Assert.True(rig.Router.HasPath(ReferenceAnnounce.DestinationHash.Span));
        Assert.Equal(T0 + lifetime - 1, rig.Router.Engine.GetPath(ReferenceAnnounce.DestinationHash.Span)!.ReceivedUnix);
        using var identity = ReferenceIdentity();
        var older = Hops(Announce.Create(identity, ReferenceDestination(identity), unixSeconds: T0 - 1), 3);
        Assert.Equal(AnnounceOutcome.Accepted, rig.Router.Engine.Process(older, rig.Ifaces[0]).Outcome);
    }

    [Fact]
    public void ConcurrentPathResponsesKeepDistinctTargetsAndRestoreHeldRebroadcast()
    {
        var time = new FakeTime(T0);
        var engine = new AnnounceEngine(new AnnounceEngineOptions { TransportEnabled = true }, timeProvider: time);
        var source = new RecordingInterface();
        var targets = Enumerable.Range(0, 16).Select(_ => new RecordingInterface()).ToArray();
        engine.Process(ReferenceAnnounce, source);
        Parallel.ForEach(targets, target => Assert.True(engine.QueuePathResponse(ReferenceAnnounce.DestinationHash.Span, target, TimeSpan.Zero)));
        Assert.Equal(16, engine.PendingPathResponses);
        time.Advance(1);
        var responses = engine.CollectDueRebroadcasts();
        Assert.Equal(16, responses.Count);
        Assert.All(targets, target => Assert.Single(responses, response => ReferenceEquals(response.TargetInterface, target)));
        Assert.All(responses, response => Assert.Equal(Announce.PathResponseContext, response.Packet.Context));
        Assert.Equal(1, engine.PendingRebroadcasts);
        var restored = Assert.Single(engine.CollectDueRebroadcasts());
        Assert.Null(restored.TargetInterface);
        Assert.Equal(0, restored.Packet.Context);
    }

    [Fact]
    public void PathResponseSaturationIsBoundedAndDuplicateTargetDoesNotConsumeCapacity()
    {
        var engine = new AnnounceEngine(new AnnounceEngineOptions { TableCapacity = 2 }, timeProvider: new FakeTime(T0));
        engine.Process(ReferenceAnnounce, null);
        var first = new RecordingInterface();
        Assert.True(engine.QueuePathResponse(ReferenceAnnounce.DestinationHash.Span, first, TimeSpan.Zero));
        Assert.True(engine.QueuePathResponse(ReferenceAnnounce.DestinationHash.Span, first, TimeSpan.Zero));
        Assert.True(engine.QueuePathResponse(ReferenceAnnounce.DestinationHash.Span, new RecordingInterface(), TimeSpan.Zero));
        Assert.False(engine.QueuePathResponse(ReferenceAnnounce.DestinationHash.Span, new RecordingInterface(), TimeSpan.Zero));
        Assert.Equal(2, engine.PendingPathResponses);
        Assert.Equal(2, engine.ClearPendingRebroadcasts());
    }

    [Fact]
    public async Task ConcurrentUnknownRequestsEngageOneDiscoveryButDeliverEveryDistinctRequester()
    {
        await using var rig = await Rig.CreateAsync(4);
        foreach (var iface in rig.Ifaces) rig.Router.SetInterfaceMode(iface, InterfaceMode.Gateway);
        var destination = ReferenceAnnounce.DestinationHash.ToArray();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(index => rig.Receive(index, Request(destination, (byte)index)).AsTask()));
        Assert.Equal(1, rig.Router.PendingPathDiscoveries);
        Assert.Equal(3, rig.Ifaces.Sum(iface => iface.Packets.Count(packet => packet.DestinationHash.Span.SequenceEqual(TransportRouter.PathRequestHash))));
        foreach (var iface in rig.Ifaces) iface.Clear();
        await rig.Receive(3, ReferenceAnnounce);
        rig.Time.Advance(1);
        await rig.Router.FlushAnnouncesAsync();
        for (var index = 0; index < 3; index++)
            Assert.Single(rig.Ifaces[index].Packets, packet => packet.Context == Announce.PathResponseContext);
        Assert.Equal(0, rig.Router.PendingPathDiscoveries);
    }

    [Fact]
    public async Task SlowMediumDiscoveryUsesReferenceRoundTripAndRecursiveRequestsShareAnnounceCap()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Ifaces[1].Bitrate = 5;
        rig.Router.SetInterfaceMode(rig.Ifaces[0], InterfaceMode.Gateway);
        await rig.Receive(0, Request(ReferenceAnnounce.DestinationHash.ToArray(), 1));
        var otherDestination = Enumerable.Repeat((byte)0xAA, 16).ToArray();
        await rig.Receive(0, Request(otherDestination, 2));
        Assert.Single(rig.Ifaces[1].Packets);
        Assert.Equal(2, rig.Router.PendingPathDiscoveries);
        rig.Time.Advance(1605);
        rig.Router.Cull();
        Assert.Equal(2, rig.Router.PendingPathDiscoveries);
        rig.Time.Advance(2);
        rig.Router.Cull();
        Assert.Equal(0, rig.Router.PendingPathDiscoveries);
        Assert.Equal(1606d, Vectors.GetProperty("mediumTimeouts").EnumerateArray().Single(value => value.GetProperty("bitrate").GetInt32() == 5).GetProperty("timeout").GetDouble());
    }

    [Fact]
    public async Task LocalHandshakeFailureExpiresPathAndQueuesThrottledStandaloneRecovery()
    {
        await using var rig = await Rig.CreateAsync(transport: false);
        await rig.Receive(0, ReferenceAnnounce);
        rig.Router.NotifyLinkEstablishmentFailed(ReferenceAnnounce.DestinationHash.ToArray());
        Assert.Equal(0, rig.Router.Engine.GetPath(ReferenceAnnounce.DestinationHash.Span)!.ReceivedUnix);
        Assert.Equal(1, rig.Router.PendingPathRecoveries);
        rig.Router.NotifyLinkEstablishmentFailed(ReferenceAnnounce.DestinationHash.ToArray());
        await rig.Router.FlushPathRecoveryAsync();
        Assert.All(rig.Ifaces, iface => Assert.Single(iface.Packets));
        rig.Router.NotifyLinkEstablishmentFailed(ReferenceAnnounce.DestinationHash.ToArray());
        Assert.Equal(0, rig.Router.PendingPathRecoveries);
        rig.Time.Advance(20);
        rig.Router.NotifyLinkEstablishmentFailed(ReferenceAnnounce.DestinationHash.ToArray());
        Assert.Equal(1, rig.Router.PendingPathRecoveries);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SharedClientAndTransportLocalFailureDoNotOriginateRediscovery(bool shared, bool transport)
    {
        await using var rig = await Rig.CreateAsync(transport: transport);
        rig.Router.ConfigureSharedClient(shared);
        await rig.Receive(0, ReferenceAnnounce);
        rig.Router.NotifyLinkEstablishmentFailed(ReferenceAnnounce.DestinationHash.ToArray());
        Assert.Equal(0, rig.Router.PendingPathRecoveries);
        Assert.Equal(shared ? 0 : T0, rig.Router.Engine.GetPath(ReferenceAnnounce.DestinationHash.Span)!.ReceivedUnix);
    }

    [Theory]
    [InlineData(InterfaceMode.Full, PathResponsiveness.Unresponsive)]
    [InlineData(InterfaceMode.Boundary, PathResponsiveness.Unknown)]
    public async Task ForwardedFailureOneHopBoundaryControlsResponsivenessAndExcludesIngress(InterfaceMode mode, PathResponsiveness state)
    {
        await using var rig = await Rig.CreateAsync(3);
        rig.Router.SetInterfaceMode(rig.Ifaces[1], mode);
        await rig.Receive(0, ReferenceAnnounce);
        await rig.Receive(1, LinkRequest(rig));
        rig.Time.Advance(7);
        rig.Router.Cull();
        Assert.Equal(0, rig.Router.LinkTableCount);
        Assert.Equal(state, rig.Router.Engine.GetPathResponsiveness(ReferenceAnnounce.DestinationHash.Span));
        Assert.Equal(1, rig.Router.PendingPathRecoveries);
        foreach (var iface in rig.Ifaces) iface.Clear();
        await rig.Router.FlushPathRecoveryAsync();
        Assert.Empty(rig.Ifaces[1].Packets);
        Assert.Single(rig.Ifaces[0].Packets);
        Assert.Single(rig.Ifaces[2].Packets);
    }

    [Fact]
    public async Task AuthenticatedForwardedProofRebalancesDestinationAndWrongIngressCannotPoisonProof()
    {
        await using var rig = await Rig.CreateAsync(3);
        await rig.Receive(0, ReferenceAnnounce);
        var request = LinkRequest(rig);
        await rig.Receive(1, request);
        using var identity = ReferenceIdentity();
        using var ephemeral = Identity.Generate();
        var linkId = Link.LinkIdFromRequest(request);
        var publicKey = ephemeral.ExportPublicKey()[..32];
        byte[] signed = [.. linkId, .. publicKey, .. identity.ExportPublicKey()[32..]];
        var proof = Packet.Create(PacketType.Proof, DestinationType.Link, linkId, (byte[])[.. identity.Sign(signed), .. publicKey], LinkContext.LrProof, hops: 2);
        foreach (var iface in rig.Ifaces) iface.Clear();
        await rig.Receive(2, proof);
        Assert.Equal(1, rig.Router.HopsTo(ReferenceAnnounce.DestinationHash.Span));
        Assert.Equal(0, rig.Router.ActiveLinkTableCount);
        var invalid = proof.ToArray();
        invalid[^1] ^= 1;
        await rig.Receive(0, Packet.Parse(invalid));
        Assert.Equal(1, rig.Router.HopsTo(ReferenceAnnounce.DestinationHash.Span));
        await rig.Receive(0, proof);
        Assert.Equal(3, rig.Router.HopsTo(ReferenceAnnounce.DestinationHash.Span));
        Assert.Equal(PathResponsiveness.Unknown, rig.Router.Engine.GetPathResponsiveness(ReferenceAnnounce.DestinationHash.Span));
        Assert.Equal(1, rig.Router.ActiveLinkTableCount);
        Assert.Equal(3, Assert.Single(rig.Ifaces[1].Packets).Hops);
        var replay = Hops(proof, 3);
        await rig.Receive(0, replay);
        Assert.Equal(3, rig.Router.HopsTo(ReferenceAnnounce.DestinationHash.Span));
        Assert.Single(rig.Ifaces[1].Packets);
        rig.Router.DetachInterface(rig.Ifaces[0]);
        Assert.Equal(0, rig.Router.LinkTableCount);
        Assert.Equal(0, rig.Router.PendingPathRecoveries);
    }

    [Fact]
    public async Task ForwardedLinkRequestHonorsDiscoveredMtuAndSlowProofDeadline()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Ifaces[0].SupportsMtuDiscovery = true;
        rig.Ifaces[0].HardwareMtu = 16384;
        rig.Ifaces[1].HardwareMtu = 16384;
        rig.Ifaces[0].Bitrate = 100;
        await rig.Receive(0, ReferenceAnnounce);
        await rig.Receive(1, LinkRequest(rig, 262144));
        var forwarded = Assert.Single(rig.Ifaces[0].Packets);
        Assert.Equal(Packet.Mtu, Link.MtuFromSignalling(forwarded.Payload.Span[^3..]));
        rig.Time.Advance(7);
        rig.Router.Cull();
        Assert.Equal(1, rig.Router.LinkTableCount);
        rig.Time.Advance(40);
        rig.Router.Cull();
        Assert.Equal(0, rig.Router.LinkTableCount);
    }

    [Fact]
    public async Task BandwidthQueueReplacesNewerAtCapacityAndDoesNotBlockData()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Ifaces[1].Bitrate = 1000;
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[1], new InterfaceAnnounceOptions { AnnounceCap = .01, QueueCapacity = 1 });
        using var identity = ReferenceIdentity();
        var destination = ReferenceDestination(identity);
        await rig.Receive(0, ReferenceAnnounce);
        rig.Time.Advance(1);
        await rig.Router.FlushAnnouncesAsync();
        await rig.Receive(0, Announce.Create(identity, destination, "second"u8.ToArray(), unixSeconds: T0 + 1));
        rig.Time.Advance(1);
        await rig.Router.FlushAnnouncesAsync();
        Assert.Equal(1, rig.Router.QueuedAnnounces(rig.Ifaces[1]));
        await rig.Receive(0, Announce.Create(identity, destination, "newest"u8.ToArray(), unixSeconds: T0 + 2));
        rig.Time.Advance(1);
        await rig.Router.FlushAnnouncesAsync();
        Assert.Equal(1, rig.Router.QueuedAnnounces(rig.Ifaces[1]));
        var data = Packet.Create(PacketType.Data, DestinationType.Plain, new byte[16], "unblocked"u8.ToArray());
        await rig.Router.SendAsync(data);
        Assert.Contains(rig.Ifaces[1].Packets, packet => packet.PacketType == PacketType.Data);
        rig.Router.Engine.ClearPendingRebroadcasts();
        rig.Time.Advance(200);
        await rig.Router.FlushAnnouncesAsync();
        Assert.Equal(0, rig.Router.QueuedAnnounces(rig.Ifaces[1]));
        Assert.True(Announce.TryParse(rig.Ifaces[1].Packets.Last(packet => packet.PacketType == PacketType.Announce), out var newest));
        Assert.Equal("newest"u8.ToArray(), newest.AppData);
    }

    [Fact]
    public async Task IngressHoldIsBoundedReplacesNewerAndReleasesWithoutSubscriber()
    {
        await using var rig = await Rig.CreateAsync();
        var limited = true;
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[0], new InterfaceAnnounceOptions
        { HeldAnnounceCapacity = 1, IngressLimitAnnounces = () => limited });
        await rig.Receive(0, ReferenceAnnounce);
        using var identity = ReferenceIdentity();
        await rig.Receive(0, Announce.Create(identity, ReferenceDestination(identity), "updated"u8.ToArray(), unixSeconds: T0 + 1));
        using var other = Identity.Generate();
        await rig.Receive(0, Announce.Create(other, Destination.Create(DestinationType.Single, "other", [], other), unixSeconds: T0));
        Assert.Equal(1, rig.Router.HeldIngressAnnounces(rig.Ifaces[0]));
        Assert.False(rig.Router.HasPath(ReferenceAnnounce.DestinationHash.Span));
        limited = false;
        await rig.Router.FlushAnnouncesAsync();
        Assert.Equal(0, rig.Router.HeldIngressAnnounces(rig.Ifaces[0]));
        Assert.True(rig.Router.HasPath(ReferenceAnnounce.DestinationHash.Span));
        Assert.Equal("updated"u8.ToArray(), rig.Router.Engine.Known.RecallAppData(ReferenceAnnounce.DestinationHash.Span));
    }

    [Fact]
    public async Task RequestedUnknownAnnounceBypassesIngressHoldAndPerInterfaceRatePolicyApplies()
    {
        await using var rig = await Rig.CreateAsync();
        var source = rig.Ifaces[0];
        rig.Router.SetInterfaceAnnounceOptions(source, new InterfaceAnnounceOptions
        { IngressLimitAnnounces = () => true, RateLimit = new AnnounceRateLimit(TimeSpan.FromSeconds(30), 0, TimeSpan.FromSeconds(5)) });
        await rig.Router.RequestPathAsync(ReferenceAnnounce.DestinationHash.Span);
        await rig.Receive(0, ReferenceAnnounce);
        Assert.True(rig.Router.HasPath(ReferenceAnnounce.DestinationHash.Span));
        using var identity = ReferenceIdentity();
        var next = Announce.Create(identity, ReferenceDestination(identity), unixSeconds: T0 + 1);
        Assert.Equal(AnnounceOutcome.AcceptedRateLimited, rig.Router.Engine.Process(next, source).Outcome);
        var noRate = Announce.Create(identity, ReferenceDestination(identity), unixSeconds: T0 + 2);
        Assert.Equal(AnnounceOutcome.Accepted, rig.Router.Engine.Process(noRate, rig.Ifaces[1]).Outcome);
    }

    [Fact]
    public async Task CancellationRetainsQueuedRecoveryAndResponseForRetry()
    {
        await using var rig = await Rig.CreateAsync(transport: false);
        await rig.Receive(0, ReferenceAnnounce);
        rig.Router.NotifyLinkEstablishmentFailed(ReferenceAnnounce.DestinationHash.ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Router.FlushPathRecoveryAsync(cancellation.Token).AsTask());
        Assert.Equal(1, rig.Router.PendingPathRecoveries);
        Assert.True(rig.Router.Engine.QueuePathResponse(ReferenceAnnounce.DestinationHash.Span, rig.Ifaces[1], TimeSpan.Zero));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Router.FlushAnnouncesAsync(cancellation.Token).AsTask());
        Assert.Equal(1, rig.Router.Engine.PendingPathResponses);
        await rig.Router.FlushPathRecoveryAsync();
        Assert.Equal(0, rig.Router.PendingPathRecoveries);
    }

    [Fact]
    public async Task InboundSaturationHasIndependentBoundsAndReferencePriorityOrder()
    {
        await using var rig = await Rig.CreateAsync(options: time => new TransportRouterOptions
        {
            TimeProvider = time, InboundDataCapacity = 2, InboundAnnounceCapacity = 1,
            InboundPathRequestCapacity = 1, InboundIngressLimitedCapacity = 1,
        });
        using var identity = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "priority", [], identity);
        var limitedDestination = Destination.Create(DestinationType.Single, "priority", ["limited"], identity);
        var order = new List<string>();
        rig.Router.RegisterLocalDestination(new LocalDestination(identity, destination)
        { PacketReceived = received => { order.Add(System.Text.Encoding.UTF8.GetString(received.Packet.Payload.Span)); return ValueTask.CompletedTask; } });
        rig.Router.RegisterLocalDestination(new LocalDestination(identity, limitedDestination));
        rig.Router.Engine.RegisterHandler(new CallbackAnnounceHandler(null, _ => order.Add("announce")));
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[1], new InterfaceAnnounceOptions { IngressLimitPathRequests = () => true });
        rig.Ifaces[0].OnSend = packet => { if (packet.Context == Announce.PathResponseContext) order.Add("request"); };
        rig.Ifaces[1].OnSend = packet => { if (packet.Context == Announce.PathResponseContext) order.Add("limited"); };
        var packets = new[]
        {
            Received(rig.Ifaces[1], Request(limitedDestination.Hash, 1)),
            Received(rig.Ifaces[0], Request(destination.Hash, 2)),
            Received(rig.Ifaces[0], ReferenceAnnounce),
            Received(rig.Ifaces[0], Packet.Create(PacketType.Data, DestinationType.Single, destination.Hash, "data-a"u8.ToArray())),
            Received(rig.Ifaces[0], Packet.Create(PacketType.Data, DestinationType.Single, destination.Hash, "data-b"u8.ToArray())),
        };
        foreach (var packet in packets) Assert.True(rig.Router.TryQueueInbound(packet));
        foreach (var index in new[] { 4, 2 }) Assert.False(rig.Router.TryQueueInbound(packets[index]));
        var overflowDestination = Enumerable.Repeat((byte)9, 16).ToArray();
        Assert.False(rig.Router.TryQueueInbound(Received(rig.Ifaces[0], Request(overflowDestination, 3))));
        Assert.False(rig.Router.TryQueueInbound(Received(rig.Ifaces[1], Request(overflowDestination, 4))));
        var statistics = rig.Router.InboundQueues;
        Assert.Equal(Vectors.GetProperty("inboundHeights").EnumerateArray().Select(value => value.GetInt32()), statistics.Heights);
        Assert.Equal(Vectors.GetProperty("inboundDropped").EnumerateArray().Select(value => value.GetInt64()), statistics.Dropped);
        await rig.Router.ProcessQueuedInboundAsync();
        Assert.Equal(Vectors.GetProperty("inboundOrder").EnumerateArray().Select(value => value.GetString()!), order);
        Assert.Equal(0, rig.Router.InboundQueues.Total);
    }

    [Fact]
    public async Task QueueCancellationDoesNotDiscardPendingPacketAndObserversAreIsolated()
    {
        await using var rig = await Rig.CreateAsync();
        using var identity = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "callbacks", [], identity);
        var errors = new List<string>();
        var delivered = 0;
        rig.Router.Log = errors.Add;
        var local = new LocalDestination(identity, destination);
        local.PacketReceived += _ => throw new InvalidOperationException("observer failure");
        local.PacketReceived += _ => { delivered++; return ValueTask.CompletedTask; };
        rig.Router.RegisterLocalDestination(local);
        Assert.True(rig.Router.TryQueueInbound(Received(rig.Ifaces[0], Packet.Create(PacketType.Data, DestinationType.Single, destination.Hash, new byte[] { 1 }))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Router.ProcessQueuedInboundAsync(cancellation.Token).AsTask());
        Assert.Equal(1, rig.Router.InboundQueues.Total);
        await rig.Router.ProcessQueuedInboundAsync();
        Assert.Equal(1, delivered);
        Assert.Contains(errors, error => error.Contains("observer failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EarlyInflightBatchingDoesNotExhaustRequestQueueAndDeliversAllTargets()
    {
        await using var rig = await Rig.CreateAsync(4, options: time => new TransportRouterOptions
        { TimeProvider = time, TransportEnabled = true, InboundPathRequestCapacity = 1, PathRequestGrace = TimeSpan.Zero });
        foreach (var iface in rig.Ifaces) rig.Router.SetInterfaceMode(iface, InterfaceMode.Gateway);
        for (var index = 0; index < 3; index++)
            Assert.True(rig.Router.TryQueueInbound(Received(rig.Ifaces[index], Request(ReferenceAnnounce.DestinationHash.ToArray(), (byte)index))));
        Assert.Equal(1, rig.Router.InflightPathRequests);
        Assert.Equal(1, rig.Router.InboundQueues.Total);
        await rig.Router.ProcessQueuedInboundAsync();
        Assert.Equal(0, rig.Router.InflightPathRequests);
        Assert.Equal(1, rig.Router.PendingPathDiscoveries);
        await rig.Receive(3, ReferenceAnnounce);
        rig.Time.Advance(1);
        await rig.Router.FlushAnnouncesAsync();
        for (var index = 0; index < 3; index++)
            Assert.Single(rig.Ifaces[index].Packets, packet => packet.Context == Announce.PathResponseContext);
    }

    [Theory]
    [InlineData(InterfaceMode.Internal, InterfaceMode.Full, false, false)]
    [InlineData(InterfaceMode.Boundary, InterfaceMode.Internal, true, true)]
    [InlineData(InterfaceMode.Boundary, InterfaceMode.Internal, false, false)]
    public async Task ExplicitInternalOriginFlagsOverrideOnlyTheirOwnBoundaryRule(InterfaceMode origin, InterfaceMode target, bool enabled, bool expected)
    {
        await using var rig = await Rig.CreateAsync();
        rig.Router.SetInterfaceMode(rig.Ifaces[0], origin);
        rig.Router.SetInterfaceMode(rig.Ifaces[1], target);
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[0], new InterfaceAnnounceOptions { AnnouncesToInternal = enabled });
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[1], new InterfaceAnnounceOptions { AnnouncesFromInternal = enabled });
        await rig.Receive(0, ReferenceAnnounce);
        rig.Time.Advance(1);
        await rig.Router.FlushAnnouncesAsync();
        Assert.Equal(expected, rig.Ifaces[1].Packets.Any(packet => packet.PacketType == PacketType.Announce));
    }

    [Fact]
    public async Task StaleBandwidthQueueExpiresAndExplicitPathResponseBypassesModeAndCap()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Ifaces[1].Bitrate = 1000;
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[1], new InterfaceAnnounceOptions
        { AnnounceCap = .01, QueueLifetime = TimeSpan.FromSeconds(5) });
        await rig.Receive(0, ReferenceAnnounce);
        rig.Time.Advance(1);
        await rig.Router.FlushAnnouncesAsync();
        using var identity = ReferenceIdentity();
        await rig.Receive(0, Announce.Create(identity, ReferenceDestination(identity), unixSeconds: T0 + 1));
        rig.Time.Advance(1);
        await rig.Router.FlushAnnouncesAsync();
        Assert.Equal(1, rig.Router.QueuedAnnounces(rig.Ifaces[1]));
        rig.Router.Engine.ClearPendingRebroadcasts();
        rig.Time.Advance(6);
        await rig.Router.FlushAnnouncesAsync();
        Assert.Equal(0, rig.Router.QueuedAnnounces(rig.Ifaces[1]));
        rig.Router.SetInterfaceMode(rig.Ifaces[1], InterfaceMode.AccessPoint);
        rig.Ifaces[1].Clear();
        await rig.Receive(1, Request(ReferenceAnnounce.DestinationHash.ToArray(), 42));
        rig.Time.Advance(1);
        await rig.Router.FlushAnnouncesAsync();
        Assert.Equal(Announce.PathResponseContext, Assert.Single(rig.Ifaces[1].Packets).Context);
    }

    [Fact]
    public async Task AutomaticProofRunsWithoutDataObserver()
    {
        await using var rig = await Rig.CreateAsync();
        using var identity = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "proven", [], identity);
        rig.Router.RegisterLocalDestination(new LocalDestination(identity, destination) { ProofStrategy = ProofStrategy.All });
        var packet = Packet.Create(PacketType.Data, DestinationType.Single, destination.Hash, identity.Encrypt(new byte[] { 1 }));
        await rig.Receive(0, packet);
        var proof = Assert.Single(rig.Ifaces[0].Packets);
        Assert.Equal(PacketType.Proof, proof.PacketType);
        Assert.Equal(packet.CalculateTruncatedHash(), proof.DestinationHash.ToArray());
    }

    [Fact]
    public async Task LongTransitFailureDoesNotRediscoverUntilPathIsMissing()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Receive(0, Hops(ReferenceAnnounce, 2));
        await rig.Receive(1, Hops(LinkRequest(rig), 2));
        rig.Time.Advance(19);
        rig.Router.Cull();
        Assert.Equal(0, rig.Router.PendingPathRecoveries);
        Assert.Equal(PathResponsiveness.Unknown, rig.Router.Engine.GetPathResponsiveness(ReferenceAnnounce.DestinationHash.Span));
        await rig.Receive(1, Hops(LinkRequest(rig), 2));
        rig.Router.Engine.RemovePath(ReferenceAnnounce.DestinationHash.Span);
        rig.Time.Advance(19);
        rig.Router.Cull();
        Assert.Equal(1, rig.Router.PendingPathRecoveries);
    }

    [Fact]
    public async Task DiscoveryAndForwardedLinkTablesRejectAtTheirIndependentBound()
    {
        await using var rig = await Rig.CreateAsync(options: time => new TransportRouterOptions
        { TimeProvider = time, TransportEnabled = true, ControlCapacity = 1 });
        rig.Router.SetInterfaceMode(rig.Ifaces[1], InterfaceMode.Gateway);
        await rig.Receive(0, ReferenceAnnounce);
        await rig.Receive(1, LinkRequest(rig));
        await rig.Receive(1, LinkRequest(rig));
        Assert.Equal(1, rig.Router.LinkTableCount);
        Assert.Single(rig.Ifaces[0].Packets);
        await rig.Receive(1, Request(Enumerable.Repeat((byte)1, 16).ToArray(), 1));
        await rig.Receive(1, Request(Enumerable.Repeat((byte)2, 16).ToArray(), 2));
        Assert.Equal(1, rig.Router.PendingPathDiscoveries);
    }

    [Fact]
    public async Task RecursiveIngressAndEgressLimitsDoNotSuppressLocalAnswersOrExplicitRequests()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Router.SetInterfaceMode(rig.Ifaces[0], InterfaceMode.Gateway);
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[0], new InterfaceAnnounceOptions { IngressLimitPathRequests = () => true });
        await rig.Receive(0, Request(ReferenceAnnounce.DestinationHash.ToArray(), 1));
        Assert.Empty(rig.Ifaces[1].Packets);
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[0], new InterfaceAnnounceOptions());
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[1], new InterfaceAnnounceOptions { EgressLimitPathRequests = () => true });
        await rig.Receive(0, Request(ReferenceAnnounce.DestinationHash.ToArray(), 2));
        Assert.Empty(rig.Ifaces[1].Packets);
        await rig.Router.RequestPathAsync(ReferenceAnnounce.DestinationHash.Span, rig.Ifaces[1]);
        Assert.Single(rig.Ifaces[1].Packets);
        using var identity = ReferenceIdentity();
        rig.Router.RegisterLocalDestination(new LocalDestination(identity, ReferenceDestination(identity)));
        rig.Router.SetInterfaceAnnounceOptions(rig.Ifaces[0], new InterfaceAnnounceOptions { IngressLimitPathRequests = () => true });
        await rig.Receive(0, Request(ReferenceAnnounce.DestinationHash.ToArray(), 3));
        Assert.Equal(Announce.PathResponseContext, Assert.Single(rig.Ifaces[0].Packets).Context);
    }

    [Fact]
    public async Task NontransportSharedOwnerRetainsPhysicalRequesterWhileSearchingOnlyLocalClients()
    {
        await using var rig = await Rig.CreateAsync(3, transport: false);
        rig.Router.RegisterLocalClientInterface(rig.Ifaces[0]);
        rig.Router.RegisterLocalClientInterface(rig.Ifaces[1]);
        await rig.Receive(2, Request(ReferenceAnnounce.DestinationHash.ToArray(), 33));
        Assert.Equal(1, rig.Router.PendingPathDiscoveries);
        Assert.Single(rig.Ifaces[0].Packets);
        Assert.Single(rig.Ifaces[1].Packets);
        Assert.Empty(rig.Ifaces[2].Packets);
        var response = Packet.Create(PacketType.Announce, DestinationType.Single, ReferenceAnnounce.DestinationHash,
            ReferenceAnnounce.Payload, Announce.PathResponseContext);
        await rig.Receive(1, response);
        rig.Time.Advance(1);
        await rig.Router.FlushAnnouncesAsync();
        var outbound = Assert.Single(rig.Ifaces[2].Packets);
        Assert.Equal(Announce.PathResponseContext, outbound.Context);
        Assert.Equal(0, outbound.Hops);
        Assert.Equal(0, rig.Router.PendingPathDiscoveries);
    }

    private static Packet Request(byte[] destination, byte tag) => Packet.Create(PacketType.Data, DestinationType.Plain,
        TransportRouter.PathRequestHash, (byte[])[.. destination, .. Enumerable.Repeat(tag, 16)]);
    private static Packet LinkRequest(Rig rig, int mtu = 500)
    {
        using var initiator = Identity.Generate();
        return Packet.Create(PacketType.LinkRequest, DestinationType.Single, ReferenceAnnounce.DestinationHash,
            (byte[])[.. initiator.ExportPublicKey(), .. Link.SignallingBytes(mtu, Link.ModeAes256Cbc)], headerType: HeaderType.Header2,
            transportId: rig.Router.TransportId, transportType: TransportType.Transport);
    }
    private static ReceivedPacket Received(IPacketInterface source, Packet packet) => new(packet.ToArray(), packet, source);

    private sealed class RecordingInterface : IPacketInterface
    {
        private readonly List<byte[]> _sent = [];
        public long Bitrate { get; set; }
        public int HardwareMtu { get; set; } = Packet.Mtu;
        public bool SupportsMtuDiscovery { get; set; }
        public Action<Packet>? OnSend { get; set; }
        public IReadOnlyList<Packet> Packets { get { lock (_sent) return _sent.Select(raw => Packet.Parse(raw)).ToArray(); } }
        public void Clear() { lock (_sent) _sent.Clear(); }
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sent) _sent.Add(packet.ToArray());
            OnSend?.Invoke(Packet.Parse(packet));
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
        public FakeTime Time { get; } = new(T0);
        public required RecordingInterface[] Ifaces { get; init; }
        public required PacketInterfaceManager Manager { get; init; }
        public TransportRouter Router { get; private set; } = null!;
        public static async Task<Rig> CreateAsync(int count = 2, bool transport = true, Func<FakeTime, TransportRouterOptions>? options = null)
        {
            var ifaces = Enumerable.Range(0, count).Select(_ => new RecordingInterface()).ToArray();
            var rig = new Rig { Ifaces = ifaces, Manager = new PacketInterfaceManager(ifaces) };
            await rig.Manager.StartAsync();
            rig.Router = new TransportRouter(rig.Manager, options?.Invoke(rig.Time)
                ?? new TransportRouterOptions { TransportEnabled = transport, TimeProvider = rig.Time, PathRequestGrace = TimeSpan.Zero });
            return rig;
        }
        public ValueTask Receive(int index, Packet packet) => Router.HandleAsync(Received(Ifaces[index], packet));
        public async ValueTask DisposeAsync()
        {
            await Router.DisposeAsync();
            await Manager.DisposeAsync();
        }
    }
}
