namespace DotReticulum.Core;

/// <summary>
/// An interface that accepts peer connections and exposes each one as its own <see cref="IPacketInterface"/>, like upstream's
/// <c>TCPServerInterface</c> spawning one sub-interface per client. A router needs this to tell clients apart: a path learned from
/// one client must be answered on that client only, and a packet from one client is forwarded to another, never echoed.
/// </summary>
/// <remarks>
/// <see cref="PacketInterfaceManager"/> subscribes before starting the spawner and adopts every child. A child is handed over
/// already connected and started; the manager only receives from it and disposes it when its receive stream ends. The spawner
/// itself neither receives nor sends packets in this mode.
/// </remarks>
public interface ISpawningPacketInterface : IPacketInterface
{
    /// <summary>Raised for every newly connected child. Handlers must not block.</summary>
    event Action<IPacketInterface>? ChildConnected;
}
