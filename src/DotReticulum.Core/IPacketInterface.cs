namespace DotReticulum.Core;

/// <summary>
/// A bearer that moves whole Reticulum packets. Lifecycle: <see cref="StartAsync"/> exactly once, then
/// <see cref="ReceiveAsync"/> (single enumerator) and <see cref="SendAsync"/>, then <see cref="IAsyncDisposable.DisposeAsync"/>.
/// <see cref="IAsyncDisposable.DisposeAsync"/> must be idempotent, must end any active receive enumeration, and must
/// complete promptly.
/// </summary>
public interface IPacketInterface : IAsyncDisposable
{
    /// <summary>Configured or estimated bearer bitrate in bits per second; zero means unknown.</summary>
    long Bitrate => 0;

    /// <summary>Maximum unprotected packet length supported by this bearer.</summary>
    int HardwareMtu => Packet.Mtu;

    /// <summary>Whether link setup may negotiate an upgraded MTU on this bearer.</summary>
    bool SupportsMtuDiscovery => false;

    /// <summary>Opens the underlying bearer (bind, connect, open). Must not be called twice.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Yields raw packet bytes as received from the bearer. Only one enumerator may be active. The stream completes
    /// when the bearer closes, and faults with the underlying exception on a bearer failure.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Transmits one packet (already a complete wire packet of at most the MTU).</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
}

/// <summary>A persistent initiating bearer that can establish a new connection without changing interface identity.</summary>
public interface IReconnectingPacketInterface : IPacketInterface
{
    bool IsConnected { get; }

    /// <summary>Raised after the initial connection and each successful reconnection.</summary>
    event Action? Connected;

    /// <summary>Raised after a connection is lost, before reconnection attempts start; not raised for disposal.</summary>
    event Action? Disconnected;
}

/// <summary>Source-specific child selection and accounting exceptions; ownership still belongs to the parent.</summary>
public interface IPacketInterfaceChildPolicy
{
    bool InheritParentOutgoing { get; }
    bool Outgoing { get; }
    bool CountParentTraffic { get; }
    bool InheritParentAccessCode => true;
}

/// <summary>A parsed packet together with the interface it arrived on.</summary>
/// <param name="Raw">The packet bytes exactly as received.</param>
/// <param name="Packet">The parsed packet.</param>
/// <param name="Source">The interface the packet arrived on.</param>
public sealed record ReceivedPacket(ReadOnlyMemory<byte> Raw, Packet Packet, IPacketInterface Source)
{
    private byte[]? _ratchetId;

    /// <summary>Selected local decryption ratchet, or null for base-key, GROUP/PLAIN and undecrypted packets.</summary>
    public byte[]? RatchetId
    {
        get => _ratchetId?.ToArray();
        init => _ratchetId = value?.ToArray();
    }
}
