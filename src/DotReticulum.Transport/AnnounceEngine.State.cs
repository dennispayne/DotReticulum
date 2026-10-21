using DotReticulum.Core;

namespace DotReticulum.Transport;

public sealed partial class AnnounceEngine
{
    public IReadOnlyList<AnnounceRateInfo> GetRateTable() => Rates;

    /// <summary>Upstream get_rate_table fields, copied from the actual announce limiter.</summary>
    public IReadOnlyList<AnnounceRateInfo> Rates
    {
        get
        {
            lock (_gate) return _rates.Select(kv => new AnnounceRateInfo(Convert.FromHexString(kv.Key),
                kv.Value.Last, kv.Value.Violations, kv.Value.BlockedUntil, kv.Value.Timestamps.ToArray())).ToArray();
        }
    }

    /// <summary>Clears native pending rebroadcast and path-response queues; returns the actual number removed.</summary>
    public int ClearPendingRebroadcasts()
    {
        lock (_gate)
        {
            var count = _table.Count + _responses.Count;
            _table.Clear();
            _responses.Clear();
            return count;
        }
    }

    /// <summary>Upstream expire_path: retains the entry with a zero timestamp until the next table cull.</summary>
    public bool ExpirePath(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate)
        {
            if (!_paths.TryGetValue(Key(destinationHash), out var path)) return false;
            path.Timestamp = 0;
            return true;
        }
    }

    internal sealed record SavedPath(TransportPathInfo Info, IReadOnlyList<byte[]> RandomBlobs, byte[] Raw);

    internal IReadOnlyList<SavedPath> SnapshotPaths()
    {
        lock (_gate)
        {
            return _paths.Select(kv => new SavedPath(
                new TransportPathInfo(Convert.FromHexString(kv.Key), kv.Value.Timestamp, kv.Value.ReceivedFrom.ToArray(),
                    kv.Value.Hops, kv.Value.Expires, kv.Value.Source, kv.Value.PacketHash.ToArray()),
                kv.Value.RandomBlobs.Select(blob => blob.ToArray()).ToArray(), kv.Value.Packet.ToArray())).ToArray();
        }
    }

    internal void RestorePath(TransportPathState path, IPacketInterface source, Packet packet, AnnounceData data)
    {
        var key = Key(path.DestinationHash);
        lock (_gate)
        {
            if (_local.Contains(key) || (_paths.TryGetValue(key, out var existing) && existing.Timestamp >= path.Timestamp)) return;
            var known = Known.GetPublicKey(path.DestinationHash);
            if (known is not null && !known.AsSpan().SequenceEqual(data.PublicKey)) return;
            Known.Remember(path.DestinationHash, path.PacketHash, data.PublicKey, data.AppData, data.Ratchet);
            _paths[key] = new PathState(path.Timestamp, path.ReceivedFrom.ToArray(),
                packet.HeaderType == HeaderType.Header2 ? packet.TransportId.ToArray() : null,
                path.Hops, path.Expires, path.RandomBlobs.Select(blob => blob.ToArray()).ToList(), source,
                path.PacketHash.ToArray(), packet);
            Trim(_paths, entry => entry.Timestamp, key);
        }
    }
}
