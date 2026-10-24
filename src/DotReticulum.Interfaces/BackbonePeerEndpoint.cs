using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;

namespace DotReticulum.Interfaces;

internal sealed class BackbonePeerEndpoint : IBackboneIngressPeer
{
    private readonly Socket _socket;
    private readonly BackbonePacketInterfaceOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<ReadOnlyMemory<byte>> _received;
    private readonly BackboneTransmitBuffer _transmit;
    private readonly SemaphoreSlim _ready = new(0, 1);
    private readonly object _gate = new();
    private readonly Action<int, bool> _account;
    private readonly Action _closed;
    private readonly BackboneEgressControl _egress = new(Now);
    private Task? _worker;
    private Task? _disposeTask;
    private long _receivedBytes;
    private long _ingressBytes;
    private long _receivedFrames;
    private long _windowIngressBytes;
    private long _windowIngressFrames;
    private long _ingressThrottleCount;
    private TaskCompletionSource? _ingressResume;
    private double _ingressHoldUntil;
    private long _drops;
    private long _droppedBytes;
    private volatile bool _connected;
    private volatile bool _stalled;

    internal static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public BackbonePeerEndpoint(Socket socket, BackbonePacketInterfaceOptions options, Action<int, bool> account, Action closed)
    {
        _socket = socket;
        _options = options;
        _account = account;
        _closed = closed;
        _received = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(options.ReceiveQueueFrames)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _transmit = new(options.TransmitQueueBytes, options.TransmitQueueFrames);
        socket.NoDelay = true;
        socket.ReceiveBufferSize = 32768;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 12);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            BackboneSocketOptions.ConfigureUserTimeout(socket);
    }

    public bool IsConnected => _connected;
    public bool IngressGated { get { lock (_gate) return _ingressResume is not null; } }
    public double IngressHoldUntil { get { lock (_gate) return _ingressHoldUntil; } }

    public (long Bytes, long Frames) TakeIngressSample() =>
        (Interlocked.Exchange(ref _windowIngressBytes, 0), Interlocked.Exchange(ref _windowIngressFrames, 0));

    public (long Bytes, long Frames) PeekIngressSample() =>
        (Interlocked.Read(ref _windowIngressBytes), Interlocked.Read(ref _windowIngressFrames));

    public void SetIngressGate(bool gated, double holdUntil = 0)
    {
        lock (_gate)
        {
            if (gated)
            {
                if (_ingressResume is not null) return;
                _ingressResume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ingressHoldUntil = holdUntil;
                Interlocked.Increment(ref _ingressThrottleCount);
            }
            else
            {
                _ingressResume?.TrySetResult();
                _ingressResume = null;
                _ingressHoldUntil = 0;
            }
        }
    }

    public void Start()
    {
        _connected = true;
        _worker = Task.Run(RunAsync);
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken token) =>
        _received.Reader.ReadAllAsync(token);

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var limit = _options.HardwareMtu + (!packet.IsEmpty && (packet.Span[0] & 0x80) != 0 ? _options.InterfaceAccessCodeSize : 0);
        if (packet.Length <= 19 || packet.Length > limit) throw new ArgumentException("Invalid Backbone frame length.", nameof(packet));
        lock (_gate)
        {
            if (!_connected || _disposeTask is not null) throw new IOException("The Backbone peer is offline.");
            var frame = HdlcPacketFraming.Encode(packet, limit, validatePacket: false);
            if (_stalled || !_transmit.Append(frame))
            {
                Interlocked.Increment(ref _drops);
                Interlocked.Add(ref _droppedBytes, frame.Length);
                throw new IOException("Backbone egress is stalled or its bounded transmit queue is full.");
            }
            if (_ready.CurrentCount == 0) _ready.Release();
        }
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync()
    {
        Exception? failure = null;
        var receive = ReadAsync();
        var transmit = WriteAsync();
        var monitor = MonitorAsync();
        try { await await Task.WhenAny(receive, transmit, monitor).ConfigureAwait(false); }
        catch (Exception error) when (_lifetime.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception error) { failure = error; Trace.TraceError("Backbone peer failed: {0}", error); }
        finally
        {
            _connected = false;
            await _lifetime.CancelAsync().ConfigureAwait(false);
            _socket.Dispose();
            foreach (var task in new[] { receive, transmit, monitor })
            {
                try { await task.ConfigureAwait(false); }
                catch (Exception error) when (_lifetime.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or SocketException) { }
                catch (Exception error) { Trace.TraceError("Backbone peer shutdown: {0}", error); }
            }
            _received.Writer.TryComplete(failure);
            _closed();
        }
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[32768];
        var decoder = new BackboneFrameDecoder(_options.HardwareMtu + _options.InterfaceAccessCodeSize);
        var frames = new List<byte[]>();
        while (true)
        {
            Task? ingressResume;
            lock (_gate) ingressResume = _ingressResume?.Task;
            if (ingressResume is not null) await ingressResume.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            var count = await _socket.ReceiveAsync(buffer, SocketFlags.None, _lifetime.Token).ConfigureAwait(false);
            if (count == 0) return;
            Interlocked.Add(ref _ingressBytes, count);
            Interlocked.Add(ref _windowIngressBytes, count);
            decoder.Feed(buffer.AsSpan(0, count), frames);
            foreach (var frame in frames)
            {
                Interlocked.Add(ref _receivedBytes, frame.Length);
                Interlocked.Increment(ref _receivedFrames);
                Interlocked.Increment(ref _windowIngressFrames);
                _account(frame.Length, false);
                await _received.Writer.WriteAsync(frame, _lifetime.Token).ConfigureAwait(false);
            }
            frames.Clear();
        }
    }

    private async Task WriteAsync()
    {
        while (true)
        {
            await _ready.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            while (_transmit.Buffered > 0)
            {
                var count = await _transmit.DrainAsync(
                    (memory, token) => _socket.SendAsync(memory, SocketFlags.None, token), _lifetime.Token).ConfigureAwait(false);
                if (count > 0) _account(count, true);
                else await Task.Delay(10, _lifetime.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
        {
            if (_egress.Evaluate(_transmit.Buffered, _transmit.Sent, Now))
                throw new IOException("Backbone egress made no progress for 12 seconds.");
            _stalled = _egress.Stalled;
        }
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics() => new Dictionary<string, object?>
    {
        ["rxb"] = Interlocked.Read(ref _receivedBytes), ["txb"] = _transmit.Sent,
        ["ingress_wire_bytes"] = Interlocked.Read(ref _ingressBytes), ["ingress_packets"] = Interlocked.Read(ref _receivedFrames),
        ["tx_buffered_bytes"] = _transmit.Buffered, ["tx_buffered_frames"] = _transmit.Frames,
        ["tx_stalled"] = _stalled, ["tx_drops"] = Interlocked.Read(ref _drops),
        ["tx_dropped_bytes"] = Interlocked.Read(ref _droppedBytes), ["status"] = IsConnected,
        ["receive_queue_frames"] = _received.Reader.Count,
        ["ingress_throttled"] = IngressGated, ["ingress_throttle_count"] = Interlocked.Read(ref _ingressThrottleCount),
    };

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        else _received.Writer.TryComplete();
    }
}
