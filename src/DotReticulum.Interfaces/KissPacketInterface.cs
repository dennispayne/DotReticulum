using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>Owned asynchronous KISS TNC transport. No device is opened until StartAsync.</summary>
public class KissPacketInterface : IReconnectingPacketInterface, IPacketInterfaceDiagnostics
{
    private sealed class Connection(Stream stream, IDisposable? owner)
    {
        public Stream Stream { get; } = stream;
        public IDisposable? Owner { get; } = owner;
        public Task? Closing { get; set; }
    }
    private sealed record SendRequest(byte[] Payload, CancellationToken Cancellation, TaskCompletionSource Completion);

    private readonly KissOptions _options;
    private readonly Ax25KissOptions? _ax25;
    private readonly Func<CancellationToken, ValueTask<Connection>> _open;
    private readonly bool _canReconnect;
    private readonly Stream? _providedStream;
    private readonly Channel<SendRequest> _outgoing;
    private readonly Channel<ReadOnlyMemory<byte>> _incoming;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly byte[] _beacon;
    private Task? _startTask;
    private Task? _worker;
    private Task? _disposeTask;
    private SendRequest? _retryRequest;
    private Connection? _connection;
    private bool _disposed;
    private bool _connected;
    private bool _startedSuccessfully;
    private bool _providedOpened;
    private int _receiving;
    private int _credits;
    private bool _waitingForReady;
    private DateTimeOffset _flowLocked;
    private DateTimeOffset? _firstTransmit;
    private long _received;
    private long _sent;
    private long _dropped;
    private long _flowTimeouts;
    private long _reconnections;
    private long _observerErrors;
    private long _wireRead;
    private long _partialExpirations;
    private string? _lastError;

    public long Bitrate => _options.ConfiguredBitrate ?? 1200;
    public int HardwareMtu => 564;
    public bool IsConnected { get { lock (_gate) return _connected && !_disposed; } }
    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<Exception>? Faulted;

    /// <summary>Owns the supplied stream, including disposal before StartAsync. A single stream cannot reconnect.</summary>
    public KissPacketInterface(Stream duplexStream, KissOptions? options = null)
        : this(OwnedStreamFactory(duplexStream), options, canReconnect: false)
    {
        _providedStream = duplexStream;
    }

    /// <summary>Each successful factory invocation transfers stream ownership. Used for explicit reconnectable simulators/adapters.</summary>
    public KissPacketInterface(Func<CancellationToken, ValueTask<Stream>> openStream, KissOptions? options = null)
        : this(WrapFactory(openStream), options, canReconnect: true) { }

    public KissPacketInterface(string portName, KissOptions? options = null)
        : this(SerialFactory(portName, options ?? new KissOptions()), options, canReconnect: true) { }

    private KissPacketInterface(Func<CancellationToken, ValueTask<Connection>> open, KissOptions? options, bool canReconnect)
    {
        _options = options ?? new KissOptions();
        _options.Validate();
        _ax25 = _options as Ax25KissOptions;
        _open = open;
        _canReconnect = canReconnect && _options.Reconnect;
        _slots = new SemaphoreSlim(_options.SendQueueCapacity, _options.SendQueueCapacity);
        _outgoing = Channel.CreateBounded<SendRequest>(new BoundedChannelOptions(_options.SendQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false,
        });
        _incoming = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(_options.ReceiveQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false,
        });
        _beacon = Encoding.UTF8.GetBytes(_options.BeaconCallsign);
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startTask is not null) throw new InvalidOperationException("The interface has already been started.");
            _startTask = StartCoreAsync(cancellationToken);
            return new ValueTask(_startTask);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        Connection? connection = null;
        try
        {
            connection = await OpenConfiguredAsync(linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                _connection = connection;
                _connected = true;
                _startedSuccessfully = true;
                _credits = 1;
                _flowLocked = Now;
                _worker = RunAsync(connection);
            }
            Notify(Connected);
        }
        catch (Exception error)
        {
            if (connection is not null) await CloseAsync(connection).ConfigureAwait(false);
            Report(error);
            _incoming.Writer.TryComplete(error);
            _outgoing.Writer.TryComplete(error);
            throw;
        }
    }

    private async Task<Connection> OpenConfiguredAsync(CancellationToken cancellationToken)
    {
        var connection = await _open(cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(connection.Stream, _providedStream)) _providedOpened = true;
        try
        {
            if (!connection.Stream.CanRead || !connection.Stream.CanWrite)
                throw new IOException("KISS requires a readable, writable duplex stream.");
            await Task.Delay(_options.DeviceSettleDelay, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            var commands = KissPacketFraming.Configuration(_options);
            for (var i = 0; i < commands.Length; i += 4)
                await connection.Stream.WriteAsync(commands.AsMemory(i, 4), cancellationToken).ConfigureAwait(false);
            await connection.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await CloseAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunAsync(Connection connection)
    {
        Exception? terminalError = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var decoder = new KissFrameDecoder(HardwareMtu + (_ax25 is null ? 0 : Ax25KissFraming.HeaderSize));
                var decoderGate = new object();
                var lastRead = Now;
                var read = ReadLoopAsync(connection.Stream, decoder, decoderGate, () => lastRead = Now, session.Token);
                var write = WriteLoopAsync(connection.Stream, session.Token);
                var maintenance = MaintainAsync(decoder, decoderGate, () => lastRead, session.Token);
                var first = await Task.WhenAny(read, write, maintenance).ConfigureAwait(false);
                Exception? failure = null;
                try
                {
                    await first.ConfigureAwait(false);
                    if (!_stop.IsCancellationRequested) failure = new EndOfStreamException("KISS byte stream closed.");
                }
                catch (Exception error) { failure = error; }
                lock (_gate) _connected = false;
                await session.CancelAsync().ConfigureAwait(false);
                try { await CloseAsync(connection).ConfigureAwait(false); }
                catch (Exception error) { failure ??= error; }
                try { await Task.WhenAll(read, write, maintenance).ConfigureAwait(false); }
                catch (Exception error) { failure ??= error; }
                lock (_gate) _connection = null;
                if (_stop.IsCancellationRequested) break;
                failure ??= new EndOfStreamException("KISS byte stream closed.");
                Report(failure);
                Notify(Disconnected);
                if (!_canReconnect) { terminalError = failure; break; }
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(_options.ReconnectDelay, _options.TimeProvider, _stop.Token).ConfigureAwait(false);
                    try
                    {
                        connection = await OpenConfiguredAsync(_stop.Token).ConfigureAwait(false);
                        lock (_gate)
                        {
                            _connection = connection;
                            _connected = true;
                            _credits = 1;
                            _flowLocked = Now;
                        }
                        Interlocked.Increment(ref _reconnections);
                        Notify(Connected);
                        break;
                    }
                    catch (Exception error) when (!_stop.IsCancellationRequested) { Report(error); }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { terminalError = error; Report(error); }
        finally
        {
            try { await CloseAsync(connection).ConfigureAwait(false); }
            catch (Exception error) { Report(error); }
            lock (_gate) { _connected = false; _connection = null; }
            _outgoing.Writer.TryComplete(terminalError);
            var retry = Interlocked.Exchange(ref _retryRequest, null);
            if (retry is not null)
            {
                retry.Completion.TrySetException(terminalError ?? new ObjectDisposedException(GetType().Name));
                _slots.Release();
            }
            while (_outgoing.Reader.TryRead(out var item))
            {
                item.Completion.TrySetException(terminalError ?? new ObjectDisposedException(GetType().Name));
                _slots.Release();
            }
            _incoming.Writer.TryComplete(terminalError);
        }
    }

    private async Task ReadLoopAsync(Stream stream, KissFrameDecoder decoder, object decoderGate, Action readObserved, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var bytes = new byte[4096];
        var frames = new List<byte[]>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (count == 0) return;
            frames.Clear();
            int ready;
            lock (decoderGate)
            {
                readObserved();
                ready = decoder.Feed(bytes.AsSpan(0, count), frames);
                Interlocked.Add(ref _wireRead, count);
            }
            for (var i = 0; i < ready; i++) ReleaseReady();
            foreach (var frame in frames)
            {
                if (_ax25 is not null && frame.Length <= Ax25KissFraming.HeaderSize) continue;
                Interlocked.Add(ref _received, frame.Length);
                ReadOnlyMemory<byte> packet = _ax25 is null ? frame : frame.AsMemory(Ax25KissFraming.HeaderSize);
                if (!_incoming.Writer.TryWrite(packet)) Interlocked.Increment(ref _dropped);
            }
        }
    }

    private async Task WriteLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendRequest? item;
            lock (_gate)
            {
                item = Interlocked.Exchange(ref _retryRequest, null);
                if (item is not null) _waitingForReady = true;
            }
            if (item is null)
            {
                if (!await _outgoing.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return;
                lock (_gate)
                {
                    if (!_outgoing.Reader.TryRead(out item)) continue;
                    _waitingForReady = true;
                }
            }
            var releaseSlot = true;
            var writeStarted = false;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, item.Cancellation);
                linked.Token.ThrowIfCancellationRequested();
                try
                {
                    while (true)
                    {
                        lock (_gate)
                        {
                            if (!_options.FlowControl || _credits > 0)
                            {
                                if (_options.FlowControl) _credits--;
                                _flowLocked = Now;
                                break;
                            }
                        }
                        await _wake.WaitAsync(linked.Token).ConfigureAwait(false);
                    }
                }
                finally { lock (_gate) _waitingForReady = false; }
                var data = _ax25 is null ? item.Payload : Ax25KissFraming.AddHeader(item.Payload, _ax25.Callsign, _ax25.Ssid);
                var wire = KissPacketFraming.Encode(data);
                try
                {
                    writeStarted = true;
                    await stream.WriteAsync(wire, linked.Token).ConfigureAwait(false);
                    await stream.FlushAsync(linked.Token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    item.Completion.TrySetException(error);
                    throw new IOException("KISS data write failed; an ambiguous active send is not replayed.", error);
                }
                Interlocked.Add(ref _sent, item.Payload.Length);
                if (_ax25 is null)
                {
                    lock (_gate)
                    {
                        // Upstream compares the escaped data, before adding KISS delimiters, with unpadded beacon data.
                        if (wire.AsSpan(2, wire.Length - 3).SequenceEqual(_beacon)) _firstTransmit = null;
                        else _firstTransmit ??= Now;
                    }
                }
                item.Completion.TrySetResult();
            }
            catch (OperationCanceledException) when (item.Cancellation.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(item.Cancellation);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested && _canReconnect && !writeStarted)
            {
                _retryRequest = item;
                releaseSlot = false;
                throw;
            }
            catch (Exception error)
            {
                item.Completion.TrySetException(error);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    _waitingForReady = false;
                    _credits = Math.Min(_credits, _outgoing.Reader.Count + 1);
                }
                if (releaseSlot) _slots.Release();
            }
        }
    }

    private async Task MaintainAsync(KissFrameDecoder decoder, object decoderGate, Func<DateTimeOffset> lastRead, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(_options.MaintenanceInterval, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            var now = Now;
            lock (decoderGate)
            {
                if (decoder.HasPartialData && now - lastRead() > _options.PartialFrameTimeout)
                {
                    decoder.ExpirePartial();
                    Interlocked.Increment(ref _partialExpirations);
                }
            }
            lock (_gate)
            {
                if (_options.FlowControl && _credits == 0 && now - _flowLocked > _options.FlowControlTimeout)
                {
                    Interlocked.Increment(ref _flowTimeouts);
                    System.Diagnostics.Trace.TraceWarning("KISS interface unlocked flow control after READY timeout.");
                    _credits = 1;
                    _flowLocked = now;
                    Wake();
                }
                if (_ax25 is null && _options.BeaconInterval is { } interval && _firstTransmit is { } first && now - first > interval)
                {
                    _firstTransmit = null;
                    if (_slots.Wait(0))
                    {
                        var beacon = new byte[Math.Max(15, _beacon.Length)];
                        _beacon.CopyTo(beacon, 0);
                        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        // Observe failure even when no caller is waiting for this internally queued beacon.
                        _ = completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                        if (!_outgoing.Writer.TryWrite(new SendRequest(beacon, _stop.Token, completion))) _slots.Release();
                    }
                    else _firstTransmit = first;
                }
            }
        }
    }

    private void ReleaseReady()
    {
        lock (_gate)
        {
            // Surplus READY bytes leave one idle permission after all already queued sends.
            _credits = Math.Min(_credits + 1, _outgoing.Reader.Count + (_waitingForReady ? 1 : 0) + 1);
            Wake();
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (packet.IsEmpty || packet.Length > HardwareMtu) throw new ArgumentOutOfRangeException(nameof(packet));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_startedSuccessfully) throw new InvalidOperationException("KISS has not started successfully.");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
        var transferred = false;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // A caller may cancel its wait while the bounded worker still owns the queued item.
            _ = completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            var request = new SendRequest(packet.ToArray(), cancellationToken, completion);
            if (!_outgoing.Writer.TryWrite(request)) throw new IOException("KISS send queue is closed.");
            transferred = true;
            await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally { if (!transferred) _slots.Release(); }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_startedSuccessfully) throw new InvalidOperationException("KISS has not started successfully.");
        }
        if (Interlocked.Exchange(ref _receiving, 1) != 0) throw new InvalidOperationException("ReceiveAsync supports a single enumerator.");
        return ReceiveCoreAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var packet in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return packet;
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["rxb"] = Interlocked.Read(ref _received), ["txb"] = Interlocked.Read(ref _sent),
            ["status"] = _connected && !_disposed, ["bitrate"] = Bitrate, ["hardware_mtu"] = HardwareMtu,
            ["dotreticulum_bitrate_kind"] = _options.ConfiguredBitrate is null ? "upstream_estimate" : "configured_estimate",
            ["kiss_receive_drops"] = Interlocked.Read(ref _dropped),
            ["kiss_flow_timeouts"] = Interlocked.Read(ref _flowTimeouts),
            ["kiss_reconnections"] = Interlocked.Read(ref _reconnections),
            ["kiss_observer_errors"] = Interlocked.Read(ref _observerErrors),
            ["kiss_wire_read_bytes"] = Interlocked.Read(ref _wireRead),
            ["kiss_partial_expirations"] = Interlocked.Read(ref _partialExpirations),
            ["kiss_send_pending"] = _options.SendQueueCapacity - _slots.CurrentCount,
            ["kiss_last_error"] = _lastError,
        };
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        Task? start;
        lock (_gate) start = _startTask;
        if (start is not null) { try { await start.ConfigureAwait(false); } catch (Exception) { } }
        Task? worker;
        Connection? connection;
        lock (_gate) { worker = _worker; connection = _connection; }
        if (connection is not null)
        {
            try { await CloseAsync(connection).ConfigureAwait(false); }
            catch (Exception error) { Report(error); }
        }
        if (worker is not null) await worker.ConfigureAwait(false);
        else if (_providedStream is not null && !_providedOpened) await _providedStream.DisposeAsync().ConfigureAwait(false);
        _outgoing.Writer.TryComplete();
        _incoming.Writer.TryComplete();
    }

    private DateTimeOffset Now => _options.TimeProvider.GetUtcNow();

    private void Notify(Action? observers)
    {
        if (observers is null) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception error) { ObserverFailed(error); }
        }
    }

    private void Report(Exception error)
    {
        lock (_gate) _lastError = error.Message;
        System.Diagnostics.Trace.TraceError("KISS interface failure: {0}", error);
        if (Faulted is not { } observers) return;
        foreach (Action<Exception> observer in observers.GetInvocationList())
        {
            try { observer(error); }
            catch (Exception observerError) { ObserverFailed(observerError); }
        }
    }

    private void ObserverFailed(Exception error)
    {
        Interlocked.Increment(ref _observerErrors);
        lock (_gate) _lastError = $"KISS observer failed: {error.Message}";
        System.Diagnostics.Trace.TraceError("KISS observer failed: {0}", error);
    }

    private static ValueTask CloseAsync(Connection connection)
    {
        lock (connection) return new ValueTask(connection.Closing ??= CloseCoreAsync(connection));
    }

    private static async Task CloseCoreAsync(Connection connection)
    {
        try { await connection.Stream.DisposeAsync().ConfigureAwait(false); }
        finally { connection.Owner?.Dispose(); }
    }

    private static Func<CancellationToken, ValueTask<Connection>> OwnedStreamFactory(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite) throw new ArgumentException("Stream must be readable and writable.", nameof(stream));
        return cancellationToken => { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Connection(stream, null)); };
    }

    private static Func<CancellationToken, ValueTask<Connection>> WrapFactory(Func<CancellationToken, ValueTask<Stream>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return async cancellationToken =>
        {
            var stream = await factory(cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(stream);
            return new Connection(stream, null);
        };
    }

    private static Func<CancellationToken, ValueTask<Connection>> SerialFactory(string portName, KissOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        return cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var port = new SerialPort(portName, options.BaudRate, options.Parity, options.DataBits, options.StopBits)
            {
                Handshake = Handshake.None, DtrEnable = false, RtsEnable = false,
            };
            try
            {
                port.Open();
                return ValueTask.FromResult(new Connection(port.BaseStream, port));
            }
            catch { port.Dispose(); throw; }
        };
    }
}
