using DotReticulum.Core;
using static DotReticulum.Transport.Tests.LinkTests;

namespace DotReticulum.Transport.Tests;

// Pinned a29bfb51c468945c96a1ee22cae0114e993e2391 Transport.py:408-457
// reconstructs paths from active stable interface hashes and cached announces;
// save_path_table:3788 preserves all eight path fields. Native restoration also
// validates signatures, expiry and blackhole policy before installing state.
[Trait("Category", "Unit")]
public class LifecycleStateTests
{
    private static readonly byte[] InterfaceHash = Enumerable.Repeat((byte)7, 32).ToArray();

    private static async Task<TransportStateSnapshot> CaptureAsync(TransportRouter router, Loopback iface, Identity identity)
    {
        var destination = Destination.Create(DestinationType.Single, "lifecycle", ["state"], identity);
        var packet = Announce.Create(identity, destination);
        await router.HandleAsync(new ReceivedPacket(packet.ToArray(), packet, iface));
        return router.ExportState(_ => InterfaceHash);
    }

    [Fact]
    public async Task RestartRestoresOriginalFieldsAndCachesWithoutCallbacksOrRebroadcast()
    {
        var iface = new Loopback();
        var time = new FakeTime(1_800_000_000);
        await using var manager = new PacketInterfaceManager([iface]);
        await using var original = new TransportRouter(manager, new TransportRouterOptions { TransportEnabled = true, TimeProvider = time });
        using var identity = Identity.Generate();
        var state = await CaptureAsync(original, iface, identity);
        await using var restarted = new TransportRouter(manager, new TransportRouterOptions { TransportEnabled = true, TimeProvider = time });
        restarted.ImportState(state, hash => hash.AsSpan().SequenceEqual(InterfaceHash) ? iface : null);
        var restored = restarted.ExportState(_ => InterfaceHash);
        Assert.Single(restarted.Paths);
        Assert.Equal(state.Paths[0].Timestamp, restored.Paths[0].Timestamp);
        Assert.Equal(state.Paths[0].Expires, restored.Paths[0].Expires);
        Assert.Equal(state.Paths[0].ReceivedFrom, restarted.NextHop(state.Paths[0].DestinationHash));
        Assert.Equal(state.Paths[0].RandomBlobs[0], restored.Paths[0].RandomBlobs[0]);
        Assert.Equal(state.CachedPackets[0].Raw, restored.CachedPackets[0].Raw);
        Assert.Contains(restored.PacketHashes, hash => hash.AsSpan().SequenceEqual(state.PacketHashes[0]));
        Assert.Equal(0, restarted.Engine.PendingRebroadcasts);
        state.CachedPackets[0].Raw[^1] ^= 1;
        state.Paths[0].RandomBlobs[0][0] ^= 1;
        Assert.Equal(restored.CachedPackets[0].Raw, restarted.ExportState(_ => InterfaceHash).CachedPackets[0].Raw);
        Assert.Equal(restored.Paths[0].RandomBlobs[0], restarted.ExportState(_ => InterfaceHash).Paths[0].RandomBlobs[0]);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("stale")]
    [InlineData("missing-cache")]
    [InlineData("corrupt-cache")]
    [InlineData("invalid-signature")]
    [InlineData("wrong-hop")]
    [InlineData("wrong-blob")]
    [InlineData("wrong-next-hop")]
    [InlineData("wrong-destination")]
    [InlineData("unresolved-interface")]
    [InlineData("detached-interface")]
    [InlineData("blocked")]
    public async Task RestoreSkipsInvalidOrUnavailablePath(string failure)
    {
        var iface = new Loopback();
        var time = new FakeTime(1_800_000_000);
        await using var sourceManager = new PacketInterfaceManager([iface]);
        await using var source = new TransportRouter(sourceManager, new TransportRouterOptions { TimeProvider = time });
        using var identity = Identity.Generate();
        var state = await CaptureAsync(source, iface, identity);
        var path = state.Paths[0];
        switch (failure)
        {
            case "expired": path = path with { Expires = 1_799_999_999 }; break;
            case "stale": path = path with { Timestamp = path.Timestamp - TimeSpan.FromDays(8).TotalSeconds }; break;
            case "missing-cache": state = state with { CachedPackets = [] }; break;
            case "corrupt-cache": state.CachedPackets[0].Raw[^1] ^= 1; break;
            case "invalid-signature":
                state.CachedPackets[0].Raw[^1] ^= 1;
                var hash = Packet.Parse(state.CachedPackets[0].Raw).CalculateHash();
                state = state with { CachedPackets = [state.CachedPackets[0] with { PacketHash = hash }] };
                path = path with { PacketHash = hash };
                break;
            case "wrong-hop": path = path with { Hops = path.Hops + 1 }; break;
            case "wrong-blob": path = path with { RandomBlobs = [new byte[10]] }; break;
            case "wrong-next-hop": path = path with { ReceivedFrom = new byte[16] }; break;
            case "wrong-destination": path = path with { DestinationHash = new byte[16] }; break;
        }
        state = state with { Paths = [path] };
        await using var manager = new PacketInterfaceManager(failure == "detached-interface" ? [] : [iface]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions
        {
            TimeProvider = time,
            IsIdentityBlocked = _ => failure == "blocked",
        });
        router.ImportState(state, _ => failure == "unresolved-interface" ? null : iface);
        Assert.Empty(router.Paths);
        Assert.Equal(0, router.Engine.PendingRebroadcasts);
    }

    [Fact]
    public async Task ManagementDropsPathsAndQueuedAnnouncementsAndSnapshotsAreIsolated()
    {
        var iface = new Loopback();
        await using var manager = new PacketInterfaceManager([iface]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions { TransportEnabled = true });
        using var identity = Identity.Generate();
        var state = await CaptureAsync(router, iface, identity);
        var destination = state.Paths[0].DestinationHash;
        Assert.True(router.Engine.QueuePathResponse(destination, iface, TimeSpan.Zero));
        var snapshot = router.Paths;
        snapshot[0].ReceivedFrom[0] ^= 1;
        Assert.Equal(destination, router.NextHop(destination));
        Assert.Equal(1, router.DropPathsVia(destination));
        Assert.Empty(router.Paths);
        Assert.Equal(0, router.Engine.PendingRebroadcasts);
        Assert.Empty(router.Engine.CollectDueRebroadcasts());
        Assert.False(router.DropPath(destination));
        Assert.Equal(0, router.DropAllPaths());
    }

    [Fact]
    public async Task ManagementRateSnapshotsQueueClearAndDeferredPathExpiryReflectRealState()
    {
        var iface = new Loopback();
        var time = new FakeTime(1_800_000_000);
        await using var manager = new PacketInterfaceManager([iface]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions
        {
            TransportEnabled = true, TimeProvider = time,
            RateLimit = new AnnounceRateLimit(TimeSpan.FromSeconds(60), 1, TimeSpan.FromSeconds(30)),
        });
        using var identity = Identity.Generate();
        var state = await CaptureAsync(router, iface, identity);
        var rate = Assert.Single(router.Engine.Rates);
        Assert.Equal(state.Paths[0].DestinationHash, rate.Hash);
        Assert.Single(rate.Timestamps);
        Assert.True(router.Engine.QueuePathResponse(rate.Hash, iface, TimeSpan.Zero));
        Assert.Equal(2, router.Engine.ClearPendingRebroadcasts());
        Assert.Equal(0, router.Engine.ClearPendingRebroadcasts());
        Assert.Empty(router.Engine.CollectDueRebroadcasts());
        Assert.True(router.ExpirePath(rate.Hash));
        Assert.Equal(0, Assert.Single(router.Paths).Timestamp);
        router.Cull();
        Assert.Empty(router.Paths);
        Assert.False(router.ExpirePath(rate.Hash));
        Assert.Equal(0, router.ActiveLinkTableCount);
    }

    [Fact]
    public async Task RestartRestoresActualPacketDeduplicationWithoutInventingPacketCacheEntries()
    {
        var iface = new Loopback();
        await using var manager = new PacketInterfaceManager([iface]);
        using var identity = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "lifecycle", ["dedup"], identity);
        var packet = Packet.Create(PacketType.Data, DestinationType.Single, destination.Hash, identity.Encrypt(new byte[] { 1 }));
        await using var source = new TransportRouter(manager);
        await source.HandleAsync(new ReceivedPacket(packet.ToArray(), packet, iface));
        var state = source.ExportState(_ => InterfaceHash);
        Assert.Single(state.PacketHashes);
        Assert.Empty(state.CachedPackets);
        await using var restarted = new TransportRouter(manager);
        var deliveries = 0;
        restarted.RegisterLocalDestination(new LocalDestination(identity, destination)
        {
            PacketReceived = _ => { Interlocked.Increment(ref deliveries); return ValueTask.CompletedTask; },
        });
        restarted.ImportState(state, _ => iface);
        await restarted.HandleAsync(new ReceivedPacket(packet.ToArray(), packet, iface));
        Assert.Equal(0, deliveries);
        var unseen = Packet.Create(PacketType.Data, DestinationType.Single, destination.Hash, identity.Encrypt(new byte[] { 2 }));
        await restarted.HandleAsync(new ReceivedPacket(unseen.ToArray(), unseen, iface));
        await LinkTests.Until(() => Volatile.Read(ref deliveries) == 1);
        Assert.Equal(1, deliveries);
    }
}
