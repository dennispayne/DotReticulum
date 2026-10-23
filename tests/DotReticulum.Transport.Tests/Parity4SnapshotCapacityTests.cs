namespace DotReticulum.Transport.Tests;

public sealed class Parity4SnapshotCapacityTests
{
    [Fact]
    public void CombinedCacheSaturationPreservesSchemaBoundAndRemovesDanglingTunnelPaths()
    {
        var packets = Enumerable.Range(0, 65_538).Select(index =>
        {
            var hash = new byte[32];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hash, index);
            return new TransportPacketState(hash, [1], index < 65_537, index == 65_537 ? 100.25 : null);
        }).ToArray();
        TransportPathState Path(int index) => new(new byte[16], 100, new byte[16], 1, 200,
            [new byte[10]], null, packets[index].PacketHash);
        var active = Path(0);
        var retainedTunnelPath = Path(65_535);
        var omittedTunnelPath = Path(65_536);
        var snapshot = new TransportStateSnapshot([active], [],
            packets, [new TransportTunnelState(new byte[32], null, [retainedTunnelPath, omittedTunnelPath], 200)]);

        var bounded = TransportRouter.LimitSnapshotPackets(snapshot);

        Assert.Equal(65_536, bounded.CachedPackets.Count);
        Assert.Same(active, Assert.Single(bounded.Paths));
        Assert.Same(retainedTunnelPath, Assert.Single(Assert.Single(bounded.Tunnels!).Paths));
        Assert.DoesNotContain(bounded.CachedPackets, packet => !packet.Announce);
        Assert.Equal(2, Assert.Single(snapshot.Tunnels!).Paths.Count);
        Assert.Same(bounded, TransportRouter.LimitSnapshotPackets(bounded));
    }
}
