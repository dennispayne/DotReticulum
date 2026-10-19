namespace DotReticulum.Core;

/// <summary>
/// A bearer that moves whole Reticulum packets. Lifecycle: <see cref="StartAsync"/> exactly once, then
/// <see cref="ReceiveAsync"/> (single enumerator) and <see cref="SendAsync"/>, then <see cref="IAsyncDisposable.DisposeAsync"/>.
/// <see cref="IAsyncDisposable.DisposeAsync"/> must be idempotent, must end any active receive enumeration, and must
/// complete promptly.
/// </summary>
public interface IPacketInterface : IAsyncDisposable
{
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

/// <summary>A parsed packet together with the interface it arrived on.</summary>
/// <param name="Raw">The packet bytes exactly as received.</param>
/// <param name="Packet">The parsed packet.</param>
/// <param name="Source">The interface the packet arrived on.</param>
public sealed record ReceivedPacket(ReadOnlyMemory<byte> Raw, Packet Packet, IPacketInterface Source);
