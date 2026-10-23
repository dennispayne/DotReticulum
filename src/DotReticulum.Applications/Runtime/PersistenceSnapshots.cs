using DotReticulum.Transport;

namespace DotReticulum.Applications.Runtime;

/// <summary>Upstream Transport.save_path_table's eight fields, with exact binary hashes.
/// InterfaceHash is a persistent interface identity, not an object reference; the runtime must resolve it
/// against active interfaces before restoring a path. Timestamps are Unix seconds, including fractions.</summary>
public sealed record PersistedPath(
    byte[] DestinationHash, double Timestamp, byte[] ReceivedFrom, int Hops, double Expires,
    IReadOnlyList<byte[]> RandomBlobs, byte[]? InterfaceHash, byte[] PacketHash);

/// <summary>Upstream Transport.save_tunnel_table's four fields, restored dormant until authenticated synthesis.</summary>
public sealed record PersistedTunnel(byte[] TunnelId, byte[]? InterfaceHash, IReadOnlyList<PersistedPath> Paths, double Expires);

/// <summary>A complete cached wire packet, keyed by its full upstream packet hash. Announce identifies the
/// separate upstream announce cache namespace; interface association is resolved from the path table.</summary>
public sealed record PersistedPacket(byte[] PacketHash, byte[] Raw, bool Announce, double? CachedAt = null);

/// <summary>Detached, caller-owned runtime state. Only public identities and public ratchets belong here.
/// Capture must be synchronized by the provider. Secret identities and local destination ratchets use
/// separate owner-only files and are never included in this aggregate.</summary>
public sealed record RuntimeStateSnapshot(
    IReadOnlyList<KnownDestinationSnapshot> KnownDestinations,
    IReadOnlyList<PersistedPath> Paths,
    IReadOnlyList<PersistedTunnel> Tunnels,
    IReadOnlyList<byte[]> PacketHashes,
    IReadOnlyList<PersistedPacket> CachedPackets)
{
    public static RuntimeStateSnapshot Empty => new([], [], [], [], []);
}

/// <summary>Boundary for the runtime's existing equivalent state. Implementations must reject expired,
/// blackholed, unavailable-interface and missing/corrupt-announcement paths before restoring them.
/// Tunnel paths remain dormant until the authenticated transport endpoint reappears.</summary>
public interface IRuntimeStateProvider
{
    RuntimeStateSnapshot CaptureState();
    void RestoreState(RuntimeStateSnapshot snapshot);
}
