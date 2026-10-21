namespace DotReticulum.Core;

/// <summary>Measurements at the decoded packet boundary, not physical/framing byte counters.</summary>
public sealed record PacketInterfaceStatistics(
    IPacketInterface Interface,
    IPacketInterface? Parent,
    long ReceivedBytes,
    long SentBytes,
    long ReceivedPackets,
    long SentPackets,
    long ReceivedAnnounceBytes,
    long SentAnnounceBytes,
    long ReceivedAnnounces,
    long SentAnnounces,
    long MalformedPackets,
    long FailedSends,
    long FailedSendBytes,
    long BackpressureWaits,
    int PendingSends);

/// <summary>Optional actual device measurements supplied by a bearer; absent values must remain absent.</summary>
public interface IPacketInterfaceDiagnostics
{
    IReadOnlyDictionary<string, object?> GetDiagnostics();
}
