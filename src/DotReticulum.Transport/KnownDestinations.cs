using DotReticulum.Core;

namespace DotReticulum.Transport;

/// <summary>A single remembered destination (upstream <c>Identity.known_destinations</c> entry).</summary>
public sealed record KnownDestination(
    long RememberedUnix,
    byte[] PacketHash,
    byte[] PublicKey,
    byte[]? AppData,
    byte[]? Ratchet,
    long RatchetReceivedUnix);

/// <summary>
/// Bounded, thread-safe cache of announced destinations: recalls identities (and their latest announced ratchet) by
/// destination hash or identity hash, like <c>Identity.recall</c>/<c>recall_app_data</c>/<c>get_ratchet</c>. When
/// <see cref="Capacity"/> is exceeded the least recently announced entries are evicted. In-memory only.
/// </summary>
public sealed class KnownDestinations
{
    public const int DefaultCapacity = 16384;

    /// <summary>Upstream <c>Identity.RATCHET_EXPIRY</c> (30 days).</summary>
    public static readonly TimeSpan RatchetExpiry = TimeSpan.FromDays(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, KnownDestination> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public KnownDestinations(int capacity = DefaultCapacity, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _time = timeProvider ?? TimeProvider.System;
    }

    public int Capacity { get; }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    private long NowUnix => _time.GetUtcNow().ToUnixTimeSeconds();

    private static string Key(ReadOnlySpan<byte> hash) => Convert.ToHexStringLower(hash);

    /// <summary>Upstream <c>Identity.remember</c> (+ <c>_remember_ratchet</c> when a ratchet was announced).</summary>
    public void Remember(ReadOnlySpan<byte> destinationHash, ReadOnlySpan<byte> packetHash, ReadOnlySpan<byte> publicKey, byte[]? appData, byte[]? ratchet = null)
    {
        if (destinationHash.Length != Destination.HashLength) throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destinationHash));
        if (publicKey.Length != Identity.KeySize) throw new ArgumentException("Public key must be 64 bytes.", nameof(publicKey));
        if (ratchet is not null && ratchet.Length != Identity.RatchetSize) throw new ArgumentException("Ratchet must be 32 bytes.", nameof(ratchet));

        var now = NowUnix;
        var key = Key(destinationHash);
        lock (_gate)
        {
            _entries.TryGetValue(key, out var existing);
            var keepRatchet = ratchet is null ? existing?.Ratchet : ratchet;
            var ratchetTime = ratchet is null ? existing?.RatchetReceivedUnix ?? 0 : now;
            _entries[key] = new KnownDestination(now, packetHash.ToArray(), publicKey.ToArray(), appData, keepRatchet, ratchetTime);
            if (_entries.Count > Capacity) Evict(key);
        }
    }

    /// <summary>The announced public key for a destination hash, or null.</summary>
    public byte[]? GetPublicKey(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) return _entries.TryGetValue(Key(destinationHash), out var e) ? e.PublicKey : null;
    }

    public KnownDestination? Get(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) return _entries.TryGetValue(Key(destinationHash), out var e) ? e : null;
    }

    /// <summary>Upstream <c>Identity.recall</c>: a public-key-only identity for a destination hash (or, with <paramref name="fromIdentityHash"/>, an identity hash).</summary>
    public Identity? Recall(ReadOnlySpan<byte> targetHash, bool fromIdentityHash = false)
    {
        byte[]? publicKey = null;
        lock (_gate)
        {
            if (fromIdentityHash)
            {
                foreach (var entry in _entries.Values)
                {
                    if (Identity.TruncatedHash(entry.PublicKey).AsSpan().SequenceEqual(targetHash))
                    {
                        publicKey = entry.PublicKey;
                        break;
                    }
                }
            }
            else if (_entries.TryGetValue(Key(targetHash), out var e))
            {
                publicKey = e.PublicKey;
            }
        }

        return publicKey is null ? null : Identity.FromPublicKey(publicKey);
    }

    /// <summary>Upstream <c>Identity.recall_app_data</c>.</summary>
    public byte[]? RecallAppData(ReadOnlySpan<byte> destinationHash) => Get(destinationHash)?.AppData;

    /// <summary>The latest announced ratchet public key for the destination, unless it is older than <see cref="RatchetExpiry"/>.</summary>
    public byte[]? GetRatchet(ReadOnlySpan<byte> destinationHash)
    {
        var e = Get(destinationHash);
        if (e?.Ratchet is null) return null;
        return NowUnix < e.RatchetReceivedUnix + (long)RatchetExpiry.TotalSeconds ? e.Ratchet : null;
    }

    /// <summary>Encrypts to a remembered destination, using its latest announced ratchet when it has one (upstream <c>Destination.encrypt</c>). Null if unknown.</summary>
    public byte[]? Encrypt(ReadOnlySpan<byte> destinationHash, ReadOnlySpan<byte> plaintext)
    {
        using var identity = Recall(destinationHash);
        return identity?.Encrypt(plaintext, GetRatchet(destinationHash));
    }

    public bool Forget(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) return _entries.Remove(Key(destinationHash));
    }

    private void Evict(string keep)
    {
        var excess = _entries.Count - Capacity;
        var drop = _entries.Where(kv => kv.Key != keep).OrderBy(kv => kv.Value.RememberedUnix)
            .Take(Math.Max(excess, Capacity / 10)).Select(kv => kv.Key).ToArray();
        foreach (var key in drop) _entries.Remove(key);
    }
}
