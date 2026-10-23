using System.Security.Cryptography;
using DotReticulum.Core;

namespace DotReticulum.Transport;

public sealed partial class TransportRouter
{
    public static readonly byte[] TunnelSynthesisHash = Destination.Create(DestinationType.Plain, "rnstransport", ["tunnel", "synthesize"]).Hash;
    public static readonly TimeSpan TunnelLifetime = TimeSpan.FromHours(8);
    private const int MaximumTunnels = 1024;
    private const int MaximumTunnelPaths = 65_536;
    private readonly Dictionary<string, TunnelEntry> _tunnels = new();
    private readonly Dictionary<IPacketInterface, string> _interfaceTunnels = new();

    private sealed class TunnelEntry(byte[] id, IPacketInterface? iface, double expires)
    {
        public byte[] Id { get; } = id;
        public IPacketInterface? Interface { get; set; } = iface;
        public double Expires { get; set; } = expires;
        public Dictionary<string, AnnounceEngine.SavedPath> Paths { get; } = new();
    }

    public int TunnelCount { get { lock (_gate) return _tunnels.Count; } }

    /// <summary>Signs the reference public-key/interface-hash/random-hash synthesis on this bearer only.</summary>
    public ValueTask SynthesizeTunnelAsync(IPacketInterface iface, byte[] interfaceHash, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(iface);
        if (interfaceHash is not { Length: 32 }) throw new ArgumentException("Tunnel interface hash must be 32 bytes.", nameof(interfaceHash));
        cancellationToken.ThrowIfCancellationRequested();
        var signed = (byte[])[.. TransportIdentity.ExportPublicKey(), .. interfaceHash, .. RandomNumberGenerator.GetBytes(16)];
        var packet = Packet.Create(PacketType.Data, DestinationType.Plain, TunnelSynthesisHash,
            (byte[])[.. signed, .. TransportIdentity.Sign(signed)]);
        return _manager.SendToAsync(packet, iface, cancellationToken);
    }

    private bool TryHandleTunnelSynthesis(ReceivedPacket received)
    {
        var packet = received.Packet;
        if (packet.PacketType != PacketType.Data || packet.DestinationType != DestinationType.Plain
            || !packet.DestinationHash.Span.SequenceEqual(TunnelSynthesisHash)) return false;
        var data = packet.Payload.Span;
        if (data.Length != 176)
        {
            Log?.Invoke("Rejected tunnel synthesis: expected 176 payload bytes.");
            return true;
        }
        try
        {
            using var identity = Identity.FromPublicKey(data[..64]);
            if (!identity.Verify(data[112..], data[..112]) || IsIdentityBlocked(identity.Hash))
            {
                Log?.Invoke("Rejected tunnel synthesis: invalid signature or blocked transport identity.");
                return true;
            }
            EstablishTunnel(SHA256.HashData(data[..96]), received.Source);
        }
        catch (ArgumentException error) { Log?.Invoke($"Rejected tunnel synthesis: {error.Message}"); }
        catch (CryptographicException error) { Log?.Invoke($"Rejected tunnel synthesis: {error.Message}"); }
        return true;
    }

    private void EstablishTunnel(byte[] id, IPacketInterface iface)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        lock (_gate)
        {
            var key = Convert.ToHexStringLower(id);
            if (_interfaceTunnels.TryGetValue(iface, out var previous) && previous != key
                && _tunnels.TryGetValue(previous, out var old)) old.Interface = null;
            _interfaceTunnels[iface] = key;
            if (!_tunnels.TryGetValue(key, out var entry))
            {
                entry = new TunnelEntry(id.ToArray(), iface, now + TunnelLifetime.TotalSeconds);
                _tunnels.Add(key, entry);
                TrimTunnels();
                return;
            }
            if (entry.Interface is { } previousInterface && !ReferenceEquals(previousInterface, iface))
                _interfaceTunnels.Remove(previousInterface);
            entry.Interface = iface;
            entry.Expires = now + TunnelLifetime.TotalSeconds;
            foreach (var path in entry.Paths.ToArray())
                if (!Engine.RestoreTunnelPath(path.Value, iface)) entry.Paths.Remove(path.Key);
        }
    }

    private void RememberTunnelPath(ReadOnlySpan<byte> destinationHash, IPacketInterface iface)
    {
        var key = Convert.ToHexStringLower(destinationHash);
        var saved = Engine.SnapshotPath(destinationHash);
        if (saved is null) return;
        lock (_gate)
        {
            if (!_interfaceTunnels.TryGetValue(iface, out var tunnelKey) || !_tunnels.TryGetValue(tunnelKey, out var tunnel)) return;
            tunnel.Paths[key] = saved with { Info = saved.Info with { Interface = null } };
            tunnel.Expires = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0 + TunnelLifetime.TotalSeconds;
            while (_tunnels.Values.Sum(value => value.Paths.Count) > MaximumTunnelPaths)
            {
                var oldest = _tunnels.Values.Where(value => value.Paths.Count != 0)
                    .SelectMany(value => value.Paths.Select(path => (Tunnel: value, Path: path)))
                    .MinBy(value => value.Path.Value.Info.Timestamp);
                oldest.Tunnel.Paths.Remove(oldest.Path.Key);
            }
        }
    }

    private void DetachTunnelInterface(IPacketInterface iface)
    {
        lock (_gate)
            if (_interfaceTunnels.Remove(iface, out var key) && _tunnels.TryGetValue(key, out var tunnel))
                tunnel.Interface = null;
    }

    private void TrimTunnels()
    {
        while (_tunnels.Count > MaximumTunnels)
        {
            var oldest = _tunnels.MinBy(pair => pair.Value.Expires);
            _tunnels.Remove(oldest.Key);
            if (oldest.Value.Interface is { } iface) _interfaceTunnels.Remove(iface);
            Log?.Invoke("Tunnel capacity reached; evicted oldest tunnel.");
        }
    }

    private void CullTunnels()
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        var current = Engine.SnapshotPaths().ToDictionary(path => Convert.ToHexStringLower(path.Info.DestinationHash));
        lock (_gate)
        {
            foreach (var (key, tunnel) in _tunnels.ToArray())
            {
                if (now > tunnel.Expires || tunnel.Expires > now + TunnelLifetime.TotalSeconds * 2)
                {
                    _tunnels.Remove(key);
                    if (tunnel.Interface is { } iface) _interfaceTunnels.Remove(iface);
                    continue;
                }
                if (tunnel.Interface is { } attached && !_manager.Contains(attached)) DetachTunnelInterface(attached);
                foreach (var (destination, saved) in tunnel.Paths.ToArray())
                    if (now > saved.Info.Timestamp + TunnelLifetime.TotalSeconds
                        || (current.TryGetValue(destination, out var active)
                            && AnnounceEngine.TunnelTimebase(active.RandomBlobs) > AnnounceEngine.TunnelTimebase(saved.RandomBlobs)))
                        tunnel.Paths.Remove(destination);
            }
        }
    }

    private IReadOnlyList<TransportTunnelState> ExportTunnels(Func<IPacketInterface, byte[]> interfaceHash,
        List<TransportPacketState> packets)
    {
        lock (_gate)
        {
            return _tunnels.Values.Select(tunnel =>
            {
                var paths = tunnel.Paths.Values.Select(saved =>
                {
                    var p = saved.Info;
                    packets.Add(new TransportPacketState(p.PacketHash.ToArray(), saved.Raw.ToArray(), true));
                    return new TransportPathState(p.DestinationHash.ToArray(), p.Timestamp, p.ReceivedFrom.ToArray(), p.Hops,
                        p.Expires, saved.RandomBlobs.Select(blob => blob.ToArray()).ToArray(), null, p.PacketHash.ToArray());
                }).ToArray();
                return new TransportTunnelState(tunnel.Id.ToArray(),
                    tunnel.Interface is { } iface && _manager.Contains(iface) ? interfaceHash(iface) : null, paths, tunnel.Expires);
            }).ToArray();
        }
    }

    private void ImportTunnels(IReadOnlyList<TransportTunnelState>? tunnels, Dictionary<string, Packet> packets)
    {
        if (tunnels is null) return;
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        var remaining = MaximumTunnelPaths;
        lock (_gate)
        {
            foreach (var tunnel in tunnels.Take(MaximumTunnels))
            {
                if (tunnel.TunnelId is not { Length: 32 } || !double.IsFinite(tunnel.Expires)
                    || tunnel.Expires <= now || tunnel.Expires > now + TunnelLifetime.TotalSeconds * 2)
                {
                    Log?.Invoke("Rejected persisted tunnel: malformed identity or expired/excessive expiry.");
                    continue;
                }
                var entry = new TunnelEntry(tunnel.TunnelId.ToArray(), null, tunnel.Expires);
                foreach (var path in tunnel.Paths.Take(remaining))
                {
                    if (path.DestinationHash is not { Length: 16 } || path.ReceivedFrom is not { Length: 16 }
                        || path.PacketHash is not { Length: 32 } || path.Hops is < 0 or >= Packet.HopLimit
                        || !double.IsFinite(path.Timestamp) || path.Timestamp < 0
                        || now > path.Timestamp + TunnelLifetime.TotalSeconds || !double.IsFinite(path.Expires)
                        || path.Expires <= now || path.Expires < path.Timestamp
                        || path.RandomBlobs is null || path.RandomBlobs.Count is < 1 or > AnnounceEngine.MaxRandomBlobs
                        || path.RandomBlobs.Any(blob => blob is not { Length: 10 }))
                    {
                        Log?.Invoke("Rejected persisted tunnel path: malformed fields or stale lifetime.");
                        continue;
                    }
                    if (!packets.TryGetValue(Convert.ToHexStringLower(path.PacketHash), out var packet)
                        || !packet.DestinationHash.Span.SequenceEqual(path.DestinationHash) || packet.Hops + 1 != path.Hops)
                    {
                        Log?.Invoke("Rejected persisted tunnel path: missing or inconsistent cached announcement.");
                        continue;
                    }
                    var validation = Announce.Validate(packet);
                    using var identity = validation.Identity;
                    if (!validation.IsValid || identity is null || IsIdentityBlocked(identity.Hash)
                        || !path.RandomBlobs.Any(blob => blob.AsSpan().SequenceEqual(validation.Data!.RandomHash)))
                    {
                        Log?.Invoke("Rejected persisted tunnel path: invalid/blocked announcement or random-hash mismatch.");
                        continue;
                    }
                    var nextHop = packet.HeaderType == HeaderType.Header2 ? packet.TransportId : packet.DestinationHash;
                    if (!nextHop.Span.SequenceEqual(path.ReceivedFrom))
                    {
                        Log?.Invoke("Rejected persisted tunnel path: next-hop mismatch.");
                        continue;
                    }
                    entry.Paths[Convert.ToHexStringLower(path.DestinationHash)] = new AnnounceEngine.SavedPath(
                        new TransportPathInfo(path.DestinationHash.ToArray(), path.Timestamp, path.ReceivedFrom.ToArray(),
                            path.Hops, path.Expires, null, path.PacketHash.ToArray()),
                        path.RandomBlobs.Select(blob => blob.ToArray()).ToArray(), packet.ToArray());
                }
                remaining -= entry.Paths.Count;
                if (entry.Paths.Count != 0) _tunnels[Convert.ToHexStringLower(entry.Id)] = entry;
            }
        }
    }
}
