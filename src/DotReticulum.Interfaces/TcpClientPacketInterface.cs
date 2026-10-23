using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>
/// HDLC TCP bearer. Reconnection is opt-in for standalone library callers; the daemon enables it.
/// Reconnecting interfaces retain their identity, discard offline sends and bound received packets with backpressure.
/// </summary>
public sealed class TcpClientPacketInterface : IReconnectingPacketInterface, IPacketInterfaceDiagnostics
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _reconnect;
    private readonly int? _maxReconnectAttempts;
    private readonly TimeSpan _reconnectDelay;
    private readonly TimeSpan _connectTimeout;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<ReadOnlyMemory<byte>> _received = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private HdlcStreamEndpoint? _endpoint;
    private Task? _worker;
    private Task? _disposeTask;
    private bool _started;
    private bool _disposed;
    private int _receiving;
    private long _receivedBytes;
    private long _sentBytes;
    private int _reconnectAttempts;

    public TcpClientPacketInterface(string host, int port, long nominalBitrate = 10_000_000, bool reconnect = false,
        int? maxReconnectAttempts = null, int? fixedMtu = null, TimeSpan? reconnectDelay = null, TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(nominalBitrate, 1);
        if (maxReconnectAttempts is < 0) throw new ArgumentOutOfRangeException(nameof(maxReconnectAttempts));
        if (fixedMtu is < Packet.Mtu or > InterfaceAccessCode.MaximumPacketSize) throw new ArgumentOutOfRangeException(nameof(fixedMtu));
        _host = host;
        _port = port;
        Bitrate = nominalBitrate;
        HardwareMtu = fixedMtu ?? PacketInterfaceCapabilities.MtuForBitrate(nominalBitrate);
        _reconnect = reconnect;
        _maxReconnectAttempts = maxReconnectAttempts;
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(5);
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        if (_reconnectDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(reconnectDelay));
        if (_connectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectTimeout));
    }

    public long Bitrate { get; }
    public int HardwareMtu { get; }
    public bool SupportsMtuDiscovery => true;
    public bool IsConnected { get { lock (_gate) return !_disposed && _endpoint is { IsConnected: true }; } }
    public event Action? Connected;
    public event Action? Disconnected;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("The interface has already been started.");
            _started = true;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { await ConnectAsync(linked.Token).ConfigureAwait(false); }
        catch (Exception error) when (_reconnect && IsConnectionFailure(error) && !linked.IsCancellationRequested)
        {
            Report(error);
        }
        catch (OperationCanceledException) when (_reconnect && !linked.IsCancellationRequested)
        {
            Report(new TimeoutException("Initial TCP connection timed out."));
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

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            await socket.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        var endpoint = new HdlcStreamEndpoint(new NetworkStream(socket, ownsSocket: true),
            received: length => Interlocked.Add(ref _receivedBytes, length),
            sent: length => Interlocked.Add(ref _sentBytes, length),
            maxFrameLength: HardwareMtu + 64, authenticatedFrames: true);
        lock (_gate)
        {
            if (!_disposed) _endpoint = endpoint;
        }
        if (_disposed)
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(GetType().FullName);
        }
        Notify(Connected);
    }

    private static void Notify(Action? handlers)
    {
        if (handlers is { } observers)
        {
            foreach (Action observer in observers.GetInvocationList())
            {
                try { observer(); }
                catch (Exception error) { Report(error); }
            }
        }
    }

    private async Task RecoverAndReceiveAsync()
    {
        var token = _lifetime.Token;
        try
        {
            var attempts = 0;
            while (!token.IsCancellationRequested)
            {
                HdlcStreamEndpoint? endpoint;
                lock (_gate) endpoint = _endpoint;
                if (endpoint is null)
                {
                    if (_maxReconnectAttempts is { } maximum && attempts >= maximum) break;
                    await Task.Delay(_reconnectDelay, token).ConfigureAwait(false);
                    attempts++;
                    Interlocked.Increment(ref _reconnectAttempts);
                    try { await ConnectAsync(token).ConfigureAwait(false); }
                    catch (Exception error) when (IsConnectionFailure(error) && !token.IsCancellationRequested)
                    {
                        Report(error);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        Report(new TimeoutException("TCP reconnection timed out."));
                    }
                    continue;
                }
                attempts = 0;
                try
                {
                    await foreach (var packet in endpoint.ReceiveAsync(token).ConfigureAwait(false))
                        await _received.Writer.WriteAsync(packet, token).ConfigureAwait(false);
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
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started || (!_reconnect && _endpoint is null)) throw new InvalidOperationException("The interface has not been started.");
        }
        if (!_reconnect) return GetEndpoint()!.ReceiveAsync(cancellationToken);
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
        if (packet.IsEmpty || packet.Length > HardwareMtu + ((packet.Span[0] & 0x80) != 0 ? 64 : 0))
            throw new ArgumentException("Packet exceeds the bearer frame bound.", nameof(packet));
        var endpoint = GetEndpoint();
        if (endpoint is null) return;
        try { await endpoint.SendAsync(packet, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (_reconnect && IsConnectionFailure(error) && !cancellationToken.IsCancellationRequested)
        {
            Report(error);
            await endpoint.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsConnectionFailure(Exception error) => error is IOException or SocketException or ObjectDisposedException;
    private static void Report(Exception error) => System.Diagnostics.Trace.TraceError("TCP client interface: {0}", error);

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["rxb"] = Interlocked.Read(ref _receivedBytes),
            ["txb"] = Interlocked.Read(ref _sentBytes),
            ["target_host"] = _host,
            ["target_port"] = _port,
            ["bitrate"] = Bitrate,
            ["hardware_mtu"] = HardwareMtu,
            ["dotreticulum_bitrate_kind"] = "configured_estimate",
            ["reconnecting"] = _reconnect && !_disposed && _endpoint is null && _worker is { IsCompleted: false },
            ["reconnect_attempts"] = Volatile.Read(ref _reconnectAttempts),
            ["status"] = IsConnected,
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
        if (worker is not null) await worker.ConfigureAwait(false);
        _received.Writer.TryComplete();
    }

    private HdlcStreamEndpoint? GetEndpoint()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started) throw new InvalidOperationException("The interface has not been started.");
            if (!_reconnect && _endpoint is null) throw new InvalidOperationException("The interface has not been started.");
            return _endpoint;
        }
    }
}
