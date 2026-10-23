using DotReticulum.Core;

namespace DotReticulum.Transport;

/// <summary>
/// Connects an <see cref="AnnounceEngine"/> to a <see cref="PacketInterfaceManager"/>: feeds received announces to the engine, hands
/// every other packet to <see cref="PacketReceived"/>, and transmits due rebroadcasts (never back out the ingress interface).
/// </summary>
public sealed class AnnounceProcessor(PacketInterfaceManager manager, AnnounceEngine engine, TimeSpan? rebroadcastInterval = null)
{
    private readonly TimeSpan _interval = rebroadcastInterval ?? TimeSpan.FromSeconds(1);

    /// <summary>Called for each received packet that is not an announce (and so not handled by the engine).</summary>
    public Func<ReceivedPacket, ValueTask>? PacketReceived { get; set; }

    /// <summary>Called after every processed announce with the engine's verdict.</summary>
    public Action<ReceivedPacket, AnnounceResult>? AnnounceProcessed { get; set; }

    /// <summary>Runs until cancelled (or the manager's receive stream ends).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var rebroadcasts = RebroadcastLoopAsync(linked.Token);
        try
        {
            await foreach (var received in manager.ReceiveWithSourceAsync(linked.Token).ConfigureAwait(false))
            {
                if (received.Packet.PacketType == PacketType.Announce)
                {
                    var result = engine.Process(received.Packet, received.Source);
                    if (AnnounceProcessed is { } observers)
                    {
                        foreach (Action<ReceivedPacket, AnnounceResult> observer in observers.GetInvocationList())
                        {
                            try { observer(received, result); }
                            catch (Exception ex)
                            {
                                engine.ReportCallbackError(nameof(AnnounceProcessed), received.Packet.DestinationHash.Span, ex);
                            }
                        }
                    }
                }
                else if (PacketReceived is { } handlers)
                {
                    foreach (Func<ReceivedPacket, ValueTask> handler in handlers.GetInvocationList())
                    {
                        try { await handler(received).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                        catch (Exception ex) { engine.ReportCallbackError(nameof(PacketReceived), received.Packet.DestinationHash.Span, ex); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try { await rebroadcasts.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RebroadcastLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var due in engine.CollectDueRebroadcasts())
            {
                if (due.TargetInterface is { } target)
                {
                    if (manager.Contains(target)) await manager.SendToAsync(due.Packet, target, cancellationToken).ConfigureAwait(false);
                }
                else if (due.ExcludedInterface is null) await manager.SendAsync(due.Packet, cancellationToken).ConfigureAwait(false);
                else await manager.SendExceptAsync(due.Packet, due.ExcludedInterface, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
