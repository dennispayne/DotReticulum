using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnsd;

public sealed partial class RnsdHost
{
    private byte[] InterfaceHash(IPacketInterface iface) =>
        RuntimeStatistics.InterfaceHash(iface, InterfaceName(iface));

    private IPacketInterface? ResolveInterface(byte[]? hash) =>
        hash is null ? null : _manager.Interfaces.FirstOrDefault(iface => InterfaceHash(iface).AsSpan().SequenceEqual(hash));

    private string ResolveInterfaceName(byte[]? hash) =>
        ResolveInterface(hash) is { } iface ? InterfaceName(iface) : "None";

    private sealed class HostStateProvider(RnsdHost host) : IRuntimeStateProvider
    {
        public RuntimeStateSnapshot CaptureState()
        {
            var state = host.Router.ExportState(host.InterfaceHash);
            return new RuntimeStateSnapshot(host.Router.Engine.Known.ExportSnapshot(),
                state.Paths.Select(p => new PersistedPath(p.DestinationHash, p.Timestamp, p.ReceivedFrom, p.Hops, p.Expires,
                    p.RandomBlobs, p.InterfaceHash, p.PacketHash)).ToArray(),
                (state.Tunnels ?? []).Select(t => new PersistedTunnel(t.TunnelId, t.InterfaceHash,
                    t.Paths.Select(p => new PersistedPath(p.DestinationHash, p.Timestamp, p.ReceivedFrom, p.Hops, p.Expires,
                        p.RandomBlobs, p.InterfaceHash, p.PacketHash)).ToArray(), t.Expires)).ToArray(), state.PacketHashes,
                state.CachedPackets.Select(p => new PersistedPacket(p.PacketHash, p.Raw, p.Announce, p.CachedAt)).ToArray());
        }

        public void RestoreState(RuntimeStateSnapshot snapshot)
        {
            host.Router.Engine.Known.RestoreSnapshot(snapshot.KnownDestinations);
            if (!host.Router.TransportEnabled)
                snapshot = snapshot with { Paths = [], Tunnels = [], PacketHashes = [] };
            host.Router.ImportState(new TransportStateSnapshot(
                snapshot.Paths.Select(p => new TransportPathState(p.DestinationHash, p.Timestamp, p.ReceivedFrom, p.Hops, p.Expires,
                    p.RandomBlobs, p.InterfaceHash, p.PacketHash)).ToArray(),
                snapshot.PacketHashes,
                snapshot.CachedPackets.Select(p => new TransportPacketState(p.PacketHash, p.Raw, p.Announce, p.CachedAt)).ToArray(),
                snapshot.Tunnels.Select(t => new TransportTunnelState(t.TunnelId, t.InterfaceHash,
                    t.Paths.Select(p => new TransportPathState(p.DestinationHash, p.Timestamp, p.ReceivedFrom, p.Hops, p.Expires,
                        p.RandomBlobs, p.InterfaceHash, p.PacketHash)).ToArray(), t.Expires)).ToArray()),
                hash => host.ResolveInterface(hash));
        }
    }
}
