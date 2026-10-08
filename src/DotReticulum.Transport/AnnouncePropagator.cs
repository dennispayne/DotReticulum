using DotReticulum.Core;

namespace DotReticulum.Transport;

/// <summary>
/// Relays valid non-ratcheted announces to managed interfaces other than ingress.
/// This is a bounded relay primitive, not a complete Reticulum transport node.
/// </summary>
public sealed class AnnouncePropagator
{
    private readonly PacketInterfaceManager _interfaces;
    private readonly AnnounceRateLimiter _rateLimiter;

    public AnnouncePropagator(PacketInterfaceManager interfaces, AnnounceRateLimiter rateLimiter)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        _interfaces = interfaces;
        _rateLimiter = rateLimiter;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await foreach (var received in _interfaces.ReceiveWithSourceAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!Packet.TryParse(received.Packet, out var packet) ||
                !Announce.TryValidate(packet!, out _) ||
                packet!.Hops >= Packet.HopLimit - 1 ||
                !_rateLimiter.TryAccept(packet.DestinationHash.Span))
                continue;

            var forwarded = packet.Raw.ToArray();
            forwarded[1]++;
            await _interfaces.SendExceptAsync(forwarded, received.Source, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
