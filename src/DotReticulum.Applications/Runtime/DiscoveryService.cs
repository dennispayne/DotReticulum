using System.Security.Cryptography;
using DotReticulum.Core;

namespace DotReticulum.Applications.Runtime;

/// <summary>A connected, owned bearer lease. Disposal must deregister the interface before releasing its resources.</summary>
public sealed class DiscoveryConnection(
    string name, Func<bool> isOnline, Func<ValueTask> detach, Func<bool>? isAttached = null) : IAsyncDisposable
{
    private int _disposed;
    public string Name { get; } = name;
    public bool IsOnline => Volatile.Read(ref _disposed) == 0 && isOnline();
    public bool IsAttached => Volatile.Read(ref _disposed) == 0 && (isAttached?.Invoke() ?? true);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await detach().ConfigureAwait(false);
    }
}

public sealed record DiscoveryConnectRequest(DiscoveryRecord Advertisement, string Name, string? Mode,
    int Gravity, bool? AnnouncesToInternal, int ConfiguredBitrate = 5_000_000);

/// <summary>
/// Bounded discovery state, source/stamp policy, persistence and owned autoconnect lifecycle.
/// The host registers the discovery announce destination and supplies bearer attachment callbacks.
/// </summary>
public sealed class DiscoveryService : IAsyncDisposable
{
    private readonly string _storage;
    private readonly DiscoveryOptions _options;
    private readonly TimeProvider _clock;
    private readonly Identity? _networkIdentity;
    private readonly Func<ReadOnlyMemory<byte>, bool>? _blocked;
    private readonly Action<string>? _diagnostic;
    private readonly Func<DiscoveryConnectRequest, CancellationToken, ValueTask<DiscoveryConnection?>>? _connect;
    private readonly Func<DiscoveryRecord, bool>? _endpointExists;
    private readonly Func<IReadOnlyCollection<string>>? _interfaceNames;
    private readonly Func<CancellationToken, ValueTask<IReadOnlyList<DiscoveryConnection>>>? _bootstrap;
    private readonly Action<DiscoveryRecord>? _discovered;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, DiscoveryRecord> _records = [];
    private readonly Dictionary<string, (DiscoveryConnection Connection, double? DownSince, DiscoveryRecord Source)> _connections = [];
    private readonly List<DiscoveryConnection> _bootstraps = [];
    private readonly Dictionary<string, DiscoveryRecord?> _validationCache = [];
    private readonly HashSet<string> _sources;
    private Task? _job;
    private bool _started;
    private bool _disposed;
    private double Now => _clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000d;

    public DiscoveryService(string storageDirectory, DiscoveryOptions options,
        Func<DiscoveryConnectRequest, CancellationToken, ValueTask<DiscoveryConnection?>>? connect = null,
        TimeProvider? timeProvider = null, Identity? networkIdentity = null,
        Func<ReadOnlyMemory<byte>, bool>? isBlocked = null, Action<string>? diagnostic = null,
        Func<DiscoveryRecord, bool>? endpointExists = null, Func<IReadOnlyCollection<string>>? interfaceNames = null,
        Func<CancellationToken, ValueTask<IReadOnlyList<DiscoveryConnection>>>? bootstrap = null,
        Action<DiscoveryRecord>? discovered = null)
    {
        if (options.Capacity is < 1 or > 65536 || options.MaxAutoConnected is < 0 or > 1024
            || options.RequiredStampValue is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.AllowedSources.Any(s => s.Length != 16)) throw new ArgumentException("Discovery sources must be identity hashes.");
        _storage = Path.GetFullPath(storageDirectory);
        _options = options;
        _sources = options.AllowedSources.Select(Convert.ToHexStringLower).ToHashSet(StringComparer.Ordinal);
        _connect = connect;
        _clock = timeProvider ?? TimeProvider.System;
        _networkIdentity = networkIdentity;
        _blocked = isBlocked;
        _diagnostic = diagnostic;
        _endpointExists = endpointExists;
        _interfaceNames = interfaceNames;
        _bootstrap = bootstrap;
        _discovered = discovered;
        Directory.CreateDirectory(_storage);
        Reload();
    }

    private bool Authorized(string source) => (_sources.Count == 0 || _sources.Contains(source)) && !Blocked(source);
    private bool Blocked(string identity) => _blocked?.Invoke(Convert.FromHexString(identity)) == true;
    private void Report(string message)
    {
        try
        {
            if (_diagnostic is null) System.Diagnostics.Trace.TraceWarning(message);
            else _diagnostic(message);
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"Discovery diagnostic observer failed: {ex.Message}; original error: {message}"); }
    }

    private void Reload()
    {
        foreach (var file in Directory.EnumerateFiles(_storage).Where(p => Path.GetFileName(p).Length == 64).Take(_options.Capacity * 2))
        {
            try
            {
                if (new FileInfo(file).Length > DiscoveryCodec.MaximumPayload * 2) throw new FormatException("Oversized discovery state.");
                if (MsgPack.Unpack(File.ReadAllBytes(file)) is not Dictionary<object, object?> map) throw new FormatException("Invalid discovery state.");
                using var source = Identity.FromPublicKey((byte[])map["public_key"]!);
                if (!Authorized(source.HexHash)) { File.Delete(file); continue; }
                var body = ((byte[])map["packed"]!).Concat((byte[])map["stamp"]!).ToArray();
                var record = DiscoveryCodec.Decode(new byte[] { 0 }.Concat(body).ToArray(), source,
                    (int)MsgPack.ToInt64(map["hops"]), _options.RequiredStampValue, received: MsgPack.ToDouble(map["received"]));
                record = record with
                {
                    Discovered = MsgPack.ToDouble(map["discovered"]),
                    LastHeard = MsgPack.ToDouble(map["last_heard"]),
                    HeardCount = MsgPack.ToInt64(map["heard_count"]),
                };
                if (!double.IsFinite(record.LastHeard) || !double.IsFinite(record.Discovered)
                    || !double.IsFinite(record.Received) || record.HeardCount < 0)
                    throw new FormatException("Invalid discovery persistence timestamps or count.");
                if (Path.GetFileName(file) != record.DiscoveryHash) throw new FormatException("Discovery filename/hash mismatch.");
                if (Now - record.LastHeard > 7 * 86400 || Blocked(record.TransportId)) { File.Delete(file); continue; }
                if (_records.Count == _options.Capacity) break;
                _records[record.DiscoveryHash] = record;
            }
            catch (Exception ex) when (ex is IOException or FormatException or ArgumentException or InvalidCastException or KeyNotFoundException or OverflowException)
            { Report($"Could not load discovery state {Path.GetFileName(file)}: {ex.Message}"); }
        }
    }

    public async ValueTask<bool> ReceiveAsync(Identity announcedIdentity, ReadOnlyMemory<byte> appData, int hops = 0,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !Authorized(announcedIdentity.HexHash)) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        // Match upstream's single in-flight expensive stamp validator; never accumulate queued announces.
        if (!await _gate.WaitAsync(0, linked.Token).ConfigureAwait(false)) return false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked.Token.ThrowIfCancellationRequested();
            if (appData.Length > DiscoveryCodec.MaximumPayload) return false;
            var cacheKey = Convert.ToHexStringLower(SHA256.HashData(appData.Span));
            DiscoveryRecord? record;
            if (_validationCache.TryGetValue(cacheKey, out var cached))
            {
                if (cached is null) return false;
                record = cached with { NetworkId = announcedIdentity.HexHash, Received = Now, Hops = hops };
            }
            else
            {
                try { record = DiscoveryCodec.Decode(appData.Span, announcedIdentity, hops, _options.RequiredStampValue, _networkIdentity, Now); }
                catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or InvalidOperationException)
                {
                    Cache(cacheKey, null);
                    Report($"Rejected discovery advertisement: {ex.Message}");
                    return false;
                }
                Cache(cacheKey, record);
            }
            if (!DiscoveryCodec.DiscoverableTypes.Contains(record.InterfaceType) || Blocked(record.TransportId)) return false;
            if (record.InterfaceType is not ("BackboneInterface" or "TCPServerInterface"))
                Report($"Discovered {record.InterfaceType}; automatic attachment is unsupported for this bearer.");
            if (_records.TryGetValue(record.DiscoveryHash, out var previous))
                record = record with { Discovered = previous.Discovered, LastHeard = Now, HeardCount = previous.HeardCount + 1 };
            else
            {
                record = record with { LastHeard = Now };
                if (_records.Count >= _options.Capacity)
                {
                    var oldest = _records.Values.MinBy(r => r.LastHeard)!;
                    Remove(oldest.DiscoveryHash);
                }
            }
            Persist(record, announcedIdentity.ExportPublicKey());
            _records[record.DiscoveryHash] = record;
            await ConnectAsync(record, linked.Token).ConfigureAwait(false);
            try { _discovered?.Invoke(Clone(record)); }
            catch (Exception ex) { Report($"Discovery observer failed: {ex.Message}"); }
            return true;
        }
        finally { _gate.Release(); }
    }

    private void Cache(string key, DiscoveryRecord? record)
    {
        if (_validationCache.Count >= 2048) _validationCache.Remove(_validationCache.Keys.First());
        _validationCache[key] = record;
    }

    private void Persist(DiscoveryRecord record, byte[] publicKey)
    {
        var map = new Dictionary<object, object?>
        {
            ["public_key"] = publicKey, ["packed"] = record.Packed, ["stamp"] = record.Stamp,
            ["hops"] = record.Hops, ["received"] = record.Received, ["discovered"] = record.Discovered,
            ["last_heard"] = record.LastHeard, ["heard_count"] = record.HeardCount,
        };
        var path = Path.Combine(_storage, record.DiscoveryHash);
        var pending = path + ".pending";
        try
        {
            using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(pending, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(MsgPack.Pack(map));
                stream.Flush(true);
            }
            File.Move(pending, path, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private void Remove(string hash)
    {
        File.Delete(Path.Combine(_storage, hash));
        _records.Remove(hash);
    }

    public async ValueTask<IReadOnlyList<DiscoveryRecord>> SnapshotAsync(bool onlyAvailable = false, bool onlyTransport = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Snapshot(onlyAvailable, onlyTransport); }
        finally { _gate.Release(); }
    }

    private IReadOnlyList<DiscoveryRecord> Snapshot(bool onlyAvailable = false, bool onlyTransport = false)
    {
        var result = new List<DiscoveryRecord>();
        foreach (var record in _records.Values.ToArray())
        {
            var age = Now - record.LastHeard;
            if (age > 7 * 86400 || !Authorized(record.NetworkId) || Blocked(record.TransportId)) { Remove(record.DiscoveryHash); continue; }
            var item = record with { Status = age > 3 * 86400 ? "stale" : age > 86400 ? "unknown" : "available" };
            if ((!onlyAvailable || item.Status == "available") && (!onlyTransport || item.Transport)) result.Add(Clone(item));
        }
        return result.OrderByDescending(r => r.StatusCode).ThenByDescending(r => r.StampValue).ThenByDescending(r => r.LastHeard).ToArray();
    }

    private static DiscoveryRecord Clone(DiscoveryRecord record) => record with
    {
        Stamp = record.Stamp.ToArray(), Packed = record.Packed.ToArray(), OperatorAddress = record.OperatorAddress?.ToArray(),
    };

    private async ValueTask ConnectAsync(DiscoveryRecord record, CancellationToken cancellationToken)
    {
        if (_connect is null || _options.MaxAutoConnected <= _connections.Count
            || !DiscoveryCodec.CanAutoConnect(record, _options.AllowUnverifiedImplementations)
            || !Authorized(record.NetworkId) || Blocked(record.TransportId)
            || _connections.ContainsKey(record.EndpointHash) || _endpointExists?.Invoke(record) == true) return;
        var names = new HashSet<string>(_interfaceNames?.Invoke() ?? []);
        foreach (var connection in _connections.Values) names.Add(connection.Connection.Name);
        var name = record.Name;
        var sequence = 2;
        while (names.Contains(name)) name = $"{record.Name} ({sequence++})";
        DiscoveryConnection? connectionLease = null;
        try
        {
            connectionLease = await _connect(new(Clone(record), name, _options.InterfaceMode,
                _options.InterfaceGravity, _options.AnnouncesToInternal), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (connectionLease is not null)
            {
                _connections.Add(record.EndpointHash, (connectionLease, null, record));
                connectionLease = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { Report($"Discovery connection to {record.ReachableOn}:{record.Port} failed: {ex.Message}"); }
        finally { if (connectionLease is not null) await DetachAsync(connectionLease).ConfigureAwait(false); }
    }

    public async ValueTask TickAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var all = Snapshot(onlyTransport: true);
            if (!_options.Enabled) return;
            foreach (var bootstrap in _bootstraps.ToArray())
            {
                try { if (bootstrap.IsAttached) continue; }
                catch (Exception ex) { Report($"Discovery bootstrap monitor failed for {bootstrap.Name}: {ex.Message}"); }
                _bootstraps.Remove(bootstrap);
                await DetachAsync(bootstrap).ConfigureAwait(false);
            }
            if (!_started)
            {
                foreach (var record in all) await ConnectAsync(record, linked.Token).ConfigureAwait(false);
                _started = true;
            }
            var online = 0;
            var detached = new List<string>();
            foreach (var (hash, state) in _connections.ToArray())
            {
                try
                {
                    if (!state.Connection.IsAttached || !Authorized(state.Source.NetworkId) || Blocked(state.Source.TransportId))
                    { detached.Add(hash); continue; }
                    if (state.Connection.IsOnline) { online++; _connections[hash] = (state.Connection, null, state.Source); }
                    else if (state.DownSince is null) _connections[hash] = (state.Connection, Now, state.Source);
                    else if (Now - state.DownSince.Value >= 12) detached.Add(hash);
                }
                catch (Exception ex) { Report($"Discovery connection monitor failed: {ex.Message}"); detached.Add(hash); }
            }
            if (_options.MaxAutoConnected > 0 && online >= _options.MaxAutoConnected)
            {
                foreach (var connection in _bootstraps) await DetachAsync(connection).ConfigureAwait(false);
                _bootstraps.Clear();
            }
            if (_options.MaxAutoConnected > 0 && online == 0 && _bootstraps.Count == 0 && _bootstrap is not null)
            {
                try
                {
                    var restored = await _bootstrap(linked.Token).ConfigureAwait(false);
                    if (restored.Count > 1024)
                    {
                        foreach (var lease in restored) await DetachAsync(lease).ConfigureAwait(false);
                        throw new InvalidOperationException("Bootstrap capacity exceeded.");
                    }
                    _bootstraps.AddRange(restored);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception ex) { Report($"Could not restore discovery bootstrap interfaces: {ex.Message}"); }
            }
            var freeSlots = Math.Max(0, _options.MaxAutoConnected - _connections.Count);
            if (freeSlots > _options.MaxAutoConnected / 4)
            {
                var available = all.Where(r => r.Status == "available").ToArray();
                if (available.Length > 0) await ConnectAsync(available[RandomNumberGenerator.GetInt32(available.Length)], linked.Token).ConfigureAwait(false);
            }
            foreach (var hash in detached)
            {
                var connection = _connections[hash].Connection;
                _connections.Remove(hash);
                await DetachAsync(connection).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    public void RegisterBootstrap(DiscoveryConnection connection)
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bootstraps.Count >= 1024) throw new InvalidOperationException("Bootstrap capacity exceeded.");
            _bootstraps.Add(connection);
        }
        finally { _gate.Release(); }
    }

    public void Start()
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _job ??= RunAsync();
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _clock);
        try
        {
            do
            {
                try { await TickAsync(_lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception ex) { Report($"Discovery monitor failed: {ex.Message}"); }
            }
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async ValueTask DetachAsync(DiscoveryConnection connection)
    {
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Report($"Discovery detach failed for {connection.Name}: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var connection in _connections.Values) await DetachAsync(connection.Connection).ConfigureAwait(false);
            foreach (var connection in _bootstraps) await DetachAsync(connection).ConfigureAwait(false);
            _connections.Clear();
            _bootstraps.Clear();
        }
        finally { _gate.Release(); }
        if (_job is not null) await _job.ConfigureAwait(false);
    }
}
