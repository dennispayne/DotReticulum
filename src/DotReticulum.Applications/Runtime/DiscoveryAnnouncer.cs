using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Runtime;

/// <summary>One due interface per 60-second job; dynamic metadata is supplied by the owning interface.</summary>
public sealed class DiscoveryAnnouncer : IAsyncDisposable
{
    private sealed class Entry(string name, TimeSpan interval, Func<CancellationToken, ValueTask<DiscoveryAdvertisement>> read, string? location)
    {
        public string Name { get; } = name;
        public TimeSpan Interval { get; } = interval;
        public Func<CancellationToken, ValueTask<DiscoveryAdvertisement>> Read { get; } = read;
        public string? Location { get; } = location;
        public DateTimeOffset Last { get; set; } = DateTimeOffset.UnixEpoch;
    }
    private readonly Identity? _networkIdentity;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _publish;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _diagnostic;
    private readonly Func<string, bool>? _authorizeExecutable;
    private readonly Func<DiscoveryAdvertisement, string?, CancellationToken, ValueTask<DiscoveryAdvertisement>>? _metadataResolver;
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, byte[]> _stamps = [];
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private DateTimeOffset _lastJob = DateTimeOffset.MinValue;
    private bool _disposed;

    public DiscoveryAnnouncer(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> publish,
        Identity? networkIdentity = null, TimeProvider? timeProvider = null, Action<string>? diagnostic = null,
        Func<string, bool>? authorizeExecutable = null,
        Func<DiscoveryAdvertisement, string?, CancellationToken, ValueTask<DiscoveryAdvertisement>>? metadataResolver = null)
    {
        _publish = publish;
        _networkIdentity = networkIdentity;
        _clock = timeProvider ?? TimeProvider.System;
        _diagnostic = diagnostic;
        _authorizeExecutable = authorizeExecutable;
        _metadataResolver = metadataResolver;
    }

    public static LocalDestination RegisterDestination(Identity transportIdentity, Identity? networkIdentity,
        Action<LocalDestination> register)
    {
        var identity = networkIdentity ?? transportIdentity;
        var local = new LocalDestination(identity, Destination.Create(DestinationType.Single, "rnstransport", ["discovery", "interface"], identity));
        register(local);
        return local;
    }

    public void Register(string name, TimeSpan interval, Func<CancellationToken, ValueTask<DiscoveryAdvertisement>> metadata, string? location = null)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Count >= 1024) throw new InvalidOperationException("Discovery advertisement capacity exceeded.");
            if (_entries.Any(e => e.Name == name)) throw new ArgumentException("Duplicate discovery interface name.", nameof(name));
            _entries.Add(new(name, interval, metadata, location));
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<bool> UnregisterAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.RemoveAll(e => e.Name == name) != 0;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask TickAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _clock.GetUtcNow();
            if (now - _lastJob < TimeSpan.FromSeconds(60)) return;
            _lastJob = now;
            var selected = _entries.Where(e => now > e.Last + e.Interval).OrderBy(e => e.Last).FirstOrDefault();
            if (selected is null) return;
            selected.Last = now;
            try
            {
                var metadata = await selected.Read(linked.Token).ConfigureAwait(false);
                metadata = _metadataResolver is null
                    ? await DiscoveryMetadata.ResolveAsync(metadata, selected.Location, linked.Token,
                        authorizeExecutable: _authorizeExecutable).ConfigureAwait(false)
                    : await _metadataResolver(metadata, selected.Location, linked.Token).ConfigureAwait(false);
                if (metadata.Encrypt && _networkIdentity is null) throw new InvalidOperationException("Discovery encryption requires a network identity.");
                var packed = DiscoveryCodec.PackAdvertisement(metadata);
                var hash = SHA256.HashData(packed);
                var key = Convert.ToHexStringLower(hash) + ":" + metadata.StampValue;
                if (!_stamps.TryGetValue(key, out var stamp))
                {
                    stamp = await Task.Run(() => DiscoveryCodec.GenerateStamp(hash, metadata.StampValue, linked.Token), linked.Token).ConfigureAwait(false);
                    if (_stamps.Count >= 2048) _stamps.Remove(_stamps.Keys.First());
                    _stamps[key] = stamp;
                }
                var body = packed.Concat(stamp).ToArray();
                try
                {
                    var payload = new[] { metadata.Encrypt ? (byte)2 : (byte)0 }
                        .Concat(metadata.Encrypt ? _networkIdentity!.Encrypt(body) : body).ToArray();
                    await _publish(payload, linked.Token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(body); }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var message = $"Discovery advertisement for {selected.Name} failed: {ex.Message}";
                try
                {
                    if (_diagnostic is null) System.Diagnostics.Trace.TraceWarning(message);
                    else _diagnostic(message);
                }
                catch (Exception observer) { System.Diagnostics.Trace.TraceError($"Discovery diagnostic observer failed: {observer.Message}; original error: {message}"); }
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; _entries.Clear(); _stamps.Clear(); }
        finally { _gate.Release(); }
    }
}
