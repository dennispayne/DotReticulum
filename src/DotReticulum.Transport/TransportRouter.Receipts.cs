using DotReticulum.Core;

namespace DotReticulum.Transport;

// Single-destination packet proofs and receipts (upstream Identity.prove / Packet.send receipts / PacketReceipt.validate_proof).
public sealed partial class TransportRouter
{
    private const int ExplicitProofLength = 32 + 64;
    private const int ImplicitProofLength = 64;

    private readonly Dictionary<PacketReceipt, PendingReceipt> _receipts = new();
    public const int MaximumReceipts = 1024;
    private readonly object _receiptTrackingGate = new();
    private readonly Queue<PacketReceipt> _receiptOrder = new();

    private sealed record PendingReceipt(PacketReceipt Receipt, Identity Destination, double Deadline);

    /// <summary>Number of sent packets still awaiting a proof.</summary>
    public int PendingReceiptCount
    {
        get { lock (_gate) return _receipts.Values.Count(item => item.Receipt.Status == PacketReceiptStatus.Sent); }
    }

    /// <summary>
    /// Sends an endpoint packet and tracks its proof when eligible. The returned receipt completes with true when a valid explicit or implicit
    /// proof signed by <paramref name="destinationIdentity"/> arrives, and with false when <paramref name="timeout"/> elapses
    /// (default 6 s plus first-hop serialization time and 6 s per hop; unknown paths use 128 hops, as upstream).
    /// </summary>
    public async ValueTask<PacketReceipt?> SendWithReceiptAsync(Packet packet, Identity destinationIdentity, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(destinationIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        if (!PacketReceipt.IsEligible(packet))
        {
            await SendAsync(packet, cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (packet.DestinationType == DestinationType.Link) throw new ArgumentException("Use the link's send API for link receipts.", nameof(packet));
        var hash = packet.CalculateHash();
        var seconds = timeout?.TotalSeconds ?? FirstHopTimeout(packet.DestinationHash.Span) + (6 * (HopsTo(packet.DestinationHash.Span) ?? Packet.HopLimit));
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(timeout));
        var now = Links.UnixNow;
        var receipt = new PacketReceipt(hash, now, seconds);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            TrackReceipt(receipt);
            _receipts[receipt] = new PendingReceipt(receipt, destinationIdentity, now + seconds);
        }
        try
        {
            await SendAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _receipts.Remove(receipt);
                receipt.Conclude(PacketReceiptStatus.Failed, Links.UnixNow);
            }
            throw;
        }

        return receipt;
    }

    /// <summary>
    /// Proves a received packet to its sender: a PROOF packet addressed to the packet's truncated hash carrying the destination identity's
    /// signature of the full hash (implicit proof, the upstream default), sent back on the interface the packet arrived on.
    /// </summary>
    public async ValueTask ProveAsync(LocalDestination destination, ReceivedPacket received, bool explicitProof = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(received);
        var hash = received.Packet.CalculateHash();
        var signature = destination.Identity.Sign(hash);
        byte[] data = explicitProof ? [.. hash, .. signature] : signature;
        var proof = Packet.Create(PacketType.Proof, DestinationType.Single, hash.AsMemory(0, Destination.HashLength), data);
        if (_manager.Contains(received.Source)) await _manager.SendToAsync(proof, received.Source, cancellationToken).ConfigureAwait(false);
        else await _manager.SendAsync(proof, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ProveIfRequiredAsync(LocalDestination local, ReceivedPacket received)
    {
        if (local.Destination.Type != DestinationType.Single) return;
        var prove = local.ProofStrategy == ProofStrategy.All;
        if (local.ProofStrategy == ProofStrategy.App && local.ProofRequested is { } callback)
        {
            Links.DispatchAsync(async () =>
            {
                try
                {
                    if (callback(received.Packet)) await ProveAsync(local, received).ConfigureAwait(false);
                }
                catch (Exception ex) { LogReceiptFailure("Proof request callback failed: " + ex.Message); }
            });
            return;
        }

        if (!prove) return;
        try { await ProveAsync(local, received).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogReceiptFailure("Could not send packet proof: " + ex.Message); }
    }

    private bool TryValidateReceipt(ReceivedPacket received)
    {
        var packet = received.Packet;
        if (packet.DestinationType != DestinationType.Single) return false;
        var data = packet.Payload.Span;
        if (data.Length != ExplicitProofLength && data.Length != ImplicitProofLength) return false;
        PendingReceipt[] candidates;
        lock (_gate) candidates = [.. _receipts.Values];
        var delivered = false;
        foreach (var pending in candidates)
        {
            var receipt = pending.Receipt;
            if (receipt.Status != PacketReceiptStatus.Sent
                || (data.Length == ExplicitProofLength && !data[..32].SequenceEqual(receipt.Hash))
                || !pending.Destination.Verify(data[^64..], receipt.Hash)) continue;
            lock (_gate)
            {
                if (!_receipts.Remove(receipt) || !receipt.Conclude(PacketReceiptStatus.Delivered, Links.UnixNow, packet)) continue;
            }
            delivered = true;
            try { receipt.Delivered?.Invoke(receipt); }
            catch (Exception ex) { LogReceiptFailure("Delivery callback failed: " + ex.Message); }
        }
        return delivered;
    }

    private void ExpireReceipts()
    {
        var now = Links.UnixNow;
        List<PendingReceipt> expired = [];
        lock (_gate)
        {
            foreach (var kv in _receipts.Where(kv => kv.Value.Receipt.Status != PacketReceiptStatus.Sent || now > kv.Value.Deadline).ToArray())
            {
                _receipts.Remove(kv.Key);
                if (kv.Value.Receipt.Conclude(PacketReceiptStatus.Failed, now)) expired.Add(kv.Value);
            }
        }

        foreach (var pending in expired)
        {
            var receipt = pending.Receipt;
            try { receipt.TimedOut?.Invoke(receipt); }
            catch (Exception ex) { LogReceiptFailure("Receipt timeout callback failed: " + ex.Message); }
        }
    }

    private void FailReceipts()
    {
        PendingReceipt[] pending;
        lock (_gate)
        {
            pending = [.. _receipts.Values];
            _receipts.Clear();
        }
        foreach (var item in pending)
        {
            if (!item.Receipt.Conclude(PacketReceiptStatus.Failed, Links.UnixNow)) continue;
            try { item.Receipt.TimedOut?.Invoke(item.Receipt); }
            catch (Exception ex)
            {
                try { Log?.Invoke("Receipt cleanup callback failed: " + ex.Message); }
                catch { }
            }
        }
    }

    internal double FirstHopTimeout(ReadOnlySpan<byte> destinationHash)
    {
        var bitrate = Engine.GetPath(destinationHash)?.Interface?.Bitrate ?? 0;
        return 6 + (bitrate > 0 ? Packet.Mtu * 8.0 / bitrate : 0);
    }

    private void LogReceiptFailure(string message)
    {
        try { Log?.Invoke(message); }
        catch { }
    }

    internal void TrackReceipt(PacketReceipt receipt)
    {
        List<PacketReceipt> culled = [];
        lock (_receiptTrackingGate)
        {
            if (_receiptOrder.Count >= MaximumReceipts)
            {
                var pending = _receiptOrder.Where(r => r.Status == PacketReceiptStatus.Sent).ToArray();
                _receiptOrder.Clear();
                foreach (var item in pending) _receiptOrder.Enqueue(item);
            }
            _receiptOrder.Enqueue(receipt);
            while (_receiptOrder.Count > MaximumReceipts)
            {
                var oldest = _receiptOrder.Dequeue();
                oldest.Timeout = -1;
                if (oldest.Conclude(PacketReceiptStatus.Culled, Links.UnixNow)) culled.Add(oldest);
            }
        }
        foreach (var item in culled)
        {
            lock (_gate)
            {
                _receipts.Remove(item);
            }
            Links.Dispatch(() => item.TimedOut?.Invoke(item));
        }
    }
}
