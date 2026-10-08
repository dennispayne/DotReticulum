namespace DotReticulum.Core;

/// <summary>A packet-oriented bearer contract; framing and networking belong to implementations.</summary>
public interface IPacketInterface
{
    /// <summary>
    /// Receives complete wire packets. Returned memory must remain valid and
    /// unchanged while the consumer uses it; it must not be a reused receive buffer.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>The caller keeps packet memory unchanged until the returned operation completes.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);
}
