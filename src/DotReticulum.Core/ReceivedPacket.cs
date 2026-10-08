namespace DotReticulum.Core;

/// <summary>A packet received from a managed packet interface.</summary>
public sealed record ReceivedPacket(IPacketInterface Source, ReadOnlyMemory<byte> Packet);
