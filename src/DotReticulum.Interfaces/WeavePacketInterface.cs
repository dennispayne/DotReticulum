using System.Buffers.Binary;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>Serial WDCL switch with separately routable endpoint peers.</summary>
public sealed class WeavePacketInterface : ISpawningPacketInterface, IReconnectingPacketInterface, IPacketInterfaceDiagnostics
{
    public const int DefaultIfacSize = 16;
    public const int FixedHardwareMtu = 1024;
    private sealed class Session(Stream stream, IDisposable? owner, CancellationToken stop)
    {
        public Stream Stream { get; } = stream;
        public IDisposable? Owner { get; } = owner;
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(stop);
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Reader { get; set; } = Task.CompletedTask;
        public Task? Closing;
        public byte[]? RemotePublicKey;
        public byte[]? RemoteId;
        public volatile bool Online;
    }

    private readonly Func<CancellationToken, ValueTask<(Stream Stream, IDisposable? Owner)>> _open;
    private readonly WeaveOptions _options;
    private readonly Identity _identity;
    private readonly TimeProvider _clock;
    private readonly byte[] _hostPublic, _hostId;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wire = new(1, 1), _capacity = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, WeavePeer> _peers = [];
    private readonly Queue<(byte[] Hash, DateTimeOffset Time)> _duplicates = new();
    private readonly Channel<ReadOnlyMemory<byte>> _parent = Channel.CreateBounded<ReadOnlyMemory<byte>>(1);
    private readonly Dictionary<string, (byte Load, DateTimeOffset Time)> _tasks = [];
    private Session? _session;
    private Task? _start, _worker, _jobs, _dispose;
    private TaskCompletionSource? _transmitsDrained;
    private byte[]? _endpoint, _display;
    private bool _disposed;
    private bool _displayEnabled;
    private int _receiving, _pendingFrames, _pendingBytes;
    private long _rxb, _txb, _wireRx, _wireTx, _drops, _observerErrors, _reconnects;
    private string? _lastError;
    private byte _cpu;
    private uint _memoryFree, _memoryTotal;

    public WeavePacketInterface(string portName, WeaveOptions? options = null)
        : this(SerialFactory(portName), options, null, null) { }

    /// <summary>The factory transfers ownership of each new duplex stream to this interface.</summary>
    public WeavePacketInterface(Func<CancellationToken, ValueTask<Stream>> streamFactory, WeaveOptions? options = null,
        Identity? switchIdentity = null, TimeProvider? timeProvider = null)
        : this(Adapt(streamFactory), options, switchIdentity, timeProvider) { }

    private WeavePacketInterface(Func<CancellationToken, ValueTask<(Stream, IDisposable?)>> open, WeaveOptions? options,
        Identity? identity, TimeProvider? timeProvider)
    {
        _open = open;
        _options = (options ?? new()).Validate();
        _clock = timeProvider ?? TimeProvider.System;
        // Copy rather than borrow: disposal never destroys the caller's identity.
        if (identity is null) _identity = Identity.Generate();
        else
        {
            var key = identity.ExportPrivateKey();
            try { _identity = Identity.FromPrivateKey(key); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        _hostPublic = _identity.ExportPublicKey()[32..];
        _hostId = _hostPublic[^4..];
    }

    private static Func<CancellationToken, ValueTask<(Stream, IDisposable?)>> Adapt(Func<CancellationToken, ValueTask<Stream>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return async token => (await factory(token).ConfigureAwait(false), null);
    }

    private static Func<CancellationToken, ValueTask<(Stream, IDisposable?)>> SerialFactory(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (portName.Contains("://", StringComparison.Ordinal)) throw new NotSupportedException("Weave requires an explicitly configured serial device or owned native stream adapter.");
        return token =>
        {
            token.ThrowIfCancellationRequested();
            var port = NativeSerialPort.Create(portName, 3_000_000, Parity.None, 8, StopBits.One, 250, 1000);
            try { port.Open(); token.ThrowIfCancellationRequested(); return ValueTask.FromResult<(Stream, IDisposable?)>((port.BaseStream, port)); }
            catch { port.Dispose(); throw; }
        };
    }

    public event Action<IPacketInterface>? ChildConnected;
    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<Exception>? Faulted;
    public event Action<WeaveLogFrame>? LogReceived;
    public event Action<ReadOnlyMemory<byte>>? DisplayUpdated;
    public bool SendsThroughChildren => true;
    public long Bitrate => _options.ConfiguredBitrate;
    public int HardwareMtu => FixedHardwareMtu;
    public bool SupportsMtuDiscovery => true;
    public bool IsConnected { get { lock (_gate) return !_disposed && _session?.Online == true; } }
    public byte[] HostSwitchId => _hostId.ToArray();
    public byte[]? SwitchId { get { lock (_gate) return _session?.RemoteId?.ToArray(); } }
    public byte[]? EndpointId { get { lock (_gate) return _endpoint?.ToArray(); } }
    public byte[]? DisplayBuffer { get { lock (_gate) return _display?.ToArray(); } }
    public IReadOnlyList<WeavePeer> Peers { get { lock (_gate) return _peers.Values.ToArray(); } }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_start is not null) throw new InvalidOperationException("The Weave interface has already been started.");
            _start = StartCoreAsync(cancellationToken);
            return new(_start);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            var session = await OpenAsync(linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                _worker = RunAsync(session);
                _jobs = PeerJobsAsync();
            }
            Notify(Connected);
        }
        catch (Exception error)
        {
            Report(error);
            Session? session;
            lock (_gate) { session = _session; _session = null; }
            if (session is not null) await CloseAsync(session).ConfigureAwait(false);
            _parent.Writer.TryComplete(error);
            throw;
        }
    }

    private async Task<Session> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (stream, owner) = await _open(cancellationToken).ConfigureAwait(false);
        var session = new Session(stream, owner, _stop.Token);
        lock (_gate) _session = session;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stream.CanRead || !stream.CanWrite) throw new IOException("WDCL requires a duplex stream.");
            session.Reader = ReadAsync(session);
            await WriteAsync(session, WeaveProtocol.Frame([255, 255, 255, 255], WeaveProtocol.Discover, _hostId), cancellationToken).ConfigureAwait(false);
            await session.Ready.Task.WaitAsync(_options.HandshakeTimeout, _clock, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch { await CloseAsync(session).ConfigureAwait(false); throw; }
    }

    private async Task RunAsync(Session session)
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await session.Reader.ConfigureAwait(false); }
            catch (Exception error) { if (!_stop.IsCancellationRequested) Report(error); }
            await CloseAsync(session).ConfigureAwait(false);
            ClearPeers();
            if (_stop.IsCancellationRequested) break;
            Notify(Disconnected);
            if (!_options.Reconnect) { _parent.Writer.TryComplete(new IOException("WDCL device disconnected.")); break; }
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_options.ReconnectDelay, _clock, _stop.Token).ConfigureAwait(false);
                    session = await OpenAsync(_stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _reconnects);
                    Notify(Connected);
                    break;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception error) { Report(error); }
            }
        }
    }

    private async Task ReadAsync(Session session)
    {
        var buffer = new byte[1500];
        var decoder = new WeaveProtocol.Decoder();
        try
        {
            while (true)
            {
                var count = await session.Stream.ReadAsync(buffer, session.Stop.Token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("WDCL serial device disconnected.");
                var frames = new List<byte[]>();
                var previousDrops = decoder.DroppedFrames;
                decoder.Feed(buffer.AsSpan(0, count), frames.Add);
                Interlocked.Add(ref _drops, decoder.DroppedFrames - previousDrops);
                foreach (var frame in frames)
                {
                    Interlocked.Add(ref _wireRx, frame.Length);
                    await ProcessAsync(session, frame).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) { session.Ready.TrySetException(error); throw; }
        finally { session.Online = false; }
    }

    private async ValueTask ProcessAsync(Session session, byte[] frame)
    {
        switch (frame[4])
        {
            case WeaveProtocol.Discover:
                if (!WeaveProtocol.ValidateDiscovery(frame, out var remote)) { Drop(); return; }
                session.RemotePublicKey = remote;
                session.RemoteId = remote[^4..];
                var payload = new byte[96];
                _hostPublic.CopyTo(payload, 0);
                _identity.Sign(session.RemoteId).CopyTo(payload, 32);
                await WriteAsync(session, WeaveProtocol.Frame(session.RemoteId, WeaveProtocol.Connect, payload), session.Stop.Token).ConfigureAwait(false);
                break;
            case WeaveProtocol.EndpointPacket:
                if (!session.Online || frame.Length <= 13 || frame.Length - 13 > WeaveProtocol.MaximumPacketLength ||
                    !frame.AsSpan(0, 4).SequenceEqual(_hostId)) { Drop(); return; }
                ReceivePacket(frame[^8..], frame[5..^8]);
                break;
            case WeaveProtocol.Log:
                if (frame.Length < 14) { Drop(); return; }
                // The reference skips two bytes after type before the four-byte timestamp.
                var log = new WeaveLogFrame(BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(7, 4)), frame[11],
                    BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2)), frame.AsMemory(14).ToArray());
                HandleLog(session, log);
                Notify(LogReceived, log);
                break;
            case WeaveProtocol.Display:
                HandleDisplay(frame);
                break;
            // CONNECT/CMD/ENCAP_PROTO have no inbound implementation in the pinned source.
        }
    }

    private void HandleLog(Session session, WeaveLogFrame log)
    {
        var data = log.Data.Span;
        switch (log.Event)
        {
            case 0x3002 when session.RemoteId is not null:
                session.Online = true; session.Ready.TrySetResult(); break;
            case 0x3003 when data.Length == 8:
                lock (_gate) _endpoint = data.ToArray(); break;
            case 0x3102 when data.Length == 8:
                if (session.Online) GetOrAddPeer(data.ToArray()); break;
            case 0x3104 when data.Length == 12:
                lock (_gate) if (_peers.TryGetValue(Convert.ToHexString(data[..8]), out var peer)) peer.SetVia(data[8..].ToArray());
                break;
            case 0xe003 when data.Length >= 1: _cpu = data[0]; break;
            case 0xe004 when data.Length >= 2:
                lock (_gate)
                {
                    foreach (var key in _tasks.Where(p => _clock.GetUtcNow() - p.Value.Time >= TimeSpan.FromSeconds(5)).Select(p => p.Key).ToArray()) _tasks.Remove(key);
                    if (_tasks.Count < 120) _tasks[System.Text.Encoding.UTF8.GetString(data[1..])] = (data[0], _clock.GetUtcNow());
                }
                break;
            case 0xe005 when data.Length == 8:
                _memoryFree = BinaryPrimitives.ReadUInt32BigEndian(data);
                _memoryTotal = BinaryPrimitives.ReadUInt32BigEndian(data[4..]); break;
        }
    }

    private void HandleDisplay(byte[] frame)
    {
        if (frame.Length < 14) { Drop(); return; }
        var offset = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(6, 4));
        var size = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(10, 4));
        var length = frame.Length - 14;
        if (size == 0 || size > _options.MaximumDisplayBytes || offset > size || length > size - offset) { Drop(); return; }
        byte[]? completed = null;
        lock (_gate)
        {
            if (_display?.Length != size) _display = new byte[(int)size];
            frame.AsSpan(14).CopyTo(_display.AsSpan((int)offset));
            if (_displayEnabled && offset + length == size) completed = _display.ToArray();
        }
        if (completed is not null) Notify(DisplayUpdated, (ReadOnlyMemory<byte>)completed);
    }

    private WeavePeer? GetOrAddPeer(byte[] endpoint)
    {
        WeavePeer peer;
        lock (_gate)
        {
            if (_disposed) return null;
            var key = Convert.ToHexString(endpoint);
            if (_peers.TryGetValue(key, out peer!)) { peer.LastHeard = _clock.GetUtcNow(); return peer; }
            if (_peers.Count >= _options.MaximumPeers) { Drop(); return null; }
            peer = new(this, endpoint, _options.ReceiveQueueCapacity, _clock.GetUtcNow());
            _peers.Add(key, peer);
        }
        Notify(ChildConnected, (IPacketInterface)peer);
        return peer;
    }

    private void ReceivePacket(byte[] endpoint, byte[] packet)
    {
        var peer = GetOrAddPeer(endpoint);
        if (peer is null) return;
        var hash = SHA256.HashData(packet);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_duplicates.Any(p => p.Hash.AsSpan().SequenceEqual(hash) && now - p.Time < TimeSpan.FromSeconds(0.75))) return;
            if (_duplicates.Count == 48) _duplicates.Dequeue();
            _duplicates.Enqueue((hash, now));
        }
        if (peer.Enqueue(packet)) Interlocked.Add(ref _rxb, packet.Length);
        else Drop();
    }

    private async Task PeerJobsAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.PeerJobInterval, _clock, _stop.Token).ConfigureAwait(false);
                lock (_gate)
                    foreach (var pair in _peers.Where(p => _clock.GetUtcNow() - p.Value.LastHeard > _options.PeeringTimeout).ToArray())
                    { _peers.Remove(pair.Key); pair.Value.Close(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    internal void Remove(WeavePeer peer, byte[] endpoint)
    {
        lock (_gate)
        {
            var key = Convert.ToHexString(endpoint);
            if (_peers.TryGetValue(key, out var current) && ReferenceEquals(current, peer)) _peers.Remove(key);
        }
    }

    internal async ValueTask DeliverAsync(WeavePeer peer, byte[] endpoint, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (packet.IsEmpty || packet.Length > WeaveProtocol.MaximumPacketLength) throw new ArgumentOutOfRangeException(nameof(packet));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var session = await ReserveAsync(packet.Length, peer, linked.Token).ConfigureAwait(false);
        try
        {
            var payload = new byte[10 + packet.Length];
            payload[1] = 1; endpoint.CopyTo(payload, 2); packet.CopyTo(payload.AsMemory(10));
            await WriteAsync(session, WeaveProtocol.Frame(session.RemoteId!, WeaveProtocol.Command, payload), linked.Token).ConfigureAwait(false);
            Interlocked.Add(ref _txb, packet.Length);
        }
        finally
        {
            Release(packet.Length);
        }
    }

    public async ValueTask SendCommandAsync(ushort command, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length > WeaveProtocol.MaximumFrameLength - 7) throw new ArgumentOutOfRangeException(nameof(data));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var session = await ReserveAsync(data.Length + 2, null, linked.Token).ConfigureAwait(false);
        try
        {
            var payload = new byte[2 + data.Length];
            BinaryPrimitives.WriteUInt16BigEndian(payload, command);
            data.CopyTo(payload.AsMemory(2));
            await WriteAsync(session, WeaveProtocol.Frame(session.RemoteId!, WeaveProtocol.Command, payload), linked.Token).ConfigureAwait(false);
        }
        finally { Release(data.Length + 2); }
    }

    public async ValueTask SetDisplayEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(0x0a00, new byte[] { enabled ? (byte)1 : (byte)0 }, cancellationToken).ConfigureAwait(false);
        lock (_gate) _displayEnabled = enabled;
    }

    private async ValueTask<Session> ReserveAsync(int bytes, WeavePeer? peer, CancellationToken cancellationToken)
    {
        if (bytes > _options.MaximumQueuedTransmitBytes) throw new ArgumentOutOfRangeException(nameof(bytes), "Command exceeds the configured shared transmit byte budget.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var session = _session ?? throw new IOException("WDCL device is offline.");
                if (!session.Online || peer is not null && !peer.IsConnected) throw new IOException("Weave endpoint is offline.");
                if (_pendingFrames < _options.MaximumQueuedTransmitFrames && _pendingBytes <= _options.MaximumQueuedTransmitBytes - bytes)
                {
                    if (_pendingFrames == 0) _transmitsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingFrames++; _pendingBytes += bytes; return session;
                }
            }
            await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Release(int bytes)
    {
        lock (_gate)
        {
            _pendingFrames--; _pendingBytes -= bytes;
            if (_pendingFrames == 0) _transmitsDrained?.TrySetResult();
        }
        SignalCapacity();
    }

    private async ValueTask WriteAsync(Session session, byte[] bytes, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Stop.Token);
        await _wire.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await session.Stream.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
            Interlocked.Add(ref _wireTx, bytes.Length);
        }
        catch (Exception error)
        {
            session.Online = false;
            session.Stop.Cancel();
            Report(error);
            // Closing the stream also unblocks serial implementations that ignore read cancellation.
            session.Owner?.Dispose();
            session.Stream.Dispose();
            throw;
        }
        finally { _wire.Release(); }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_start is null) throw new InvalidOperationException("Start the Weave interface first.");
        if (Interlocked.Exchange(ref _receiving, 1) != 0) throw new InvalidOperationException("Only one receive enumeration is supported.");
        return _parent.Reader.ReadAllAsync(cancellationToken);
    }
    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected) throw new IOException("WDCL device is offline.");
        return ValueTask.CompletedTask;
    }

    private ValueTask CloseAsync(Session session)
    {
        lock (session) return new(session.Closing ??= CloseCoreAsync(session));
    }

    private async Task CloseCoreAsync(Session session)
    {
        await Task.Yield();
        session.Online = false;
        session.Stop.Cancel();
        try { session.Owner?.Dispose(); }
        catch (Exception error) { Report(error); }
        try { await session.Stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { Report(error); }
        try { await session.Reader.ConfigureAwait(false); }
        catch (Exception) { /* The reader's failure is reported by its owning worker/start path. */ }
        await _wire.WaitAsync().ConfigureAwait(false);
        _wire.Release();
        session.Stop.Dispose();
    }
    private void ClearPeers()
    {
        lock (_gate)
        {
            foreach (var peer in _peers.Values) peer.Close();
            _peers.Clear(); _duplicates.Clear(); _endpoint = null;
        }
        SignalCapacity();
    }
    private void SignalCapacity() { try { _capacity.Release(); } catch (SemaphoreFullException) { } }
    private void Drop() => Interlocked.Increment(ref _drops);
    private void Report(Exception error)
    {
        _lastError = error.Message;
        var observers = Faulted;
        if (observers is null) return;
        foreach (Action<Exception> observer in observers.GetInvocationList())
            try { observer(error); } catch (Exception) { Interlocked.Increment(ref _observerErrors); }
    }
    private void Notify(Action? observers)
    {
        if (observers is null) return;
        foreach (Action observer in observers.GetInvocationList())
            try { observer(); } catch (Exception error) { Interlocked.Increment(ref _observerErrors); Report(error); }
    }
    private void Notify<T>(Action<T>? observers, T value)
    {
        if (observers is null) return;
        foreach (Action<T> observer in observers.GetInvocationList())
            try { observer(value); } catch (Exception error) { Interlocked.Increment(ref _observerErrors); Report(error); }
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["status"] = IsConnected, ["rxb"] = Interlocked.Read(ref _rxb), ["txb"] = Interlocked.Read(ref _txb),
            ["wdcl_rxb"] = Interlocked.Read(ref _wireRx), ["wdcl_txb"] = Interlocked.Read(ref _wireTx),
            ["bitrate"] = Bitrate, ["hardware_mtu"] = HardwareMtu, ["peers"] = _peers.Count,
            ["dotreticulum_bitrate_kind"] = "configured_estimate",
            ["switch_id"] = _session?.RemoteId is { } id ? Convert.ToHexStringLower(id) : null,
            ["endpoint_id"] = _endpoint is null ? null : Convert.ToHexStringLower(_endpoint),
            ["cpu_load"] = _cpu, ["memory_free"] = _memoryFree, ["memory_total"] = _memoryTotal,
            ["mem_load"] = _memoryTotal == 0 ? null : (double?)Math.Round((1 - (double)_memoryFree / _memoryTotal) * 100, 2),
            ["queued_transmit_frames"] = _pendingFrames, ["queued_transmit_bytes"] = _pendingBytes,
            ["dropped_frames"] = Interlocked.Read(ref _drops), ["observer_errors"] = Interlocked.Read(ref _observerErrors),
            ["reconnects"] = Interlocked.Read(ref _reconnects), ["last_error"] = _lastError,
            ["active_tasks"] = _tasks.Where(p => _clock.GetUtcNow() - p.Value.Time < TimeSpan.FromSeconds(5) && !p.Key.StartsWith("IDLE", StringComparison.Ordinal))
                .ToDictionary(p => p.Key, p => p.Value.Load),
        };
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true;
            _stop.Cancel();
            _dispose = DisposeCoreAsync();
            return new(_dispose);
        }
    }
    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        Session? session;
        lock (_gate) session = _session;
        if (session is not null) await CloseAsync(session).ConfigureAwait(false);
        foreach (var task in new[] { _start, _worker, _jobs })
            if (task is not null)
                try { await task.ConfigureAwait(false); } catch (Exception) { }
        Task drained;
        lock (_gate) drained = _transmitsDrained?.Task ?? Task.CompletedTask;
        await drained.ConfigureAwait(false);
        ClearPeers();
        _parent.Writer.TryComplete();
        _identity.Dispose();
    }
}
