using System.Security.Cryptography;
using DotReticulum.Crypto;

namespace DotReticulum.Core;

/// <summary>
/// The ratchet key ring of a local destination, matching upstream <c>Destination.rotate_ratchets</c> semantics:
/// the newest private key is first, a new key is generated only when the rotation interval has elapsed
/// (the first rotation always happens), and at most <see cref="Retained"/> keys are kept.
/// <see cref="EnablePersistence"/> explicitly binds mandatory save-before-publish persistence.
/// </summary>
public sealed class DestinationRatchets : IDisposable
{
    public const int DefaultRetained = 512;
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    private readonly List<byte[]> _keys = [];
    private readonly object _gate = new();
    private long _latestRotationUnix;
    private int _retained = DefaultRetained;
    private TimeSpan _interval = DefaultInterval;
    private bool _enforce;
    private bool _disposed;
    private bool _persisting;
    private Action<DestinationRatchetSnapshot>? _persistence;
    private IDisposable? _persistenceLifetime;

    /// <summary>Minimum time between rotations.</summary>
    public TimeSpan Interval
    {
        get { lock (_gate) return _interval; }
        set
        {
            if (value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate)
            {
                ThrowIfMutable();
                if (_interval == value) return;
                PersistCandidate(_keys, _latestRotationUnix, _retained, value, _enforce);
                _interval = value;
            }
        }
    }

    /// <summary>When true the owner must reject packets that were not encrypted to a ratchet key.</summary>
    public bool Enforce
    {
        get { lock (_gate) return _enforce; }
        set
        {
            lock (_gate)
            {
                ThrowIfMutable();
                if (_enforce == value) return;
                PersistCandidate(_keys, _latestRotationUnix, _retained, _interval, value);
                _enforce = value;
            }
        }
    }

    public long LatestRotationUnix { get { lock (_gate) return _latestRotationUnix; } }

    public int Retained
    {
        get { lock (_gate) return _retained; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            lock (_gate)
            {
                ThrowIfMutable();
                if (_retained == value) return;
                PersistCandidate(_keys, _latestRotationUnix, value, _interval, _enforce);
                _retained = value;
                Trim();
            }
        }
    }

    public int Count
    {
        get { lock (_gate) return _keys.Count; }
    }

    public bool HasPersistence { get { lock (_gate) return _persistence is not null; } }

    /// <summary>Explicitly enables mandatory persistence, analogous to upstream enable_ratchets(path).
    /// The current state is saved before binding succeeds; every subsequent rotation/policy change saves
    /// its candidate state before committing it. Save failures propagate and leave the old ring intact.
    /// The callback receives a borrowed secret snapshot, valid only during the call; clone it to retain it.
    /// The ring disposes that snapshot, and takes ownership of ownedLifetime only after successful binding.
    /// Callbacks run synchronously under the ring lock and must not mutate or dispose this ring.</summary>
    public void EnablePersistence(Action<DestinationRatchetSnapshot> persist, IDisposable? ownedLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            ThrowIfMutable();
            if (_persistence is not null) throw new InvalidOperationException("Ratchet persistence is already enabled.");
            InvokePersistence(persist, _keys, _latestRotationUnix, _retained, _interval, _enforce);
            _persistence = persist;
            _persistenceLifetime = ownedLifetime;
        }
    }

    /// <summary>Creates a ring pre-loaded with persisted private keys (newest first).</summary>
    public static DestinationRatchets FromPrivateKeys(IEnumerable<byte[]> privateKeysNewestFirst, long latestRotationUnix = 0, int retained = DefaultRetained)
    {
        ArgumentNullException.ThrowIfNull(privateKeysNewestFirst);
        ArgumentOutOfRangeException.ThrowIfNegative(latestRotationUnix);
        ArgumentOutOfRangeException.ThrowIfLessThan(retained, 1);
        var ratchets = new DestinationRatchets { _latestRotationUnix = latestRotationUnix, _retained = retained };
        try
        {
            foreach (var key in privateKeysNewestFirst)
            {
                if (key is not { Length: Identity.RatchetSize }) throw new ArgumentException("Ratchet private keys must be 32 bytes.", nameof(privateKeysNewestFirst));
                ratchets._keys.Add((byte[])key.Clone());
            }
            ratchets.Trim();
            return ratchets;
        }
        catch { ratchets.Dispose(); throw; }
    }

    /// <summary>Upstream <c>Identity._get_ratchet_id</c>: SHA-256(ratchet public key)[..10].</summary>
    public static byte[] GetRatchetId(ReadOnlySpan<byte> ratchetPublicKey) =>
        SHA256.HashData(ratchetPublicKey)[..Destination.NameHashLength];

    /// <summary>Rotates if the interval has elapsed (or no key exists) and returns the newest ratchet public key.</summary>
    public byte[] Rotate(long nowUnix)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowUnix);
        lock (_gate)
        {
            ThrowIfMutable();
            if (_keys.Count == 0 || nowUnix > _latestRotationUnix + _interval.TotalSeconds)
            {
                var candidate = X25519.GeneratePrivateKey();
                var committed = false;
                try
                {
                    PersistCandidate(_keys.Prepend(candidate), nowUnix, _retained, _interval, _enforce);
                    _keys.Insert(0, candidate);
                    _latestRotationUnix = nowUnix;
                    Trim();
                    committed = true;
                }
                finally { if (!committed) CryptographicOperations.ZeroMemory(candidate); }
            }

            return X25519.GetPublicKey(_keys[0]);
        }
    }

    /// <summary>The newest ratchet public key, or null if no ratchet has been generated yet.</summary>
    public byte[]? CurrentPublicKey
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _keys.Count == 0 ? null : X25519.GetPublicKey(_keys[0]);
            }
        }
    }

    /// <summary>A snapshot of the private keys, newest first. Handle as secret material.</summary>
    public IReadOnlyList<byte[]> ExportPrivateKeys()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _keys.Select(k => (byte[])k.Clone()).ToArray();
        }
    }

    /// <summary>Captures keys and rotation policy atomically. The caller must dispose the owned secret snapshot.</summary>
    public DestinationRatchetSnapshot ExportSnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return new DestinationRatchetSnapshot(_keys.Select(k => k.ToArray()).ToArray(), _latestRotationUnix, _retained, _interval, _enforce);
        }
    }

    public static DestinationRatchets FromSnapshot(DestinationRatchetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var copy = snapshot.Clone();
        var ring = new DestinationRatchets
        {
            _latestRotationUnix = copy.LatestRotationUnix, _retained = copy.Retained,
            _interval = copy.Interval, _enforce = copy.Enforce,
        };
        ring._keys.AddRange(copy.PrivateKeys.Select(k => k.ToArray()));
        ring.Trim();
        return ring;
    }

    /// <summary>Decrypts for the owning identity trying each ratchet, honouring <see cref="Enforce"/> (upstream <c>Destination.decrypt</c>).</summary>
    public byte[]? Decrypt(Identity identity, ReadOnlySpan<byte> token)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            ThrowIfDisposed();
            return identity.Decrypt(token, _keys, _enforce);
        }
    }

    private void Trim()
    {
        while (_keys.Count > _retained)
        {
            CryptographicOperations.ZeroMemory(_keys[^1]);
            _keys.RemoveAt(_keys.Count - 1);
        }
    }

    private void PersistCandidate(IEnumerable<byte[]> keys, long rotation, int retained, TimeSpan interval, bool enforce)
    {
        if (_persistence is { } persist) InvokePersistence(persist, keys, rotation, retained, interval, enforce);
    }

    private void InvokePersistence(Action<DestinationRatchetSnapshot> persist, IEnumerable<byte[]> keys,
        long rotation, int retained, TimeSpan interval, bool enforce)
    {
        using var snapshot = new DestinationRatchetSnapshot(keys.Take(retained).Select(k => k.ToArray()).ToArray(),
            rotation, retained, interval, enforce);
        _persisting = true;
        try { persist(snapshot); }
        finally { _persisting = false; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_persisting) throw new InvalidOperationException("Cannot dispose a ratchet ring from its persistence callback.");
            _disposed = true;
            foreach (var key in _keys) CryptographicOperations.ZeroMemory(key);
            _keys.Clear();
            _persistence = null;
            var lifetime = _persistenceLifetime;
            _persistenceLifetime = null;
            lifetime?.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfMutable()
    {
        ThrowIfDisposed();
        if (_persisting) throw new InvalidOperationException("Cannot mutate a ratchet ring from its persistence callback.");
    }
}

/// <summary>Owned secret snapshot. PrivateKeys are owned by this snapshot and must not be used after disposal.
/// Clone provides a synchronized independent copy for asynchronous persistence.</summary>
public sealed class DestinationRatchetSnapshot : IDisposable
{
    private readonly object _gate = new();
    private bool _disposed;
    public IReadOnlyList<byte[]> PrivateKeys { get; }
    public long LatestRotationUnix { get; }
    public int Retained { get; }
    public TimeSpan Interval { get; }
    public bool Enforce { get; }

    internal DestinationRatchetSnapshot(byte[][] keys, long latestRotationUnix, int retained, TimeSpan interval, bool enforce)
    {
        PrivateKeys = keys;
        LatestRotationUnix = latestRotationUnix;
        Retained = retained;
        Interval = interval;
        Enforce = enforce;
    }

    public DestinationRatchetSnapshot Clone()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new DestinationRatchetSnapshot(PrivateKeys.Select(k => k.ToArray()).ToArray(), LatestRotationUnix, Retained, Interval, Enforce);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var key in PrivateKeys) CryptographicOperations.ZeroMemory(key);
        }
    }
}
