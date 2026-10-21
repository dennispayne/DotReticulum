using DotReticulum.Core;

namespace DotReticulum.Transport;

/// <summary>A single remembered destination (upstream <c>Identity.known_destinations</c> entry).</summary>
public sealed record KnownDestination(
    long RememberedUnix,
    byte[] PacketHash,
    byte[] PublicKey,
    byte[]? AppData,
    byte[]? Ratchet,
    long RatchetReceivedUnix,
    double LastUsedUnix = 0)
{
    public bool IsRetained => LastUsedUnix == -1;
    public double RememberedTimestamp { get; init; } = RememberedUnix;
    public double RatchetReceivedTimestamp { get; init; } = RatchetReceivedUnix;
}

/// <summary>An owned, detached destination-cache entry for persistence.</summary>
public sealed record KnownDestinationSnapshot(byte[] DestinationHash, KnownDestination Entry);

/// <summary>
/// Bounded, thread-safe cache of announced destinations: recalls identities (and their latest announced ratchet) by
/// destination hash or identity hash, like <c>Identity.recall</c>/<c>recall_app_data</c>/<c>get_ratchet</c>. When
/// <see cref="Capacity"/> is exceeded the least recently announced unretained entries are evicted.
/// </summary>
public sealed class KnownDestinations
{
    public const int DefaultCapacity = 16384;

    /// <summary>Upstream <c>Identity.RATCHET_EXPIRY</c> (30 days).</summary>
    public static readonly TimeSpan RatchetExpiry = TimeSpan.FromDays(30);
    public static readonly TimeSpan UnusedDestinationLinger = TimeSpan.FromMinutes(6);
    public static readonly TimeSpan UsedDestinationExpiry = TimeSpan.FromDays(7 * 1.25);

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
        if (packetHash.Length != 32) throw new ArgumentException("Packet hash must be 32 bytes.", nameof(packetHash));
        if (publicKey.Length != Identity.KeySize) throw new ArgumentException("Public key must be 64 bytes.", nameof(publicKey));
        if (ratchet is not null && ratchet.Length != Identity.RatchetSize) throw new ArgumentException("Ratchet must be 32 bytes.", nameof(ratchet));

        var now = NowUnix;
        var key = Key(destinationHash);
        lock (_gate)
        {
            _entries.TryGetValue(key, out var existing);
            if (existing is null && _entries.Count >= Capacity && _entries.Values.All(e => e.IsRetained))
                throw new InvalidOperationException("Known destination capacity is exhausted by retained entries.");
            var sameRatchet = ratchet is null || existing?.Ratchet is { } previous && previous.AsSpan().SequenceEqual(ratchet);
            var keepRatchet = ratchet is null ? existing?.Ratchet : ratchet.ToArray();
            var ratchetTime = sameRatchet ? existing?.RatchetReceivedUnix ?? 0 : now;
            _entries[key] = new KnownDestination(now, packetHash.ToArray(), publicKey.ToArray(), appData?.ToArray(), keepRatchet, ratchetTime, existing?.LastUsedUnix ?? 0)
            {
                RememberedTimestamp = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0,
                RatchetReceivedTimestamp = sameRatchet ? existing?.RatchetReceivedTimestamp ?? 0 : _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0,
            };
            if (_entries.Count > Capacity) Evict(key);
        }
    }

    /// <summary>The announced public key for a destination hash, or null.</summary>
    public byte[]? GetPublicKey(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) return _entries.TryGetValue(Key(destinationHash), out var e) ? e.PublicKey.ToArray() : null;
    }

    public KnownDestination? Get(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) return _entries.TryGetValue(Key(destinationHash), out var e) ? Clone(e) : null;
    }

    /// <summary>Upstream <c>Identity.recall</c>: a public-key-only identity for a destination hash (or, with <paramref name="fromIdentityHash"/>, an identity hash).</summary>
    public Identity? Recall(ReadOnlySpan<byte> targetHash, bool fromIdentityHash = false, bool markUsed = true)
    {
        byte[]? publicKey = null;
        lock (_gate)
        {
            if (fromIdentityHash)
            {
                foreach (var (hash, entry) in _entries)
                {
                    if (Identity.TruncatedHash(entry.PublicKey).AsSpan().SequenceEqual(targetHash))
                    {
                        publicKey = entry.PublicKey;
                        if (markUsed) MarkUsed(hash, entry);
                        break;
                    }
                }
            }
            else if (_entries.TryGetValue(Key(targetHash), out var e))
            {
                publicKey = e.PublicKey;
                if (markUsed) MarkUsed(Key(targetHash), e);
            }
        }

        return publicKey is null ? null : Identity.FromPublicKey(publicKey);
    }

    /// <summary>Upstream <c>Identity.recall_app_data</c>.</summary>
    public byte[]? RecallAppData(ReadOnlySpan<byte> destinationHash, bool markUsed = true)
    {
        lock (_gate)
        {
            var key = Key(destinationHash);
            if (!_entries.TryGetValue(key, out var entry)) return null;
            if (markUsed) MarkUsed(key, entry);
            return entry.AppData?.ToArray();
        }
    }

    private void MarkUsed(string key, KnownDestination entry)
    {
        if (!entry.IsRetained) _entries[key] = entry with { LastUsedUnix = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0 };
    }

    public bool UseDestination(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate)
        {
            var key = Key(destinationHash);
            if (!_entries.TryGetValue(key, out var entry) || entry.IsRetained) return false;
            MarkUsed(key, entry);
            return true;
        }
    }

    public bool RetainDestination(ReadOnlySpan<byte> destinationHash) => SetUsage(destinationHash, -1);

    public bool UnretainDestination(ReadOnlySpan<byte> destinationHash) =>
        SetUsage(destinationHash, _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0);

    private bool SetUsage(ReadOnlySpan<byte> hash, double usage)
    {
        lock (_gate)
        {
            var key = Key(hash);
            if (!_entries.TryGetValue(key, out var entry)) return false;
            _entries[key] = entry with { LastUsedUnix = usage };
            return true;
        }
    }

    public bool RetainIdentity(ReadOnlySpan<byte> identityHash)
    {
        if (identityHash.Length != Destination.HashLength) throw new ArgumentException("Identity hash must be 16 bytes.", nameof(identityHash));
        var retained = false;
        lock (_gate)
        {
            foreach (var key in _entries.Keys.ToArray())
            {
                var entry = _entries[key];
                if (!Identity.TruncatedHash(entry.PublicKey).AsSpan().SequenceEqual(identityHash)) continue;
                _entries[key] = entry with { LastUsedUnix = -1 };
                retained = true;
            }
        }
        return retained;
    }

    /// <summary>Upstream Identity.clean_known_destinations: paths and explicit retention protect destination data.
    /// Path lookups run outside this cache's lock to avoid lock inversion with the router.</summary>
    public int Cleanup(Func<byte[], bool>? hasPath = null)
    {
        var paths = ExportSnapshot().Where(e => hasPath?.Invoke(e.DestinationHash) == true)
            .Select(e => Key(e.DestinationHash)).ToHashSet(StringComparer.Ordinal);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        lock (_gate)
        {
            var stale = _entries.Where(kv => !kv.Value.IsRetained && !paths.Contains(kv.Key) &&
                (kv.Value.LastUsedUnix == 0 && now - kv.Value.RememberedTimestamp > UnusedDestinationLinger.TotalSeconds ||
                 now - kv.Value.LastUsedUnix > UsedDestinationExpiry.TotalSeconds)).Select(kv => kv.Key).ToArray();
            foreach (var key in stale) _entries.Remove(key);
            foreach (var key in _entries.Keys.ToArray())
            {
                var entry = _entries[key];
                if (entry.Ratchet is not null && now >= entry.RatchetReceivedTimestamp + RatchetExpiry.TotalSeconds)
                    _entries[key] = entry with { Ratchet = null, RatchetReceivedUnix = 0, RatchetReceivedTimestamp = 0 };
            }
            return stale.Length;
        }
    }

    public IReadOnlyList<KnownDestinationSnapshot> ExportSnapshot()
    {
        lock (_gate) return _entries.Select(kv => new KnownDestinationSnapshot(Convert.FromHexString(kv.Key), Clone(kv.Value))).ToArray();
    }

    /// <summary>Validates the entire snapshot before replacing live data. Expired public ratchets are discarded.</summary>
    public void RestoreSnapshot(IEnumerable<KnownDestinationSnapshot> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var restored = new Dictionary<string, KnownDestination>(StringComparer.Ordinal);
        foreach (var item in snapshot)
        {
            var entry = item.Entry;
            if (item.DestinationHash.Length != Destination.HashLength || entry.PacketHash.Length != 32 ||
                entry.PublicKey.Length != Identity.KeySize || entry.Ratchet is { Length: not Identity.RatchetSize } ||
                entry.RememberedUnix < 0 || entry.RatchetReceivedUnix < 0 || !double.IsFinite(entry.LastUsedUnix) ||
                entry.LastUsedUnix < 0 && entry.LastUsedUnix != -1 || !double.IsFinite(entry.RememberedTimestamp) ||
                entry.RememberedTimestamp < 0 || !double.IsFinite(entry.RatchetReceivedTimestamp) || entry.RatchetReceivedTimestamp < 0)
                throw new InvalidDataException("Invalid known destination snapshot entry.");
            using var identity = Identity.FromPublicKey(entry.PublicKey);
            var copy = Clone(entry);
            if (NowUnix >= copy.RatchetReceivedTimestamp + RatchetExpiry.TotalSeconds)
                copy = copy with { Ratchet = null, RatchetReceivedUnix = 0, RatchetReceivedTimestamp = 0 };
            if (!restored.TryAdd(Key(item.DestinationHash), copy))
                throw new InvalidDataException("Duplicate known destination snapshot hash.");
            if (restored.Count > Capacity) throw new InvalidDataException("Known destination snapshot exceeds capacity.");
        }
        lock (_gate)
        {
            _entries.Clear();
            foreach (var (key, entry) in restored) _entries.Add(key, entry);
        }
    }

    private static KnownDestination Clone(KnownDestination entry) => entry with
    {
        PacketHash = entry.PacketHash.ToArray(), PublicKey = entry.PublicKey.ToArray(),
        AppData = entry.AppData?.ToArray(), Ratchet = entry.Ratchet?.ToArray(),
    };

    /// <summary>The latest announced ratchet public key for the destination, unless it is older than <see cref="RatchetExpiry"/>.</summary>
    public byte[]? GetRatchet(ReadOnlySpan<byte> destinationHash)
    {
        var e = Get(destinationHash);
        if (e?.Ratchet is null) return null;
        return _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0 < e.RatchetReceivedTimestamp + RatchetExpiry.TotalSeconds ? e.Ratchet : null;
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
        var drop = _entries.Where(kv => kv.Key != keep && !kv.Value.IsRetained).OrderBy(kv => kv.Value.RememberedUnix)
            .Take(Math.Max(excess, Capacity / 10)).Select(kv => kv.Key).ToArray();
        foreach (var key in drop) _entries.Remove(key);
    }
}
