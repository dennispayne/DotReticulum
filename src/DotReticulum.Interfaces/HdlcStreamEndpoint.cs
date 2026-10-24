using System.Runtime.CompilerServices;

namespace DotReticulum.Interfaces;

/// <summary>HDLC packet transport over a duplex <see cref="Stream"/>: shared by TCP client, TCP server clients and serial.</summary>
internal sealed class HdlcStreamEndpoint : IAsyncDisposable
{
    private const int ReadBufferSize = 4096;

    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly HdlcFrameDecoder _decoder;
    private readonly TcpKissFrameDecoder? _kissDecoder;
    private readonly bool _serialFraming;
    private readonly int _maxFrameLength;
    private readonly int _unprotectedFrameLength;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;
    private int _receiving;
    private long _receivedBytes;
    private long _sentBytes;
    private int _receiveEnded;
    private readonly Action<int>? _received;
    private readonly Action<int>? _sent;

    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public long SentBytes => Interlocked.Read(ref _sentBytes);

    public HdlcStreamEndpoint(Stream stream, IDisposable? owner = null, Action<int>? received = null, Action<int>? sent = null,
        int maxFrameLength = DotReticulum.Core.Packet.Mtu, bool authenticatedFrames = false,
        bool kissFraming = false, bool serialFraming = false, int? unprotectedFrameLength = null)
    {
        _stream = stream;
        _owner = owner;
        _received = received;
        _sent = sent;
        _maxFrameLength = maxFrameLength;
        _unprotectedFrameLength = unprotectedFrameLength ?? maxFrameLength;
        _decoder = new HdlcFrameDecoder(maxFrameLength, validatePackets: !authenticatedFrames, sharedFlags: !serialFraming);
        _kissDecoder = kissFraming ? new TcpKissFrameDecoder(maxFrameLength) : null;
        _serialFraming = serialFraming;
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public bool IsConnected => !IsDisposed && Volatile.Read(ref _receiveEnded) == 0;

    public CancellationToken DisposedToken => _disposeCts.Token;

    /// <summary>
    /// Single-enumerator stream of decoded packets. Completes on end of stream or disposal; an I/O failure that was
    /// not caused by disposal faults the stream with the underlying exception.
    /// </summary>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _receiving, 1) != 0)
        {
            throw new InvalidOperationException("ReceiveAsync supports a single enumerator.");
        }

        return ReceiveCoreAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var buffer = new byte[ReadBufferSize];
        var frames = new List<byte[]>();
        while (true)
        {
            int read;
            try
            {
                var pending = _stream.ReadAsync(buffer, linked.Token).AsTask();
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = linked.Token.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);
                if (_serialFraming)
                {
                    var idleStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    while (!pending.IsCompleted)
                    {
                        using var pollLifetime = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                        var poll = Task.Delay(TimeSpan.FromMilliseconds(80), pollLifetime.Token);
                        var ready = await Task.WhenAny(pending, poll, cancelled.Task).ConfigureAwait(false);
                        await pollLifetime.CancelAsync().ConfigureAwait(false);
                        if (ready == pending || linked.IsCancellationRequested) break;
                        if (System.Diagnostics.Stopwatch.GetElapsedTime(idleStarted) > TimeSpan.FromMilliseconds(100))
                            _decoder.ExpirePartial();
                    }
                }
                else if (!pending.IsCompleted)
                    await Task.WhenAny(pending, cancelled.Task).ConfigureAwait(false);
                if (linked.IsCancellationRequested)
                {
                    // Some serial drivers ignore read cancellation but unblock when the owned handle closes.
                    await DisposeAsync().ConfigureAwait(false);
                    try { await pending.ConfigureAwait(false); }
                    catch (Exception) when (linked.IsCancellationRequested) { linked.Token.ThrowIfCancellationRequested(); }
                    linked.Token.ThrowIfCancellationRequested();
                }
                read = await pending.ConfigureAwait(false);
            }
            catch (Exception) when (IsDisposed && !cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                Volatile.Write(ref _receiveEnded, 1);
                System.Diagnostics.Trace.TraceError("Stream bearer read failed: {0}", error);
                throw;
            }

            if (read == 0)
            {
                Volatile.Write(ref _receiveEnded, 1);
                yield break;
            }

            frames.Clear();
            if (_kissDecoder is { } kiss) kiss.Feed(buffer.AsSpan(0, read), frames);
            else _decoder.Feed(buffer.AsSpan(0, read), frames);
            foreach (var frame in frames)
            {
                if (frame.Length > _unprotectedFrameLength + ((frame[0] & 0x80) != 0 ? 64 : 0)) continue;
                Interlocked.Add(ref _receivedBytes, frame.Length);
                _received?.Invoke(frame.Length);
                yield return frame;
            }
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _writeLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var validate = packet.IsEmpty || (packet.Span[0] & 0x80) == 0;
            if (packet.IsEmpty || packet.Length > _maxFrameLength ||
                packet.Length > _unprotectedFrameLength + ((packet.Span[0] & 0x80) != 0 ? 64 : 0) ||
                (validate && !DotReticulum.Core.Packet.TryParse(packet, out _, _maxFrameLength)))
                throw new ArgumentException("Not a valid Reticulum packet (or larger than the MTU).", nameof(packet));
            var framed = _kissDecoder is null
                ? HdlcPacketFraming.Encode(packet, _maxFrameLength, validatePacket: validate)
                : TcpKissFrameDecoder.Encode(packet.Span);
            await CompleteWriteAsync(_stream.WriteAsync(framed, linked.Token).AsTask(), linked.Token).ConfigureAwait(false);
            await CompleteWriteAsync(_stream.FlushAsync(linked.Token), linked.Token).ConfigureAwait(false);
            Interlocked.Add(ref _sentBytes, framed.Length);
            _sent?.Invoke(framed.Length);
        }
        catch (OperationCanceledException) when (IsDisposed && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(HdlcStreamEndpoint));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask CompleteWriteAsync(Task pending, CancellationToken cancellationToken)
    {
        if (!pending.IsCompleted)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);
            await Task.WhenAny(pending, cancelled.Task).ConfigureAwait(false);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            try { await pending.ConfigureAwait(false); }
            catch (Exception) when (cancellationToken.IsCancellationRequested) { cancellationToken.ThrowIfCancellationRequested(); }
            cancellationToken.ThrowIfCancellationRequested();
        }
        await pending.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            Volatile.Write(ref _disposed, 1);
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        try
        {
            await _disposeCts.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await _stream.DisposeAsync().ConfigureAwait(false); }
            finally { _owner?.Dispose(); }
        }
    }
}
