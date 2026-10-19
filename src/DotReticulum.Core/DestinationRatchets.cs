using System.Security.Cryptography;
using DotReticulum.Crypto;

namespace DotReticulum.Core;

/// <summary>
/// The ratchet key ring of a local destination, matching upstream <c>Destination.rotate_ratchets</c> semantics:
/// the newest private key is first, a new key is generated only when the rotation interval has elapsed
/// (the first rotation always happens), and at most <see cref="Retained"/> keys are kept. Persistence is the caller's job.
/// </summary>
public sealed class DestinationRatchets : IDisposable
{
    public const int DefaultRetained = 512;
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    private readonly List<byte[]> _keys = [];
    private readonly object _gate = new();
    private long _latestRotationUnix;
    private int _retained = DefaultRetained;

    /// <summary>Minimum time between rotations.</summary>
    public TimeSpan Interval { get; set; } = DefaultInterval;

    /// <summary>When true the owner must reject packets that were not encrypted to a ratchet key.</summary>
    public bool Enforce { get; set; }

    public int Retained
    {
        get => _retained;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            lock (_gate)
            {
                _retained = value;
                Trim();
            }
        }
    }

    public int Count
    {
        get { lock (_gate) return _keys.Count; }
    }

    /// <summary>Creates a ring pre-loaded with persisted private keys (newest first).</summary>
    public static DestinationRatchets FromPrivateKeys(IEnumerable<byte[]> privateKeysNewestFirst, long latestRotationUnix = 0)
    {
        var ratchets = new DestinationRatchets { _latestRotationUnix = latestRotationUnix };
        foreach (var key in privateKeysNewestFirst)
        {
            if (key.Length != Identity.RatchetSize) throw new ArgumentException("Ratchet private keys must be 32 bytes.", nameof(privateKeysNewestFirst));
            ratchets._keys.Add((byte[])key.Clone());
        }

        ratchets.Trim();
        return ratchets;
    }

    /// <summary>Upstream <c>Identity._get_ratchet_id</c>: SHA-256(ratchet public key)[..10].</summary>
    public static byte[] GetRatchetId(ReadOnlySpan<byte> ratchetPublicKey) =>
        SHA256.HashData(ratchetPublicKey)[..Destination.NameHashLength];

    /// <summary>Rotates if the interval has elapsed (or no key exists) and returns the newest ratchet public key.</summary>
    public byte[] Rotate(long nowUnix)
    {
        lock (_gate)
        {
            if (_keys.Count == 0 || nowUnix > _latestRotationUnix + (long)Interval.TotalSeconds)
            {
                _keys.Insert(0, X25519.GeneratePrivateKey());
                _latestRotationUnix = nowUnix;
                Trim();
            }

            return X25519.GetPublicKey(_keys[0]);
        }
    }

    /// <summary>The newest ratchet public key, or null if no ratchet has been generated yet.</summary>
    public byte[]? CurrentPublicKey
    {
        get
        {
            lock (_gate) return _keys.Count == 0 ? null : X25519.GetPublicKey(_keys[0]);
        }
    }

    /// <summary>A snapshot of the private keys, newest first. Handle as secret material.</summary>
    public IReadOnlyList<byte[]> ExportPrivateKeys()
    {
        lock (_gate) return _keys.Select(k => (byte[])k.Clone()).ToArray();
    }

    /// <summary>Decrypts for the owning identity trying each ratchet, honouring <see cref="Enforce"/> (upstream <c>Destination.decrypt</c>).</summary>
    public byte[]? Decrypt(Identity identity, ReadOnlySpan<byte> token)
    {
        IReadOnlyList<byte[]> keys;
        lock (_gate) keys = _keys.ToArray();
        return identity.Decrypt(token, keys, Enforce);
    }

    private void Trim()
    {
        while (_keys.Count > _retained)
        {
            CryptographicOperations.ZeroMemory(_keys[^1]);
            _keys.RemoveAt(_keys.Count - 1);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var key in _keys) CryptographicOperations.ZeroMemory(key);
            _keys.Clear();
        }
    }
}
