using DotReticulum.Core;

namespace DotReticulum.Transport;

// Single-destination packet proofs and receipts (upstream Identity.prove / Packet.send receipts / PacketReceipt.validate_proof).
public sealed partial class TransportRouter
{
    private const int ExplicitProofLength = 32 + 64;
    private const int ImplicitProofLength = 64;

    private readonly Dictionary<string, PendingReceipt> _receipts = new();

    private sealed record PendingReceipt(PacketReceipt Receipt, Identity Destination, double Deadline);

    /// <summary>Number of sent packets still awaiting a proof.</summary>
    public int PendingReceiptCount
    {
        get { lock (_gate) return _receipts.Count; }
    }

    /// <summary>
    /// Sends a packet to a SINGLE destination and tracks its proof. The returned receipt completes with true when a valid explicit or implicit
    /// proof signed by <paramref name="destinationIdentity"/> arrives, and with false when <paramref name="timeout"/> elapses
    /// (default 6 s plus 6 s per known hop, as upstream).
    /// </summary>
    public async ValueTask<PacketReceipt> SendWithReceiptAsync(Packet packet, Identity destinationIdentity, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(destinationIdentity);
        var hash = packet.CalculateHash();
        var seconds = timeout?.TotalSeconds ?? 6 + (6 * (HopsTo(packet.DestinationHash.Span) ?? 0));
        var now = Links.UnixNow;
        var receipt = new PacketReceipt(hash, now, seconds);
        var key = Convert.ToHexStringLower(hash.AsSpan(0, Destination.HashLength));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_receipts.Remove(key, out var replaced))
            {
                replaced.Receipt.Status = PacketReceiptStatus.Failed;
                replaced.Receipt.Complete(false);
            }
            _receipts[key] = new PendingReceipt(receipt, destinationIdentity, now + seconds);
        }
        try
        {
            await SendAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (_receipts.TryGetValue(key, out var current) && ReferenceEquals(current.Receipt, receipt)) _receipts.Remove(key);
                receipt.Status = PacketReceiptStatus.Failed;
                receipt.Complete(false);
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
            try { prove = callback(received.Packet); }
            catch (Exception ex) { Log?.Invoke("Proof request callback failed: " + ex.Message); }
        }

        if (!prove || local.Decrypt(received.Packet.Payload.Span) is null) return;
        try { await ProveAsync(local, received).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log?.Invoke("Could not send packet proof: " + ex.Message); }
    }

    private bool TryValidateReceipt(ReceivedPacket received)
    {
        var packet = received.Packet;
        if (packet.DestinationType != DestinationType.Single) return false;
        var data = packet.Payload.Span;
        if (data.Length != ExplicitProofLength && data.Length != ImplicitProofLength) return false;
        PendingReceipt? pending;
        lock (_gate) _receipts.TryGetValue(Convert.ToHexStringLower(packet.DestinationHash.Span), out pending);
        if (pending is null) return false;

        var hash = pending.Receipt.Hash;
        if (data.Length == ExplicitProofLength && !data[..32].SequenceEqual(hash)) return false;
        if (!pending.Destination.Verify(data[^64..], hash)) return false;

        var receipt = pending.Receipt;
        lock (_gate)
        {
            var key = Convert.ToHexStringLower(packet.DestinationHash.Span);
            if (!_receipts.TryGetValue(key, out var current) || !ReferenceEquals(current, pending)) return false;
            _receipts.Remove(key);
            receipt.Status = PacketReceiptStatus.Delivered;
            receipt.Rtt = Links.UnixNow - receipt.SentAt;
            receipt.Complete(true);
        }
        try { receipt.Delivered?.Invoke(receipt); }
        catch (Exception ex) { Log?.Invoke("Delivery callback failed: " + ex.Message); }
        return true;
    }

    private void ExpireReceipts()
    {
        var now = Links.UnixNow;
        List<PendingReceipt> expired = [];
        lock (_gate)
        {
            foreach (var kv in _receipts.Where(kv => now > kv.Value.Deadline).ToArray())
            {
                _receipts.Remove(kv.Key);
                kv.Value.Receipt.Status = PacketReceiptStatus.Failed;
                kv.Value.Receipt.Complete(false);
                expired.Add(kv.Value);
            }
        }

        foreach (var pending in expired)
        {
            var receipt = pending.Receipt;
            try { receipt.TimedOut?.Invoke(receipt); }
            catch (Exception ex) { Log?.Invoke("Receipt timeout callback failed: " + ex.Message); }
        }
    }

    private void FailReceipts()
    {
        PendingReceipt[] pending;
        lock (_gate)
        {
            pending = [.. _receipts.Values];
            _receipts.Clear();
            foreach (var item in pending)
            {
                item.Receipt.Status = PacketReceiptStatus.Failed;
                item.Receipt.Complete(false);
            }
        }
        foreach (var item in pending)
        {
            try { item.Receipt.TimedOut?.Invoke(item.Receipt); }
            catch (Exception ex)
            {
                try { Log?.Invoke("Receipt cleanup callback failed: " + ex.Message); }
                catch { }
            }
        }
    }
}
