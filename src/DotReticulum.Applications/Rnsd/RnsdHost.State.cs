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
                    p.RandomBlobs, p.InterfaceHash, p.PacketHash)).ToArray(), [], state.PacketHashes,
                state.CachedPackets.Select(p => new PersistedPacket(p.PacketHash, p.Raw, p.Announce)).ToArray());
        }

        public void RestoreState(RuntimeStateSnapshot snapshot)
        {
            if (snapshot.Tunnels.Count != 0)
                throw new NotSupportedException("Stored transport tunnels require the Parity 4 tunnel implementation.");
            host.Router.Engine.Known.RestoreSnapshot(snapshot.KnownDestinations);
            host.Router.ImportState(new TransportStateSnapshot(
                snapshot.Paths.Select(p => new TransportPathState(p.DestinationHash, p.Timestamp, p.ReceivedFrom, p.Hops, p.Expires,
                    p.RandomBlobs, p.InterfaceHash, p.PacketHash)).ToArray(),
                snapshot.PacketHashes,
                snapshot.CachedPackets.Select(p => new TransportPacketState(p.PacketHash, p.Raw, p.Announce)).ToArray()),
                hash => host.ResolveInterface(hash));
        }
    }
}
