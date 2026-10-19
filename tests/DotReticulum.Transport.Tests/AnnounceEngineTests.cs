using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// Upstream vectors: ../DotReticulum.Core.Tests/Fixtures/announce-vectors.json (EXECUTED upstream Reticulum
// e40191b3d193b46b7f2d8a44424a594cd758839b, see GenerateAnnounceVectors.py there). Engine behaviour tests follow upstream
// Transport.inbound / Transport.jobs announce handling (RNS/Transport.py, same revision).
public sealed class FakeTime(long unix) : TimeProvider
{
    public long Unix { get; set; } = unix;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Unix);
    public void Advance(long seconds) => Unix += seconds;
}

public sealed class NullInterface : IPacketInterface
{
    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class AnnounceEngineTests
{
    private const long T0 = 1_800_000_000;

    private static JsonElement Root { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "announce-vectors.json"))).RootElement;

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    private static byte[]? HexOrNull(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Convert.FromHexString(e.GetProperty(name).GetString()!);

    public static IEnumerable<object[]> Labels() =>
        Root.GetProperty("announces").EnumerateArray().Select(a => new object[] { a.GetProperty("label").GetString()! });

    private static JsonElement Vector(string label) =>
        Root.GetProperty("announces").EnumerateArray().Single(a => a.GetProperty("label").GetString() == label);

    private sealed record Local(Identity Identity, Destination Destination)
    {
        public Packet Announce(long t, byte[]? appData = null, DestinationRatchets? ratchets = null, bool pathResponse = false) =>
            Core.Announce.Create(Identity, Destination, appData, ratchets, pathResponse, t);
    }

    private static Local NewLocal(string aspect = "engine")
    {
        var identity = Identity.Generate();
        return new Local(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["tests", aspect], identity));
    }

    private static AnnounceEngine NewEngine(FakeTime time, AnnounceEngineOptions? options = null) => new(options, timeProvider: time);

    [Theory]
    [MemberData(nameof(Labels))]
    public void UpstreamAnnounce_IsAccepted_RecallsIdentityAppDataAndRatchet(string label)
    {
        var v = Vector(label);
        var engine = NewEngine(new FakeTime(T0));
        var destinationHash = Hex(v, "destination_hash");
        var result = engine.Process(Packet.Parse(Hex(v, "raw")), null);

        Assert.Equal(AnnounceOutcome.Accepted, result.Outcome);
        Assert.Equal(1, result.Hops);
        using var recalled = engine.Known.Recall(destinationHash);
        Assert.NotNull(recalled);
        Assert.Equal(Hex(v, "identity_public"), recalled.ExportPublicKey());
        using var byIdentityHash = engine.Known.Recall(recalled.Hash, fromIdentityHash: true);
        Assert.NotNull(byIdentityHash);
        Assert.Equal(HexOrNull(v, "upstream_recalled_app_data"), engine.Known.RecallAppData(destinationHash));
        Assert.Equal(HexOrNull(v, "ratchet"), engine.Known.GetRatchet(destinationHash));

        // Encrypt to the recalled destination (via its ratchet if announced); the holder of the upstream keys decrypts.
        var token = engine.Known.Encrypt(destinationHash, "hello from dotnet"u8)!;
        using var owner = Identity.FromPrivateKey(Hex(v, "identity_private"));
        var ratchetPrivate = HexOrNull(v, "ratchet_private");
        var plaintext = owner.Decrypt(token, ratchetPrivate is null ? null : [ratchetPrivate], enforceRatchets: ratchetPrivate is not null);
        Assert.Equal("hello from dotnet"u8.ToArray(), plaintext);
    }

    [Fact]
    public void TamperedUpstreamAnnounces_AreRejected_AndNothingIsRemembered()
    {
        var engine = NewEngine(new FakeTime(T0));
        var tried = 0;
        foreach (var t in Root.GetProperty("tampered").EnumerateArray())
        {
            if (!Packet.TryParse(Hex(t, "raw"), out var packet)) continue;
            tried++;
            var result = engine.Process(packet, null);
            Assert.NotEqual(AnnounceOutcome.Accepted, result.Outcome);
            Assert.Equal(AnnounceOutcome.Invalid, result.Outcome);
        }

        Assert.True(tried > 50);
        Assert.Equal(0, engine.Known.Count);
        Assert.Null(engine.GetPath(new byte[16]));
    }

    [Fact]
    public void DuplicateRandomHash_IsNotReprocessed_ButNewerEmissionIs()
    {
        var time = new FakeTime(T0);
        var engine = NewEngine(time);
        var calls = 0;
        engine.RegisterHandler(new CallbackAnnounceHandler(null, _ => calls++));
        var local = NewLocal();
        var first = local.Announce(T0);

        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(first, null).Outcome);
        Assert.Equal(AnnounceOutcome.Duplicate, engine.Process(Packet.Parse(first.ToArray()), null).Outcome);
        Assert.Equal(1, calls);
        // Same emission second but a different random hash: not newer than what we have.
        Assert.Equal(AnnounceOutcome.Duplicate, engine.Process(local.Announce(T0), null).Outcome);
        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(local.Announce(T0 + 5), null).Outcome);
        Assert.Equal(2, calls);
        // An older emission arriving later (e.g. via a longer path) is ignored.
        Assert.Equal(AnnounceOutcome.Duplicate, engine.Process(local.Announce(T0 - 100), null).Outcome);
    }

    [Fact]
    public void LongerPath_IsIgnoredUnlessMoreRecentOrExpired()
    {
        var time = new FakeTime(T0);
        var engine = NewEngine(time);
        var local = NewLocal();
        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(local.Announce(T0), null).Outcome);

        var farOld = HopsAdjusted(local.Announce(T0 - 10), 3);
        Assert.Equal(AnnounceOutcome.Duplicate, engine.Process(farOld, null).Outcome);
        var farNew = HopsAdjusted(local.Announce(T0 + 10), 3);
        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(farNew, null).Outcome);
        Assert.Equal(4, engine.GetPath(local.Destination.Hash)!.Hops);

        // After the path expired any announce not heard before replaces it.
        time.Advance((long)AnnounceEngine.PathExpiry.TotalSeconds + 1);
        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(HopsAdjusted(local.Announce(T0 - 50), 5), null).Outcome);
    }

    private static Packet HopsAdjusted(Packet p, byte hops) =>
        Packet.Create(p.PacketType, p.DestinationType, p.DestinationHash, p.Payload, p.Context, contextFlag: p.ContextFlag, hops: hops);

    [Fact]
    public void LocalDestinations_AreNotAddedButStillValidated()
    {
        var engine = NewEngine(new FakeTime(T0));
        var local = NewLocal();
        engine.RegisterLocalDestination(local.Destination.Hash);
        Assert.Equal(AnnounceOutcome.Local, engine.Process(local.Announce(T0), null).Outcome);
        Assert.Null(engine.GetPath(local.Destination.Hash));
        engine.DeregisterLocalDestination(local.Destination.Hash);
        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(local.Announce(T0 + 1), null).Outcome);
    }

    [Fact]
    public void KnownDestinationWithADifferentPublicKey_IsRejected()
    {
        var engine = NewEngine(new FakeTime(T0));
        var local = NewLocal();
        using var other = Identity.Generate();
        engine.Known.Remember(local.Destination.Hash, new byte[32], other.ExportPublicKey(), null);
        Assert.Equal(AnnounceOutcome.KeyConflict, engine.Process(local.Announce(T0), null).Outcome);
        Assert.Equal(other.ExportPublicKey(), engine.Known.GetPublicKey(local.Destination.Hash));
    }

    [Fact]
    public void NonAnnounceAndNonSinglePackets_AreRefused()
    {
        var engine = NewEngine(new FakeTime(T0));
        Assert.Equal(AnnounceOutcome.NotAnnounce, engine.Process(Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], new byte[] { 1 }), null).Outcome);
        Assert.Equal(AnnounceOutcome.WrongDestinationType,
            engine.Process(Packet.Create(PacketType.Announce, DestinationType.Plain, new byte[16], new byte[200]), null).Outcome);
        Assert.Equal(AnnounceOutcome.Invalid,
            engine.Process(Packet.Create(PacketType.Announce, DestinationType.Single, new byte[16], new byte[10]), null).Outcome);
    }

    [Fact]
    public void Handlers_FilterByAspect_AndPathResponsesOnlyWhenRequested()
    {
        var engine = NewEngine(new FakeTime(T0));
        var local = NewLocal("handlers");
        var all = new List<AnnounceReceived>();
        var matching = new List<AnnounceReceived>();
        var other = new List<AnnounceReceived>();
        var withPathResponses = new List<AnnounceReceived>();
        engine.RegisterHandler(new CallbackAnnounceHandler(null, all.Add));
        engine.RegisterHandler(new CallbackAnnounceHandler("dotreticulum.tests.handlers", matching.Add));
        engine.RegisterHandler(new CallbackAnnounceHandler("dotreticulum.tests.nope", other.Add));
        engine.RegisterHandler(new CallbackAnnounceHandler(null, withPathResponses.Add, receivePathResponses: true));
        engine.RegisterHandler(new CallbackAnnounceHandler(null, _ => throw new InvalidOperationException("faulty handler")));
        var removedCalls = 0;
        var removed = new CallbackAnnounceHandler(null, _ => removedCalls++);
        engine.RegisterHandler(removed);
        engine.DeregisterHandler(removed);

        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(local.Announce(T0, "data"u8.ToArray()), null).Outcome);
        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(local.Announce(T0 + 1, pathResponse: true), null).Outcome);

        Assert.Single(all);
        Assert.Single(matching);
        Assert.Empty(other);
        Assert.Equal(2, withPathResponses.Count);
        Assert.Equal(0, removedCalls);
        Assert.Equal(local.Destination.Hash, matching[0].DestinationHash);
        Assert.Equal(local.Identity.ExportPublicKey(), matching[0].Identity.ExportPublicKey());
        Assert.Equal("data"u8.ToArray(), matching[0].AppData);
        Assert.False(matching[0].IsPathResponse);
        Assert.True(withPathResponses[1].IsPathResponse);
        Assert.Equal(1, matching[0].Hops);
    }

    [Fact]
    public void FaultingHandler_IsReported_AndDoesNotPreventOtherHandlersOrRecall()
    {
        var engine = NewEngine(new FakeTime(T0));
        var local = NewLocal("fault");
        var errors = new List<string>();
        var deliveries = new List<AnnounceReceived>();
        engine.Log = errors.Add;
        engine.RegisterHandler(new CallbackAnnounceHandler(null, _ => throw new InvalidOperationException("observer failed")));
        engine.RegisterHandler(new CallbackAnnounceHandler(null, deliveries.Add));

        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(local.Announce(T0), null).Outcome);

        Assert.Single(deliveries);
        Assert.NotNull(engine.Known.Recall(local.Destination.Hash));
        var error = Assert.Single(errors);
        Assert.Contains("observer failed", error);
        Assert.Contains(Convert.ToHexStringLower(local.Destination.Hash), error);
    }

    [Fact]
    public void RateLimit_BlocksRebroadcastAfterGraceViolations_AndRecoversAfterThePenalty()
    {
        var time = new FakeTime(T0);
        var engine = NewEngine(time, new AnnounceEngineOptions
        {
            TransportEnabled = true,
            RateLimit = new AnnounceRateLimit(TimeSpan.FromSeconds(10), Grace: 1, Penalty: TimeSpan.FromSeconds(60)),
        });
        var local = NewLocal();

        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(local.Announce(T0), null).Outcome);
        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(local.Announce(T0 + 1), null).Outcome);
        Assert.Equal(AnnounceOutcome.AcceptedRateLimited, engine.Process(local.Announce(T0 + 2), null).Outcome);
        Assert.Equal(AnnounceOutcome.AcceptedRateLimited, engine.Process(local.Announce(T0 + 3), null).Outcome);

        // Identity and path are still updated while blocked, only the rebroadcast is withheld.
        Assert.NotNull(engine.GetPath(local.Destination.Hash));

        time.Advance(71);
        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(local.Announce(T0 + 80), null).Outcome);

        // Path responses are exempt from rate limiting.
        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(local.Announce(T0 + 81, pathResponse: true), null).Outcome);
    }

    [Fact]
    public void Rebroadcast_IsQueuedOnlyWhenTransportEnabled_WithHopsPlusOneAndNoEchoToIngress()
    {
        var time = new FakeTime(T0);
        var ingress = new NullInterface();
        var local = NewLocal();
        var disabled = NewEngine(time);
        disabled.Process(local.Announce(T0), ingress);
        Assert.Equal(0, disabled.PendingRebroadcasts);

        var transportId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var ratchets = new DestinationRatchets();
        var engine = NewEngine(time, new AnnounceEngineOptions { TransportEnabled = true, TransportId = transportId });
        var announce = local.Announce(T0, "x"u8.ToArray(), ratchets);
        Assert.Equal(AnnounceOutcome.Accepted, engine.Process(HopsAdjusted2(announce, 2), ingress).Outcome);
        Assert.Equal(1, engine.PendingRebroadcasts);
        Assert.Empty(engine.CollectDueRebroadcasts());

        time.Advance(1);
        var first = Assert.Single(engine.CollectDueRebroadcasts());
        Assert.Same(ingress, first.ExcludedInterface);
        Assert.Equal(3, first.Packet.Hops);
        Assert.Equal(HeaderType.Header2, first.Packet.HeaderType);
        Assert.Equal(TransportType.Transport, first.Packet.TransportType);
        Assert.Equal(transportId, first.Packet.TransportId.ToArray());
        Assert.True(first.Packet.ContextFlag);
        Assert.Equal(announce.Payload.ToArray(), first.Packet.Payload.ToArray());
        Assert.Equal(AnnounceStatus.Valid, Announce.Validate(Packet.Parse(first.Packet.ToArray())).Status);

        Assert.Empty(engine.CollectDueRebroadcasts());
        time.Advance(6);
        Assert.Single(engine.CollectDueRebroadcasts());
        time.Advance(6);
        Assert.Empty(engine.CollectDueRebroadcasts());
        Assert.Equal(0, engine.PendingRebroadcasts);
    }

    private static Packet HopsAdjusted2(Packet p, byte hops) => HopsAdjusted(p, hops);

    [Fact]
    public void Rebroadcast_WithoutTransportId_IsHeader1()
    {
        var time = new FakeTime(T0);
        var engine = NewEngine(time, new AnnounceEngineOptions { TransportEnabled = true });
        engine.Process(NewLocal().Announce(T0), null);
        time.Advance(1);
        var due = Assert.Single(engine.CollectDueRebroadcasts());
        Assert.Equal(HeaderType.Header1, due.Packet.HeaderType);
        Assert.Equal(1, due.Packet.Hops);
        Assert.Null(due.ExcludedInterface);
    }

    [Fact]
    public void HearingANeighbourRebroadcast_CancelsOurPendingRetransmission()
    {
        var time = new FakeTime(T0);
        var transportId = new byte[16];
        var local = NewLocal();
        var announce = local.Announce(T0);

        // Passed on by another node one hop further: no more tries needed once we have retried and are still within the timeout.
        var engine = NewEngine(time, new AnnounceEngineOptions { TransportEnabled = true });
        engine.Process(announce, null);
        time.Advance(1);
        Assert.Single(engine.CollectDueRebroadcasts());
        Assert.Equal(AnnounceOutcome.Duplicate, engine.Process(Header2Copy(announce, transportId, hops: 1 + 1), null).Outcome);
        Assert.Equal(0, engine.PendingRebroadcasts);

        // Two same-level neighbour rebroadcasts after our retry also complete processing.
        var engine2 = NewEngine(time, new AnnounceEngineOptions { TransportEnabled = true });
        engine2.Process(announce, null);
        time.Advance(1);
        Assert.Single(engine2.CollectDueRebroadcasts());
        engine2.Process(Header2Copy(announce, transportId, hops: 1), null);
        Assert.Equal(1, engine2.PendingRebroadcasts);
        engine2.Process(Header2Copy(announce, transportId, hops: 1), null);
        Assert.Equal(0, engine2.PendingRebroadcasts);
    }

    private static Packet Header2Copy(Packet p, byte[] transportId, byte hops) =>
        Packet.Create(p.PacketType, p.DestinationType, p.DestinationHash, p.Payload, p.Context, HeaderType.Header2, transportId,
            TransportType.Transport, p.ContextFlag, hops);

    [Fact]
    public void KnownDestinations_AreBounded_AndRatchetsExpire()
    {
        var time = new FakeTime(T0);
        var known = new KnownDestinations(capacity: 5, time);
        var hashes = new List<byte[]>();
        for (var i = 0; i < 20; i++)
        {
            time.Advance(1);
            using var id = Identity.Generate();
            var dest = Destination.Create(DestinationType.Single, "dotreticulum", ["bounded"], id);
            known.Remember(dest.Hash, new byte[32], id.ExportPublicKey(), null, i == 19 ? new byte[32] : null);
            hashes.Add(dest.Hash);
            Assert.True(known.Count <= 5);
        }

        Assert.NotNull(known.Get(hashes[19]));
        Assert.Null(known.Get(hashes[0]));
        Assert.NotNull(known.GetRatchet(hashes[19]));
        time.Advance((long)KnownDestinations.RatchetExpiry.TotalSeconds + 1);
        Assert.Null(known.GetRatchet(hashes[19]));
        Assert.True(known.Forget(hashes[19]));
        Assert.Null(known.Get(hashes[19]));
        Assert.Null(known.Encrypt(hashes[19], "x"u8));
    }

    [Fact]
    public void NewerAnnounceReplacesTheRatchet_AndAnAnnounceWithoutOneKeepsIt()
    {
        var time = new FakeTime(T0);
        var engine = NewEngine(time);
        var local = NewLocal();
        using var ratchets = new DestinationRatchets { Interval = TimeSpan.Zero };

        engine.Process(local.Announce(T0, ratchets: ratchets), null);
        var first = engine.Known.GetRatchet(local.Destination.Hash);
        engine.Process(local.Announce(T0 + 1, ratchets: ratchets), null);
        var second = engine.Known.GetRatchet(local.Destination.Hash);
        Assert.NotNull(first);
        Assert.NotEqual(first, second);
        Assert.Equal(ratchets.CurrentPublicKey, second);
        engine.Process(local.Announce(T0 + 2), null);
        Assert.Equal(second, engine.Known.GetRatchet(local.Destination.Hash));
    }
}
