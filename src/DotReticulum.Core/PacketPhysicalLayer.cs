namespace DotReticulum.Core;

/// <summary>Local receive measurements, not authenticated peer data or part of the packet wire format.</summary>
public readonly record struct PacketPhysicalLayer(double? Rssi = null, double? Snr = null, double? Quality = null);

/// <summary>A frame and its measurements captured together before entering a bearer receive queue.</summary>
public readonly record struct ReceivedFrame(ReadOnlyMemory<byte> Data, PacketPhysicalLayer PhysicalLayer = default);

/// <summary>Optional atomic receive boundary for bearers that report packet-specific PHY measurements.</summary>
public interface IPacketInterfacePhysicalLayer : IPacketInterface
{
    IAsyncEnumerable<ReceivedFrame> ReceiveFramesAsync(CancellationToken cancellationToken);
}

internal sealed class PacketPhysicalLayerCache
{
    internal const int MaximumEntries = 512;
    private readonly object _gate = new();
    private readonly Queue<(string Hash, double Value)> _rssi = new();
    private readonly Queue<(string Hash, double Value)> _snr = new();
    private readonly Queue<(string Hash, double Value)> _quality = new();

    internal void Add(Packet packet)
    {
        var measurements = packet.PhysicalLayer;
        if (measurements == default) return;
        var hash = Convert.ToHexStringLower(packet.CalculateHash());
        lock (_gate)
        {
            Add(_rssi, hash, measurements.Rssi);
            Add(_snr, hash, measurements.Snr);
            Add(_quality, hash, measurements.Quality);
        }
    }

    internal PacketPhysicalLayer Get(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != 32) throw new ArgumentException("Packet hash must contain 32 bytes.", nameof(hash));
        var key = Convert.ToHexStringLower(hash);
        lock (_gate) return new(Find(_rssi, key), Find(_snr, key), Find(_quality, key));
    }

    private static void Add(Queue<(string Hash, double Value)> entries, string hash, double? value)
    {
        if (value is not { } number || !double.IsFinite(number)) return;
        entries.Enqueue((hash, number));
        while (entries.Count > MaximumEntries) entries.Dequeue();
    }

    private static double? Find(Queue<(string Hash, double Value)> entries, string hash)
    {
        foreach (var entry in entries)
            if (entry.Hash == hash) return entry.Value;
        return null;
    }
}
