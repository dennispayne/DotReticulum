using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Core;
using static DotReticulum.Transport.Tests.LinkTests;

namespace DotReticulum.Transport.Tests;

// Executed reference: a29bfb51c468945c96a1ee22cae0114e993e2391, Transport.py:
// synthesis 2764-2810, restoration 2820-2874, path association 2467-2476,
// housekeeping 1015-1060. GenerateParity4TunnelVectors.py preserves provenance.
public sealed class Parity4TunnelTests
{
    private static readonly JsonElement Fixture = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity4-tunnels.json"))).RootElement;
    private static byte[] Bytes(string key) => Convert.FromHexString(Fixture.GetProperty(key).GetString()!);
    private static readonly byte[] StableHash = SHA256.HashData("tunnel test interface"u8);

    private static Task Deliver(TransportRouter router, IPacketInterface source, Packet packet) =>
        router.HandleAsync(new ReceivedPacket(packet.ToArray(), packet, source)).AsTask();

    private static Packet Synthesis(byte nonce = 0)
    {
        var packet = Packet.Parse(Bytes("synthesis_packet"));
        if (nonce == 0) return packet;
        using var identity = Identity.FromPrivateKey(Bytes("private_key"));
        var signed = packet.Payload.Span[..112].ToArray();
        signed[^1] = nonce;
        return Packet.Create(PacketType.Data, DestinationType.Plain, TransportRouter.TunnelSynthesisHash,
            (byte[])[.. signed, .. identity.Sign(signed)]);
    }

    private static Packet Announcement(Identity identity, int hops = 2, long emitted = 100)
    {
        var destination = Destination.Create(DestinationType.Single, "parity4", ["tunnel"], identity);
        var announce = Announce.Create(identity, destination, unixSeconds: emitted);
        return Packet.Create(PacketType.Announce, DestinationType.Single, destination.Hash, announce.Payload,
            headerType: HeaderType.Header2, transportId: Enumerable.Repeat((byte)3, 16).ToArray(),
            transportType: TransportType.Transport, hops: (byte)hops);
    }

    [Fact]
    public async Task IndependentReferenceSynthesisAuthenticatesWithoutAnObserver()
    {
        var iface = new Loopback();
        await using var manager = new PacketInterfaceManager([iface]);
        await using var router = new TransportRouter(manager);
        await Deliver(router, iface, Synthesis());
        Assert.Equal(1, router.TunnelCount);
        var tunnel = Assert.Single(router.ExportState(_ => StableHash).Tunnels!);
        Assert.Equal(Bytes("tunnel_id"), tunnel.TunnelId);
        Assert.Equal(TransportRouter.TunnelLifetime.TotalSeconds, Fixture.GetProperty("timeout").GetDouble());
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("key")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("blocked")]
    public async Task InvalidOrBlockedSynthesisIsRejectedWithDiagnostic(string failure)
    {
        var iface = new Loopback();
        await using var manager = new PacketInterfaceManager([iface]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions { IsIdentityBlocked = _ => failure == "blocked" });
        var logs = new List<string>();
        router.Log = logs.Add;
        var original = Synthesis();
        var payload = original.Payload.ToArray();
        if (failure == "signature") payload[^1] ^= 1;
        if (failure == "key") payload.AsSpan(0, 64).Clear();
        if (failure == "short") payload = payload[..^1];
        if (failure == "long") payload = [.. payload, 0];
        await Deliver(router, iface, Packet.Create(PacketType.Data, DestinationType.Plain, original.DestinationHash, payload));
        Assert.Equal(0, router.TunnelCount);
        Assert.Contains(logs, message => message.Contains("Rejected tunnel synthesis"));
    }

    [Fact]
    public async Task OutboundSynthesisUsesOnlyRequestedBearerAndHasReferenceSignatureLayout()
    {
        var (a, peerA) = Pair();
        var (b, peerB) = Pair();
        await using var manager = new PacketInterfaceManager([a, b]);
        using var identity = Identity.FromPrivateKey(Bytes("private_key"));
        await using var router = new TransportRouter(manager, new TransportRouterOptions { TransportIdentity = identity });
        await manager.StartAsync();
        await router.SynthesizeTunnelAsync(a, Bytes("interface_hash"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var incoming = peerA.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await incoming.MoveNextAsync());
        var sent = Packet.Parse(incoming.Current.ToArray());
        Assert.Equal(Bytes("synthesis_packet")[..19], sent.ToArray()[..19]);
        Assert.Equal(176, sent.Payload.Length);
        Assert.Equal(identity.ExportPublicKey(), sent.Payload.Span[..64].ToArray());
        Assert.Equal(Bytes("interface_hash"), sent.Payload.Span.Slice(64, 32).ToArray());
        Assert.True(identity.Verify(sent.Payload.Span[112..], sent.Payload.Span[..112]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.SynthesizeTunnelAsync(b, StableHash, cancelled.Token).AsTask());
        await using var other = peerB.ReceiveAsync(cancelled.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => other.MoveNextAsync().AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => router.SynthesizeTunnelAsync(a, new byte[31]).AsTask());
    }

    [Fact]
    public async Task DetachedPathSurvivesPersistenceButOnlyAuthenticatedReappearanceRestoresIt()
    {
        var old = new Loopback();
        var replacement = new Loopback();
        var time = new FakeTime(1_800_000_000);
        using var identity = Identity.Generate();
        var announce = Announcement(identity);
        await using var manager = new PacketInterfaceManager([old]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions { TimeProvider = time });
        await Deliver(router, old, Synthesis());
        await Deliver(router, old, announce);
        Assert.Single(router.Paths);
        Assert.Single(Assert.Single(router.ExportState(_ => StableHash).Tunnels!).Paths);
        router.DetachInterface(old);
        Assert.Empty(router.Paths);
        var state = router.ExportState(_ => StableHash);
        Assert.Null(Assert.Single(state.Tunnels!).InterfaceHash);
        Assert.Single(state.CachedPackets);
        await using var restartedManager = new PacketInterfaceManager([replacement]);
        await using var restarted = new TransportRouter(restartedManager, new TransportRouterOptions { TimeProvider = time });
        restarted.ImportState(state, _ => null);
        Assert.Empty(restarted.Paths);
        Assert.Equal(1, restarted.TunnelCount);
        await Deliver(restarted, replacement, Synthesis(1));
        Assert.Same(replacement, Assert.Single(restarted.Paths).Interface);
        Assert.Equal(3, restarted.HopsTo(announce.DestinationHash.Span));
        Assert.Equal(0, restarted.Engine.PendingRebroadcasts);
        state.Tunnels![0].Paths[0].RandomBlobs[0][0] ^= 1;
        Assert.NotEqual(state.Tunnels[0].Paths[0].RandomBlobs[0],
            restarted.ExportState(_ => StableHash).Tunnels![0].Paths[0].RandomBlobs[0]);
    }

    [Theory]
    [InlineData("absent_valid", false, 3, 90)]
    [InlineData("shorter_current", true, 1, 90)]
    [InlineData("same_hops_newer_tunnel", true, 2, 90)]
    [InlineData("same_hops_older_tunnel", true, 2, 110)]
    public async Task RestorationDecisionsMatchExecutedReference(string name, bool existing, int currentWireHops, long currentEmission)
    {
        var old = new Loopback();
        var replacement = new Loopback();
        var current = new Loopback();
        var time = new FakeTime(1_800_000_000);
        using var identity = Identity.Generate();
        await using var manager = new PacketInterfaceManager([old, replacement, current]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions { TimeProvider = time });
        await Deliver(router, old, Synthesis());
        await Deliver(router, old, Announcement(identity, 2, 100));
        router.DetachInterface(old);
        if (existing) await Deliver(router, current, Announcement(identity, currentWireHops, currentEmission));
        await Deliver(router, replacement, Synthesis(1));
        var expected = Fixture.GetProperty("restoration").EnumerateArray().Single(value => value.GetProperty("name").GetString() == name);
        var active = Assert.Single(router.Paths);
        Assert.Equal(expected.GetProperty("restored").GetBoolean(), ReferenceEquals(active.Interface, replacement));
        Assert.Equal(expected.GetProperty("retained").GetBoolean(),
            Assert.Single(router.ExportState(_ => StableHash).Tunnels!).Paths.Count != 0);
    }

    [Fact]
    public async Task ExpiryAndNewerActivePathPruneTunnelMemory()
    {
        var iface = new Loopback();
        var other = new Loopback();
        var time = new FakeTime(1_800_000_000);
        using var identity = Identity.Generate();
        await using var manager = new PacketInterfaceManager([iface, other]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions { TimeProvider = time });
        await Deliver(router, iface, Synthesis());
        await Deliver(router, iface, Announcement(identity, 2, 100));
        await Deliver(router, other, Announcement(identity, 1, 101));
        router.Cull();
        Assert.Empty(Assert.Single(router.ExportState(_ => StableHash).Tunnels!).Paths);
        time.Advance((long)TransportRouter.TunnelLifetime.TotalSeconds + 1);
        router.Cull();
        Assert.Equal(0, router.TunnelCount);
    }

    [Theory]
    [InlineData(false, "absent_expired")]
    [InlineData(true, "expired_current")]
    public async Task ReappearanceBeforeHousekeepingUsesReferenceExpiredPathBranches(bool existing, string referenceCase)
    {
        var old = new Loopback();
        var current = new Loopback();
        var replacement = new Loopback();
        var time = new FakeTime(1_800_000_000);
        using var identity = Identity.Generate();
        await using var manager = new PacketInterfaceManager([old, current, replacement]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions { TimeProvider = time });
        await Deliver(router, old, Synthesis());
        await Deliver(router, old, Announcement(identity, 2, 100));
        router.DetachInterface(old);
        if (existing) await Deliver(router, current, Announcement(identity, 1, 90));
        time.Advance((long)TimeSpan.FromDays(7).TotalSeconds + 1);
        await Deliver(router, replacement, Synthesis(1));
        var expected = Fixture.GetProperty("restoration").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == referenceCase);
        Assert.Equal(expected.GetProperty("restored").GetBoolean(),
            router.Paths.Any(path => ReferenceEquals(path.Interface, replacement)));
        Assert.Equal(expected.GetProperty("retained").GetBoolean(),
            Assert.Single(router.ExportState(_ => StableHash).Tunnels!).Paths.Count != 0);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("excessive")]
    [InlineData("invalid-id")]
    [InlineData("missing-cache")]
    [InlineData("invalid-signature")]
    [InlineData("wrong-hop")]
    [InlineData("wrong-next-hop")]
    [InlineData("wrong-random-blob")]
    [InlineData("stale-path")]
    [InlineData("blocked")]
    public async Task DormantTunnelImportRejectsInvalidUnbackedOrStaleState(string failure)
    {
        var iface = new Loopback();
        var time = new FakeTime(1_800_000_000);
        using var identity = Identity.Generate();
        await using var manager = new PacketInterfaceManager([iface]);
        await using var source = new TransportRouter(manager, new TransportRouterOptions { TimeProvider = time });
        await Deliver(source, iface, Synthesis());
        await Deliver(source, iface, Announcement(identity));
        source.DetachInterface(iface);
        var state = source.ExportState(_ => StableHash);
        var tunnel = Assert.Single(state.Tunnels!);
        var path = Assert.Single(tunnel.Paths);
        switch (failure)
        {
            case "expired": tunnel = tunnel with { Expires = time.Unix }; break;
            case "excessive": tunnel = tunnel with { Expires = time.Unix + 57_601 }; break;
            case "invalid-id": tunnel = tunnel with { TunnelId = new byte[31] }; break;
            case "missing-cache": state = state with { CachedPackets = [] }; break;
            case "wrong-hop": path = path with { Hops = 4 }; break;
            case "wrong-next-hop": path = path with { ReceivedFrom = new byte[16] }; break;
            case "wrong-random-blob": path = path with { RandomBlobs = [new byte[10]] }; break;
            case "stale-path": path = path with { Timestamp = time.Unix - 28_801 }; break;
            case "invalid-signature":
                var raw = state.CachedPackets[0].Raw.ToArray();
                raw[^1] ^= 1;
                var hash = Packet.Parse(raw).CalculateHash();
                state = state with { CachedPackets = [new TransportPacketState(hash, raw, true)] };
                path = path with { PacketHash = hash };
                break;
        }
        state = state with { Tunnels = [tunnel with { Paths = [path] }] };
        await using var restarted = new TransportRouter(manager, new TransportRouterOptions
        {
            TimeProvider = time, IsIdentityBlocked = _ => failure == "blocked",
        });
        restarted.ImportState(state, _ => iface);
        Assert.Equal(0, restarted.TunnelCount);
        Assert.Empty(restarted.Paths);
    }

    [Fact]
    public async Task ConcurrentEstablishmentAndCaptureRemainBoundedAndDetached()
    {
        var iface = new Loopback();
        await using var manager = new PacketInterfaceManager([iface]);
        await using var router = new TransportRouter(manager);
        await Task.WhenAll(Enumerable.Range(0, 64).Select(async index =>
        {
            await Deliver(router, iface, Synthesis((byte)(index + 1)));
            var state = router.ExportState(_ => StableHash);
            Assert.Single(state.Tunnels!);
        }));
        Assert.Equal(1, router.TunnelCount);
        Assert.Equal(Bytes("tunnel_id"), Assert.Single(router.ExportState(_ => StableHash).Tunnels!).TunnelId);
    }

    [Fact]
    public async Task SignedTunnelSaturationEvictsBoundedlyAndReportsCapacity()
    {
        var iface = new Loopback();
        var time = new FakeTime(1_800_000_000);
        await using var manager = new PacketInterfaceManager([iface]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions { TimeProvider = time });
        var diagnostics = new List<string>();
        router.Log = diagnostics.Add;
        using var identity = Identity.FromPrivateKey(Bytes("private_key"));
        byte[] lastId = [];
        for (var index = 0; index < 1025; index++)
        {
            var interfaceHash = new byte[32];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(interfaceHash, index);
            var signed = (byte[])[.. identity.ExportPublicKey(), .. interfaceHash, .. new byte[16]];
            lastId = SHA256.HashData(signed.AsSpan(0, 96));
            await Deliver(router, iface, Packet.Create(PacketType.Data, DestinationType.Plain, TransportRouter.TunnelSynthesisHash,
                (byte[])[.. signed, .. identity.Sign(signed)]));
            time.Advance(1);
        }
        Assert.Equal(1024, router.TunnelCount);
        Assert.Contains(router.ExportState(_ => StableHash).Tunnels!, tunnel => tunnel.TunnelId.AsSpan().SequenceEqual(lastId));
        Assert.Contains(diagnostics, message => message.Contains("Tunnel capacity reached"));
    }
}
