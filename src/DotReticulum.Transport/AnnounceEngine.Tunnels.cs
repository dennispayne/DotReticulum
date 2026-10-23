using DotReticulum.Core;

namespace DotReticulum.Transport;

public sealed partial class AnnounceEngine
{
    internal SavedPath? SnapshotPath(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate)
        {
            if (!_paths.TryGetValue(Key(destinationHash), out var path)) return null;
            return new SavedPath(new TransportPathInfo(destinationHash.ToArray(), path.Timestamp, path.ReceivedFrom.ToArray(),
                path.Hops, path.Expires, path.Source, path.PacketHash.ToArray()),
                path.RandomBlobs.Select(blob => blob.ToArray()).ToArray(), path.Packet.ToArray());
        }
    }

    internal static long TunnelTimebase(IEnumerable<byte[]> blobs)
    {
        long latest = 0;
        foreach (var blob in blobs)
        {
            if (blob.Length != 10) continue;
            long emitted = 0;
            foreach (var value in blob.AsSpan(5)) emitted = (emitted << 8) | value;
            latest = Math.Max(latest, emitted);
        }
        return latest;
    }

    internal bool RestoreTunnelPath(SavedPath saved, IPacketInterface source)
    {
        var path = saved.Info;
        var key = Key(path.DestinationHash);
        if (!Packet.TryParse(saved.Raw, out var packet)) return false;
        var validation = Announce.Validate(packet);
        using var identity = validation.Identity;
        if (!validation.IsValid) return false;
        lock (_gate)
        {
            if (_local.Contains(key)) return false;
            if (_paths.TryGetValue(key, out var existing))
            {
                if (path.Hops > existing.Hops && Now <= existing.Expires) return false;
                if (TunnelTimebase(saved.RandomBlobs) < TunnelTimebase(existing.RandomBlobs)) return false;
            }
            else if (Now >= path.Expires) return false;
            var known = Known.GetPublicKey(path.DestinationHash);
            if (known is not null && !known.AsSpan().SequenceEqual(validation.Data!.PublicKey)) return false;
            Known.Remember(path.DestinationHash, path.PacketHash, validation.Data!.PublicKey, validation.Data.AppData, validation.Data.Ratchet);
            _paths[key] = new PathState(Now, path.ReceivedFrom.ToArray(),
                packet.HeaderType == HeaderType.Header2 ? packet.TransportId.ToArray() : null,
                path.Hops, path.Expires, saved.RandomBlobs.Select(blob => blob.ToArray()).ToList(), source,
                path.PacketHash.ToArray(), packet);
            Trim(_paths, entry => entry.Timestamp, key);
            return true;
        }
    }
}
