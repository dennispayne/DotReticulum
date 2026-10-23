using System.Security.Cryptography;
using DotReticulum.Core;

namespace DotReticulum.Transport;

public sealed partial class TransportRouter
{
    // Only Resource.prove(force_cache=True) is implemented here, not an invented general packet cache.
    private const int MaximumResourceProofs = 1024;
    private const double ResourceProofLifetime = 24 * 60 * 60;
    private readonly Dictionary<string, CachedResourceProof> _resourceProofCache = [];
    private sealed record CachedResourceProof(Packet Packet, double CachedAt);

    public int CachedResourceProofCount
    {
        get { lock (_gate) { CullResourceProofs(); return _resourceProofCache.Count; } }
    }

    internal void CacheResourceProof(Packet packet, double? cachedAt = null)
    {
        if (!IsResourceProof(packet)) throw new ArgumentException("Only complete resource proof packets can be force cached.", nameof(packet));
        var now = Links.UnixNow;
        var timestamp = cachedAt ?? now;
        if (!double.IsFinite(timestamp) || timestamp < 0 || timestamp > now || now > timestamp + ResourceProofLifetime) return;
        var copy = Packet.Parse(packet.ToArray());
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CullResourceProofs();
            var key = Convert.ToHexStringLower(copy.CalculateHash());
            _resourceProofCache.Remove(key);
            while (_resourceProofCache.Count >= MaximumResourceProofs) _resourceProofCache.Remove(_resourceProofCache.Keys.First());
            _resourceProofCache[key] = new CachedResourceProof(copy, timestamp);
        }
    }

    private static bool IsResourceProof(Packet packet) => packet.PacketType == PacketType.Proof
        && packet.DestinationType == DestinationType.Link && packet.Context == LinkContext.ResourcePrf && packet.Payload.Length == 64;

    private void CullResourceProofs()
    {
        var now = Links.UnixNow;
        foreach (var key in _resourceProofCache.Where(item => now > item.Value.CachedAt + ResourceProofLifetime).Select(item => item.Key).ToArray())
            _resourceProofCache.Remove(key);
    }

    internal Packet? GetResourceProof(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> linkId)
    {
        if (hash.Length != 32 || linkId.Length != Destination.HashLength) return null;
        lock (_gate)
        {
            CullResourceProofs();
            if (!_resourceProofCache.TryGetValue(Convert.ToHexStringLower(hash), out var entry)
                || !entry.Packet.DestinationHash.Span.SequenceEqual(linkId)) return null;
            return entry.Packet;
        }
    }

    /// <summary>Forced resource proofs persisted alongside the reference-compatible packet cache.</summary>
    public IReadOnlyList<TransportPacketState> CaptureProofCache()
    {
        lock (_gate)
        {
            CullResourceProofs();
            return _resourceProofCache.Values.Select(entry => new TransportPacketState(entry.Packet.CalculateHash(), entry.Packet.ToArray(), false, entry.CachedAt)).ToArray();
        }
    }

    /// <summary>Rejects corrupt or unrelated cached packets rather than treating this as a general replay facility.</summary>
    public void RestoreProofCache(IEnumerable<TransportPacketState> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        foreach (var state in packets.Where(state => state is not null && !state.Announce).Take(MaximumResourceProofs))
        {
            if (state.PacketHash is not { Length: 32 } || !Packet.TryParse(state.Raw, out var packet)
                || !IsResourceProof(packet) || !CryptographicOperations.FixedTimeEquals(state.PacketHash, packet.CalculateHash())) continue;
            CacheResourceProof(packet, state.CachedAt);
        }
    }

    internal async ValueTask<bool> HandleResourceCacheRequestAsync(ReceivedPacket received, CancellationToken cancellationToken)
    {
        var request = received.Packet;
        if (request.PacketType != PacketType.Data || request.Context != LinkContext.CacheRequest
            || request.DestinationType != DestinationType.Link || request.Payload.Length != 32) return false;
        var proof = GetResourceProof(request.Payload.Span, request.DestinationHash.Span);
        if (proof is null) return false;
        var local = Links.Find(request.DestinationHash.Span);
        if (local is not null && !ReferenceEquals(local.AttachedInterface, received.Source)) return false;
        if (!_manager.Contains(received.Source)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        await _manager.SendToAsync(proof, received.Source, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
