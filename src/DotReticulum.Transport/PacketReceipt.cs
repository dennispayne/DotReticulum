namespace DotReticulum.Transport;

public enum PacketReceiptStatus : byte
{
    Failed = 0x00,
    Sent = 0x01,
    Delivered = 0x02,
}

/// <summary>Tracks the proof of a packet sent over a link (upstream <c>PacketReceipt</c>, link case).</summary>
public sealed class PacketReceipt
{
    private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PacketReceipt(byte[] hash, double sentAt, double timeout)
    {
        Hash = hash;
        SentAt = sentAt;
        Timeout = timeout;
    }

    public byte[] Hash { get; }

    public PacketReceiptStatus Status { get; internal set; } = PacketReceiptStatus.Sent;

    public double SentAt { get; }

    public double Timeout { get; internal set; }

    /// <summary>Round-trip time between sending the packet and receiving its proof, once delivered.</summary>
    public double? Rtt { get; internal set; }

    public Action<PacketReceipt>? Delivered { get; set; }

    public Action<PacketReceipt>? TimedOut { get; set; }

    /// <summary>Completes with true when the proof arrived, false when the receipt timed out.</summary>
    public Task<bool> Completion => _done.Task;

    internal void Complete(bool delivered) => _done.TrySetResult(delivered);
}
