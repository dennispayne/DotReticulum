using DotReticulum.Core;

namespace DotReticulum.Transport;

public enum PacketReceiptStatus : byte
{
    Failed = 0x00,
    Sent = 0x01,
    Delivered = 0x02,
    Culled = 0xFF,
}

/// <summary>Tracks proof, timeout and terminal metadata for an eligible data packet (upstream <c>PacketReceipt</c>).</summary>
public sealed class PacketReceipt
{
    private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _awaitingSend;

    internal PacketReceipt(byte[] hash, double sentAt, double timeout, bool awaitingSend = false)
    {
        Hash = hash;
        SentAt = sentAt;
        Timeout = timeout;
        TimeoutOrigin = sentAt;
        _awaitingSend = awaitingSend;
    }

    public byte[] Hash { get; }

    public PacketReceiptStatus Status { get; internal set; } = PacketReceiptStatus.Sent;

    /// <summary>Unix send timestamp; queued link receipts are updated by the outbound write.</summary>
    public double SentAt { get; private set; }

    public double Timeout { get; internal set; }
    internal double TimeoutOrigin { get; set; }
    internal Packet? SentPacket { get; init; }
    internal Link? ChannelOwner { get; init; }
    internal bool CoreCallbacks { get; set; }

    public byte[] TruncatedHash => Hash[..Destination.HashLength];

    public bool Proved { get; private set; }

    public double? ConcludedAt { get; private set; }

    public Packet? ProofPacket { get; private set; }

    /// <summary>Round-trip time between sending the packet and receiving its proof, once delivered.</summary>
    public double? Rtt { get; internal set; }

    public Action<PacketReceipt>? Delivered { get; set; }

    public Action<PacketReceipt>? TimedOut { get; set; }

    /// <summary>Completes with true when proven, false on timeout, culling or cancellation.</summary>
    public Task<bool> Completion => _done.Task;

    internal void Complete(bool delivered) => _done.TrySetResult(delivered);

    internal void MarkTransmitting(double unixNow)
    {
        lock (_done)
        {
            if (_awaitingSend && Status == PacketReceiptStatus.Sent) SentAt = unixNow;
        }
    }

    internal void StartProofDeadline(double unixNow, double now)
    {
        lock (_done)
        {
            if (!_awaitingSend || Status != PacketReceiptStatus.Sent) return;
            SentAt = unixNow;
            TimeoutOrigin = now;
            _awaitingSend = false;
        }
    }

    internal bool IsTimedOut(double now)
    {
        lock (_done) return !_awaitingSend && now > TimeoutOrigin + Timeout;
    }

    internal bool Conclude(PacketReceiptStatus status, double now, Packet? proof = null)
    {
        lock (_done)
        {
            if (Status != PacketReceiptStatus.Sent) return false;
            Status = status;
            ConcludedAt = now;
            ProofPacket = proof;
            Proved = status == PacketReceiptStatus.Delivered;
            if (Proved) Rtt = now - SentAt;
            Complete(Proved);
            return true;
        }
    }

    /// <summary>The reference's outbound eligibility rule; resource and link-control packets never create receipts.</summary>
    public static bool IsEligible(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return packet.PacketType == PacketType.Data && packet.DestinationType != DestinationType.Plain
            && packet.Context is not (>= LinkContext.Resource and <= LinkContext.ResourceRcl)
            && packet.Context < LinkContext.Keepalive;
    }
}
