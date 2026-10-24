using System.Security.Cryptography;
using DotReticulum.Crypto;

namespace DotReticulum.Core;

public enum DestinationDirection : byte
{
    In = 0x11,
    Out = 0x12,
}

/// <summary>Endpoint state, separate from the name/address codec. Supplied identities and ratchets remain caller-owned.</summary>
public sealed class DestinationEndpoint : IDisposable
{
    public const int DefaultPathResponseCapacity = 256;
    public const int MaximumPathResponseTagBytes = Destination.HashLength;
    public static readonly TimeSpan PathResponseWindow = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, CachedAnnounce> _responses = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly bool _ownsIdentity;
    private byte[]? _groupKey;
    private Token? _groupToken;
    private byte[]? _defaultAppData;
    private Func<byte[]?>? _appDataProvider;
    private bool _disposed;

    public DestinationEndpoint(Identity? identity, Destination destination, DestinationDirection direction = DestinationDirection.In,
        TimeProvider? timeProvider = null, int pathResponseCapacity = DefaultPathResponseCapacity)
        : this(identity, destination, direction, timeProvider, pathResponseCapacity, false) { }

    private DestinationEndpoint(Identity? identity, Destination destination, DestinationDirection direction,
        TimeProvider? timeProvider, int pathResponseCapacity, bool ownsIdentity)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        ArgumentOutOfRangeException.ThrowIfLessThan(pathResponseCapacity, 1);
        if (destination.Type == DestinationType.Plain && identity is not null)
            throw new ArgumentException("A plain destination cannot hold an identity.", nameof(identity));
        if (destination.Type != DestinationType.Plain && identity is null)
            throw new ArgumentException("An identity-bound endpoint requires an identity.", nameof(identity));
        if (identity is not null && (destination.IdentityHash is null || !identity.Hash.AsSpan().SequenceEqual(destination.IdentityHash)))
            throw new ArgumentException("Destination and identity do not match.", nameof(identity));
        Identity = identity;
        Destination = destination;
        Direction = direction;
        _time = timeProvider ?? TimeProvider.System;
        PathResponseCapacity = pathResponseCapacity;
        _ownsIdentity = ownsIdentity;
    }

    public Identity? Identity { get; }
    public Destination Destination { get; }
    public DestinationDirection Direction { get; }
    public bool AcceptLinks { get; set; } = true;
    public DestinationRatchets? Ratchets { get; set; }
    public int PathResponseCapacity { get; }
    public int CachedPathResponses { get { lock (_gate) return _responses.Count; } }

    /// <summary>Matches upstream's generated incoming identity and identity-hash aspect when no identity is supplied.</summary>
    public static DestinationEndpoint Create(DestinationType type, DestinationDirection direction, string appName,
        ReadOnlySpan<string> aspects, Identity? identity = null, TimeProvider? timeProvider = null,
        int pathResponseCapacity = DefaultPathResponseCapacity)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!Enum.IsDefined(type) || type == DestinationType.Link) throw new ArgumentOutOfRangeException(nameof(type));
        var owns = identity is null && type != DestinationType.Plain && direction == DestinationDirection.In;
        if (owns) identity = Identity.Generate();
        try
        {
            if (type != DestinationType.Plain && identity is null)
                throw new ArgumentException("An outgoing encrypted endpoint requires an identity.", nameof(identity));
            string[] names = owns ? [.. aspects.ToArray(), identity!.HexHash] : aspects.ToArray();
            var destination = identity is null
                ? Destination.Create(type, appName, names)
                : Destination.Create(type, appName, names, identity);
            return new DestinationEndpoint(identity, destination, direction, timeProvider, pathResponseCapacity, owns);
        }
        catch
        {
            if (owns) identity?.Dispose();
            throw;
        }
    }

    public void SetDefaultAppData(byte[]? data)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _defaultAppData = data?.ToArray();
            _appDataProvider = null;
        }
    }

    public void SetDefaultAppDataProvider(Func<byte[]?>? provider)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _defaultAppData = null;
            _appDataProvider = provider;
        }
    }

    /// <summary>Null selects the current default; an empty array explicitly suppresses it. Cache hits do not invoke providers.
    /// The cache is bounded; oldest tags are evicted before their 30-second expiry if capacity is exhausted.
    /// Unlike upstream's cached-ratchet flag bug, cached payloads preserve their original context flag.</summary>
    public Packet CreateAnnounce(byte[]? appData = null, bool pathResponse = false, byte[]? tag = null,
        ReadOnlySpan<byte> randomHash = default)
    {
        if (tag is { Length: > MaximumPathResponseTagBytes })
            throw new ArgumentException("A path response tag cannot exceed 16 bytes.", nameof(tag));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (Direction != DestinationDirection.In) throw new InvalidOperationException("Only incoming destinations can announce.");
            if (Destination.Type != DestinationType.Single) throw new InvalidOperationException("Only SINGLE destinations can announce.");
            var now = _time.GetUtcNow();
            foreach (var key in _responses.Where(entry => now > entry.Value.Created + PathResponseWindow).Select(entry => entry.Key).ToArray())
                _responses.Remove(key);
            var cacheKey = tag is null ? "-" : Convert.ToHexStringLower(tag);
            if (pathResponse && tag is not null && _responses.TryGetValue(cacheKey, out var cached))
                return Packet.Create(PacketType.Announce, DestinationType.Single, Destination.Hash, cached.Payload,
                    context: Announce.PathResponseContext, contextFlag: cached.ContextFlag);

            // Monitor locks are reentrant: a provider may update defaults, but must not resurrect a disposed endpoint.
            var selected = appData ?? _appDataProvider?.Invoke() ?? _defaultAppData;
            ThrowIfDisposed();
            var packet = Announce.Create(Identity!, Destination, selected, Ratchets, pathResponse, now.ToUnixTimeSeconds(), randomHash);
            if (!_responses.ContainsKey(cacheKey) && _responses.Count >= PathResponseCapacity)
                _responses.Remove(_responses.MinBy(entry => entry.Value.Created).Key);
            _responses[cacheKey] = new CachedAnnounce(now, packet.Payload.ToArray(), packet.ContextFlag);
            return packet;
        }
    }

    public void CreateGroupKey()
    {
        var key = Token.GenerateKey();
        try { LoadGroupKey(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public void LoadGroupKey(ReadOnlySpan<byte> key)
    {
        lock (_gate)
        {
            EnsureGroup();
            // Validate before replacing the current key, so failed imports do not disable an endpoint.
            var replacement = new Token(key);
            _groupToken?.Dispose();
            if (_groupKey is not null) CryptographicOperations.ZeroMemory(_groupKey);
            _groupKey = key.ToArray();
            _groupToken = replacement;
        }
    }

    public byte[] ExportGroupKey()
    {
        lock (_gate)
        {
            EnsureGroup();
            return _groupKey?.ToArray() ?? throw new InvalidOperationException("No GROUP key has been created or loaded.");
        }
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> ratchetPublicKey = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return Destination.Type switch
            {
                DestinationType.Plain => plaintext.ToArray(),
                DestinationType.Group => (_groupToken ?? throw new InvalidOperationException("No GROUP key has been created or loaded.")).Encrypt(plaintext),
                _ => Identity!.Encrypt(plaintext, ratchetPublicKey),
            };
        }
    }

    public byte[]? Decrypt(ReadOnlySpan<byte> ciphertext)
        => DecryptWithMetadata(ciphertext)?.Plaintext;

    /// <summary>Per-packet plaintext and selected-ratchet metadata; no cross-packet mutable result state.</summary>
    public IdentityDecryptionResult? DecryptWithMetadata(ReadOnlySpan<byte> ciphertext)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (Destination.Type == DestinationType.Plain) return new IdentityDecryptionResult(ciphertext.ToArray(), null);
            if (Destination.Type == DestinationType.Single)
                return Ratchets is { } ratchets ? ratchets.DecryptWithMetadata(Identity!, ciphertext) : Identity!.DecryptWithMetadata(ciphertext);
            var token = _groupToken ?? throw new InvalidOperationException("No GROUP key has been created or loaded.");
            return token.TryDecrypt(ciphertext, out var plaintext) ? new IdentityDecryptionResult(plaintext, null) : null;
        }
    }

    public byte[]? Sign(ReadOnlySpan<byte> message)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return Destination.Type == DestinationType.Single ? Identity!.Sign(message) : null;
        }
    }

    private void EnsureGroup()
    {
        ThrowIfDisposed();
        if (Destination.Type != DestinationType.Group) throw new InvalidOperationException("Only GROUP destinations hold symmetric keys.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _groupToken?.Dispose();
            _groupToken = null;
            if (_groupKey is not null) CryptographicOperations.ZeroMemory(_groupKey);
            _groupKey = null;
            _defaultAppData = null;
            _appDataProvider = null;
            _responses.Clear();
            if (_ownsIdentity) Identity?.Dispose();
        }
    }

    private sealed record CachedAnnounce(DateTimeOffset Created, byte[] Payload, bool ContextFlag);
}
