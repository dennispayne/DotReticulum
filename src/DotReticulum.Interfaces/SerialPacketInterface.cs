using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>Serial HDLC with alternating delimiters, idle expiry and optional owned-port reconnection.</summary>
public sealed class SerialPacketInterface : IReconnectingPacketInterface, IPacketInterfaceDiagnostics
{
    private readonly Func<CancellationToken, ValueTask<(Stream Stream, IDisposable? Owner)>> _open;
    private readonly bool _reconnect;
    private readonly TimeSpan _reconnectDelay;
    private readonly TimeSpan _openDelay;
    private readonly object _gate = new();
    private readonly Stream? _providedStream;
    private readonly int? _baudRate;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<ReadOnlyMemory<byte>> _received = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private HdlcStreamEndpoint? _endpoint;
    private Task? _worker;
    private Task? _disposeTask;
    private bool _started;
    private bool _disposed;
    private int _receiving;
    private long _rxBytes;
    private long _txBytes;
    private int _reconnectAttempts;
    private long _offlineSends;
    private string? _lastError;

    /// <summary>Real serial port. Reconnection is opt-in for existing standalone callers.</summary>
    public SerialPacketInterface(string portName, int baudRate = 9600, Parity parity = Parity.None, int dataBits = 8,
        StopBits stopBits = StopBits.One, long? nominalBitrate = null, bool reconnect = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentOutOfRangeException.ThrowIfLessThan(baudRate, 1);
        if (dataBits is < 5 or > 8) throw new ArgumentOutOfRangeException(nameof(dataBits));
        if (parity is not (Parity.None or Parity.Even or Parity.Odd)) throw new ArgumentOutOfRangeException(nameof(parity));
        if (stopBits is not (StopBits.One or StopBits.Two)) throw new ArgumentOutOfRangeException(nameof(stopBits));
        if (nominalBitrate is < 1) throw new ArgumentOutOfRangeException(nameof(nominalBitrate));
        Bitrate = nominalBitrate ?? baudRate;
        _baudRate = baudRate;
        PortName = portName;
        DataBits = dataBits;
        Parity = parity;
        StopBits = stopBits;
        _reconnect = reconnect;
        _reconnectDelay = TimeSpan.FromSeconds(5);
        _openDelay = TimeSpan.FromMilliseconds(500);
        _open = token =>
        {
            token.ThrowIfCancellationRequested();
            var port = NativeSerialPort.Create(portName, baudRate, parity, dataBits, stopBits);
            try
            {
                port.Open();
                return ValueTask.FromResult<(Stream, IDisposable?)>((port.BaseStream, port));
            }
            catch
            {
                port.Dispose();
                throw;
            }
        };
    }

    /// <summary>Owns a supplied duplex stream; it is opened once and never reconnected.</summary>
    public SerialPacketInterface(Stream duplexStream, long? nominalBitrate = null)
    {
        ValidateStream(duplexStream);
        if (nominalBitrate is < 1) throw new ArgumentOutOfRangeException(nameof(nominalBitrate));
        Bitrate = nominalBitrate ?? 0;
        _providedStream = duplexStream;
        _open = _ => ValueTask.FromResult<(Stream, IDisposable?)>((duplexStream, null));
    }

    /// <summary>
    /// Owns each stream produced by the factory. Initial open failures and disconnects retry until disposal.
    /// The cancellation-aware factory must release partially opened resources if it fails.
    /// </summary>
    public SerialPacketInterface(Func<CancellationToken, ValueTask<Stream>> streamFactory, long nominalBitrate = 9600,
        TimeSpan? reconnectDelay = null, TimeSpan? openDelay = null)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(nominalBitrate, 1);
        Bitrate = nominalBitrate;
        _reconnect = true;
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(5);
        _openDelay = openDelay ?? TimeSpan.FromMilliseconds(500);
        if (_reconnectDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(reconnectDelay));
        if (_openDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(openDelay));
        _open = async token => (await streamFactory(token).ConfigureAwait(false), null);
    }

    public long Bitrate { get; }
    public int HardwareMtu => 564;
    public string? PortName { get; }
    public int DataBits { get; } = 8;
    public Parity Parity { get; } = Parity.None;
    public StopBits StopBits { get; } = StopBits.One;
    public bool IsConnected { get { lock (_gate) return !_disposed && _endpoint is { IsConnected: true }; } }
    public event Action? Connected;
    public event Action? Disconnected;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("The interface has already been started.");
            _started = true;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { await OpenAsync(linked.Token).ConfigureAwait(false); }
        catch (Exception error) when (_reconnect && IsConnectionFailure(error) && !linked.IsCancellationRequested)
        {
            Report(error);
        }
        catch (Exception error)
        {
            Report(error);
            throw;
        }
        if (_reconnect)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _worker = Task.Run(RecoverAndReceiveAsync, CancellationToken.None);
            }
        }
    }

    private async ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        var (stream, owner) = await _open(cancellationToken).ConfigureAwait(false);
        HdlcStreamEndpoint? endpoint = null;
        try
        {
            ValidateStream(stream);
            endpoint = new HdlcStreamEndpoint(stream, owner,
                received: length => Interlocked.Add(ref _rxBytes, length),
                sent: length => Interlocked.Add(ref _txBytes, length),
                maxFrameLength: HardwareMtu + 64, authenticatedFrames: true, serialFraming: true,
                unprotectedFrameLength: HardwareMtu);
            if (_openDelay > TimeSpan.Zero) await Task.Delay(_openDelay, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _endpoint = endpoint;
            }
        }
        catch
        {
            if (endpoint is not null) await endpoint.DisposeAsync().ConfigureAwait(false);
            else
            {
                if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                owner?.Dispose();
            }
            throw;
        }
        Notify(Connected);
    }

    private async Task RecoverAndReceiveAsync()
    {
        var token = _lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                HdlcStreamEndpoint? endpoint;
                lock (_gate) endpoint = _endpoint;
                if (endpoint is null)
                {
                    await Task.Delay(_reconnectDelay, token).ConfigureAwait(false);
                    Interlocked.Increment(ref _reconnectAttempts);
                    try { await OpenAsync(token).ConfigureAwait(false); }
                    catch (Exception error) when (IsConnectionFailure(error) && !token.IsCancellationRequested) { Report(error); }
                    continue;
                }
                try
                {
                    await foreach (var frame in endpoint.ReceiveAsync(token).ConfigureAwait(false))
                        await _received.Writer.WriteAsync(frame, token).ConfigureAwait(false);
                }
                catch (Exception error) when (IsConnectionFailure(error) && !token.IsCancellationRequested) { Report(error); }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_endpoint, endpoint)) _endpoint = null;
                    }
                    await endpoint.DisposeAsync().ConfigureAwait(false);
                    if (!token.IsCancellationRequested) Notify(Disconnected);
                }
            }
            _received.Writer.TryComplete();
        }
        catch (Exception) when (token.IsCancellationRequested) { _received.Writer.TryComplete(); }
        catch (Exception error)
        {
            Report(error);
            _received.Writer.TryComplete(error);
        }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        var endpoint = GetEndpoint();
        if (!_reconnect) return endpoint!.ReceiveAsync(cancellationToken);
        if (Interlocked.Exchange(ref _receiving, 1) != 0) throw new InvalidOperationException("ReceiveAsync supports a single enumerator.");
        return ReceiveQueuedAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveQueuedAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await using var incoming = _received.Reader.ReadAllAsync(linked.Token).GetAsyncEnumerator();
        while (true)
        {
            bool more;
            try { more = await incoming.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { more = false; }
            if (!more) yield break;
            yield return incoming.Current;
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = GetEndpoint();
        if (packet.IsEmpty || packet.Length > HardwareMtu + ((packet.Span[0] & 0x80) != 0 ? 64 : 0))
            throw new ArgumentException("Packet exceeds the bearer frame bound.", nameof(packet));
        if (endpoint is null)
        {
            Interlocked.Increment(ref _offlineSends);
            System.Diagnostics.Trace.TraceWarning("Serial interface: discarded send while disconnected.");
            return;
        }
        try { await endpoint.SendAsync(packet, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (IsConnectionFailure(error) && !cancellationToken.IsCancellationRequested)
        {
            Report(error);
            if (_reconnect) await endpoint.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private HdlcStreamEndpoint? GetEndpoint()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started || (!_reconnect && _endpoint is null)) throw new InvalidOperationException("The interface has not been started.");
            return _endpoint;
        }
    }

    private static void ValidateStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite) throw new ArgumentException("The stream must be readable and writable.", nameof(stream));
    }

    private static bool IsConnectionFailure(Exception error) => error is IOException or UnauthorizedAccessException or ObjectDisposedException;
    private void Report(Exception error)
    {
        Volatile.Write(ref _lastError, error.Message);
        System.Diagnostics.Trace.TraceError("Serial interface {0}: {1}", PortName ?? "stream", error);
    }

    private void Notify(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error) { Report(error); }
        }
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["rxb"] = Interlocked.Read(ref _rxBytes),
            ["txb"] = Interlocked.Read(ref _txBytes),
            ["bitrate"] = Bitrate == 0 ? null : (long?)Bitrate,
            ["hardware_mtu"] = HardwareMtu,
            ["dotreticulum_bitrate_kind"] = Bitrate == 0 ? null : _baudRate == Bitrate ? "configured_baud" : "configured_estimate",
            ["port"] = PortName,
            ["speed"] = _baudRate,
            ["databits"] = DataBits,
            ["parity"] = Parity.ToString(),
            ["stopbits"] = StopBits.ToString(),
            ["status"] = IsConnected,
            ["reconnecting"] = _reconnect && !_disposed && _endpoint is null && _worker is { IsCompleted: false },
            ["reconnect_attempts"] = Volatile.Read(ref _reconnectAttempts),
            ["offline_sends"] = Interlocked.Read(ref _offlineSends),
            ["last_error"] = Volatile.Read(ref _lastError),
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
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        HdlcStreamEndpoint? endpoint;
        Task? worker;
        lock (_gate) { endpoint = _endpoint; worker = _worker; }
        if (endpoint is not null) await endpoint.DisposeAsync().ConfigureAwait(false);
        else if (_providedStream is not null) await _providedStream.DisposeAsync().ConfigureAwait(false);
        if (worker is not null) await worker.ConfigureAwait(false);
        _received.Writer.TryComplete();
    }
}
