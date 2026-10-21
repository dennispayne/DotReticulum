using System.Runtime.CompilerServices;

namespace DotReticulum.Interfaces;

/// <summary>HDLC packet transport over a duplex <see cref="Stream"/>: shared by TCP client, TCP server clients and serial.</summary>
internal sealed class HdlcStreamEndpoint : IAsyncDisposable
{
    private const int ReadBufferSize = 4096;

    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly HdlcFrameDecoder _decoder = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;
    private int _receiving;
    private long _receivedBytes;
    private long _sentBytes;
    private int _receiveEnded;
    private readonly Action<int>? _received;
    private readonly Action<int>? _sent;

    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public long SentBytes => Interlocked.Read(ref _sentBytes);

    public HdlcStreamEndpoint(Stream stream, IDisposable? owner = null, Action<int>? received = null, Action<int>? sent = null)
    {
        _stream = stream;
        _owner = owner;
        _received = received;
        _sent = sent;
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

        return ReceiveTrackedAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveTrackedAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var packet in ReceiveCoreAsync(cancellationToken).ConfigureAwait(false)) yield return packet;
        }
        finally { Volatile.Write(ref _receiveEnded, 1); }
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
                read = await _stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            }
            catch (Exception) when (IsDisposed && !cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (read == 0)
            {
                yield break;
            }

            frames.Clear();
            _decoder.Feed(buffer.AsSpan(0, read), frames);
            foreach (var frame in frames)
            {
                Interlocked.Add(ref _receivedBytes, frame.Length);
                _received?.Invoke(frame.Length);
                yield return frame;
            }
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var framed = HdlcPacketFraming.Encode(packet);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _writeLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(framed, linked.Token).ConfigureAwait(false);
            await _stream.FlushAsync(linked.Token).ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _disposeCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _owner?.Dispose();
        }
    }
}
