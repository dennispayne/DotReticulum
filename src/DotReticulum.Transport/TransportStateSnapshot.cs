using DotReticulum.Core;

namespace DotReticulum.Transport;

/// <summary>Detached upstream path-table fields, including the stable full interface hash supplied by the runtime.</summary>
public sealed record TransportPathState(
    byte[] DestinationHash, double Timestamp, byte[] ReceivedFrom, int Hops, double Expires,
    IReadOnlyList<byte[]> RandomBlobs, byte[]? InterfaceHash, byte[] PacketHash);

/// <summary>A cached packet's original wire bytes. Currently only announcements backing actual paths are cached.</summary>
public sealed record TransportPacketState(byte[] PacketHash, byte[] Raw, bool Announce);

/// <summary>Existing native routing and deduplication state. Tunnel and arbitrary packet-cache mechanics are not implemented.</summary>
public sealed record TransportStateSnapshot(
    IReadOnlyList<TransportPathState> Paths,
    IReadOnlyList<byte[]> PacketHashes,
    IReadOnlyList<TransportPacketState> CachedPackets);

/// <summary>An isolated view of a live path, using the actual receiving interface rather than its persisted hash.</summary>
public sealed record TransportPathInfo(
    byte[] DestinationHash, double Timestamp, byte[] ReceivedFrom, int Hops, double Expires,
    IPacketInterface? Interface, byte[] PacketHash);

public sealed record AnnounceRateInfo(
    byte[] Hash, double Last, int RateViolations, double BlockedUntil, IReadOnlyList<double> Timestamps);
