using DotReticulum.Core;

namespace DotReticulum.Transport;

public sealed partial class TransportRouter
{
    public int ActiveLinkTableCount
    {
        get { lock (_gate) return _linkTable.Values.Count(entry => entry.Validated); }
    }

    public bool ExpirePath(ReadOnlySpan<byte> destinationHash) => Engine.ExpirePath(destinationHash);

    /// <summary>Actual path-table entries; returned arrays are detached from the router's mutable state.</summary>
    public IReadOnlyList<TransportPathInfo> Paths => Engine.SnapshotPaths().Select(path => path.Info).ToArray();

    public byte[]? NextHop(ReadOnlySpan<byte> destinationHash)
    {
        if (Engine.GetNextHop(destinationHash) is { } nextHop) return nextHop.ToArray();
        lock (_gate) return _locals.ContainsKey(Convert.ToHexStringLower(destinationHash)) ? TransportId.ToArray() : null;
    }

    public bool DropPath(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) _discovery.Remove(Convert.ToHexStringLower(destinationHash));
        return Engine.RemovePath(destinationHash);
    }

    public int DropPathsVia(ReadOnlySpan<byte> transportIdentityHash)
    {
        var via = transportIdentityHash.ToArray();
        var paths = Paths.Where(path => path.ReceivedFrom.AsSpan().SequenceEqual(via)).ToArray();
        return paths.Count(path => DropPath(path.DestinationHash));
    }

    public int DropAllPaths() => Paths.Count(path => DropPath(path.DestinationHash));

    /// <summary>Captures real path announcements and packet-filter hashes; no tunnels or arbitrary cached packets are synthesized.</summary>
    public TransportStateSnapshot ExportState(Func<IPacketInterface, byte[]> interfaceHash)
    {
        ArgumentNullException.ThrowIfNull(interfaceHash);
        List<TransportPathState> paths = [];
        List<TransportPacketState> packets = [];
        foreach (var saved in Engine.SnapshotPaths())
        {
            var path = saved.Info;
            if (path.Interface is not { } iface || !_manager.Contains(iface)) continue;
            var hash = interfaceHash(iface);
            if (hash is not { Length: 32 }) throw new ArgumentException("Persistent interface hashes must be 32 bytes.", nameof(interfaceHash));
            paths.Add(new TransportPathState(path.DestinationHash, path.Timestamp, path.ReceivedFrom, path.Hops,
                path.Expires, saved.RandomBlobs, hash.ToArray(), path.PacketHash));
            packets.Add(new TransportPacketState(path.PacketHash.ToArray(), saved.Raw, true));
        }
        byte[][] hashes;
        lock (_gate) hashes = _hashes.Concat(_hashesPrev).Distinct().Select(Convert.FromBase64String).ToArray();
        return new TransportStateSnapshot(paths, hashes, packets);
    }

    /// <summary>
    /// Merges validated, unexpired announcement-backed paths without replaying handlers or rebroadcasts, and restores the bounded hash filter.
    /// Unknown interfaces, malformed records and blocked identities are ignored. Call before starting the dispatcher.
    /// </summary>
    public void ImportState(TransportStateSnapshot state, Func<byte[], IPacketInterface?> resolver)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(resolver);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_runCancellation is not null) throw new InvalidOperationException("Restore routing state before starting the dispatcher.");
        }
        var packets = new Dictionary<string, Packet>();
        foreach (var cached in state.CachedPackets.Take(65_536))
        {
            if (!cached.Announce || cached.PacketHash is not { Length: 32 } || cached.Raw is null) continue;
            if (!Packet.TryParse(cached.Raw.ToArray(), out var packet) || packet.PacketType != PacketType.Announce
                || !packet.CalculateHash().AsSpan().SequenceEqual(cached.PacketHash)) continue;
            packets.TryAdd(Convert.ToHexStringLower(cached.PacketHash), packet);
        }
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        foreach (var path in state.Paths.Take(65_536))
        {
            if (path.DestinationHash is not { Length: 16 } || path.ReceivedFrom is not { Length: 16 }
                || path.InterfaceHash is not { Length: 32 } || path.PacketHash is not { Length: 32 }
                || path.Hops is < 0 or > AnnounceEngine.MaxHops || !double.IsFinite(path.Timestamp) || path.Timestamp < 0
                || !double.IsFinite(path.Expires) || path.Expires <= now || path.Expires < path.Timestamp
                || path.RandomBlobs is null || path.RandomBlobs.Count is < 1 or > AnnounceEngine.MaxRandomBlobs
                || path.RandomBlobs.Any(blob => blob is not { Length: 10 })) continue;
            if (!packets.TryGetValue(Convert.ToHexStringLower(path.PacketHash), out var packet)
                || !packet.DestinationHash.Span.SequenceEqual(path.DestinationHash)) continue;
            var validation = Announce.Validate(packet);
            using var identity = validation.Identity;
            if (!validation.IsValid || identity is null || IsIdentityBlocked(identity.Hash)
                || !path.RandomBlobs.Any(blob => blob.AsSpan().SequenceEqual(validation.Data!.RandomHash))) continue;
            var expectedNextHop = packet.HeaderType == HeaderType.Header2 ? packet.TransportId : packet.DestinationHash;
            if (!path.ReceivedFrom.AsSpan().SequenceEqual(expectedNextHop.Span)) continue;
            var iface = resolver(path.InterfaceHash.ToArray());
            if (iface is null || !_manager.Contains(iface)) continue;
            if (packet.Hops + InboundHopIncrement(iface) != path.Hops) continue;
            var lifetime = GetInterfaceMode(iface) switch
            {
                InterfaceMode.AccessPoint => _options.AccessPointPathLifetime,
                InterfaceMode.Roaming => _options.RoamingPathLifetime,
                _ => _options.PathLifetime,
            };
            if (now > path.Timestamp + lifetime.TotalSeconds) continue;
            Engine.RestorePath(path, iface, packet, validation.Data!);
        }
        lock (_gate)
        {
            _hashes = [];
            _hashesPrev = [];
            var limit = Math.Max(1, _options.HashlistCapacity / 2);
            foreach (var hash in state.PacketHashes.Where(hash => hash is { Length: 32 }).Take(limit * 2))
            {
                var key = Convert.ToBase64String(hash);
                if (_hashes.Count < limit) _hashes.Add(key);
                else _hashesPrev.Add(key);
            }
        }
    }
}
