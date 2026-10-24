using System.Buffers.Binary;

namespace DotReticulum.Transport;

/// <summary>Stream data carried over a <see cref="Channel"/> (upstream <c>StreamDataMessage</c>, system message type 0xFF00).</summary>
public sealed class StreamDataMessage : MessageBase
{
    public const ushort StreamDataMsgType = 0xFF00;
    public const int StreamIdMax = 0x3FFF;
    public const int HeaderLength = 2;

    public StreamDataMessage()
    {
    }

    public StreamDataMessage(int streamId, byte[] data, bool eof = false, bool compressed = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (streamId is < 0 or > StreamIdMax)
        {
            throw new ArgumentOutOfRangeException(nameof(streamId), "stream_id must be 0-16383");
        }

        StreamId = streamId;
        Data = data;
        Eof = eof;
        Compressed = compressed;
    }

    public override ushort MsgType => StreamDataMsgType;

    public int StreamId { get; private set; }

    public byte[] Data { get; private set; } = [];

    public bool Eof { get; private set; }

    /// <summary>True when <see cref="Data"/> is a bz2 stream on the wire (it is decompressed on unpack).</summary>
    public bool Compressed { get; private set; }

    public override byte[] Pack()
    {
        var header = (ushort)((StreamId & 0x3FFF) | (Eof ? 0x8000 : 0) | (Compressed ? 0x4000 : 0));
        var raw = new byte[HeaderLength + Data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(raw, header);
        Data.CopyTo(raw, HeaderLength);
        return raw;
    }

    public override void Unpack(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < HeaderLength) throw new IOException("Stream message header is truncated.");
        var header = BinaryPrimitives.ReadUInt16BigEndian(raw);
        Eof = (header & 0x8000) != 0;
        Compressed = (header & 0x4000) != 0;
        StreamId = header & 0x3FFF;
        Data = raw[HeaderLength..].ToArray();
        if (Compressed)
        {
            Data = Bz2.Decompress(Data, RawChannelWriter.MaxChunkLength);
        }
    }
}

/// <summary>Readable stream fed by <see cref="StreamDataMessage"/>s of one stream id (upstream <c>RawChannelReader</c>).</summary>
public sealed class RawChannelReader : Stream
{
    public const int DefaultMaxBufferedBytes = 1024 * 1024;
    public const int MaximumReadyCallbacks = 64;
    private readonly int _streamId;
    private readonly Channel _channel;
    private readonly Func<MessageBase, bool> _handler;
    private readonly object _lock = new();
    private readonly List<Action<int>> _listeners = [];
    private byte[]? _buffer;
    private int _readOffset;
    private int _bufferedBytes;
    private bool _eof;
    private bool _disposed;
    private IOException? _failure;
    private bool _callbackPending;
    private bool _callbackRunning;
    private Task _callbackTask = Task.CompletedTask;
    private readonly CancellationTokenRegistration _closedRegistration;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RawChannelReader(int streamId, Channel channel, int maxBufferedBytes = DefaultMaxBufferedBytes)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (streamId is < 0 or > StreamDataMessage.StreamIdMax) throw new ArgumentOutOfRangeException(nameof(streamId));
        if (maxBufferedBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
        _streamId = streamId;
        _channel = channel;
        MaxBufferedBytes = maxBufferedBytes;
        _handler = Handle;
        channel.RegisterSystemMessageType<StreamDataMessage>();
        channel.AddSystemMessageHandler(_handler);
        _closedRegistration = channel.Closed.UnsafeRegister(
            static state => ((RawChannelReader)state!).OnChannelClosed(), this);
    }

    private void OnChannelClosed()
    {
        lock (_lock)
        {
            _eof = true;
            _signal.TrySetResult();
        }
    }

    /// <summary>Unread-byte limit. Exceeding it fails the reader and tears down its link, rather than acknowledging silent loss.</summary>
    public int MaxBufferedBytes { get; }

    /// <summary>Currently owned callback work. Disposal stops queued callbacks; an already executing user callback must return itself.</summary>
    public Task ReadyCallbacksCompletion
    {
        get
        {
            lock (_lock) return _callbackTask;
        }
    }

    /// <summary>Number of bytes buffered and ready to read.</summary>
    public int Available
    {
        get
        {
            lock (_lock)
            {
                return _bufferedBytes;
            }
        }
    }

    /// <summary>True once the sender signalled end of stream (data may still be buffered).</summary>
    public bool EndOfStream
    {
        get
        {
            lock (_lock)
            {
                return _eof || _channel.Completion.IsCompleted;
            }
        }
    }

    public void AddReadyCallback(Action<int> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listeners.Count == MaximumReadyCallbacks)
                throw new InvalidOperationException("The stream ready-callback limit has been reached.");
            _listeners.Add(callback);
        }
    }

    public void RemoveReadyCallback(Action<int> callback)
    {
        lock (_lock)
        {
            _listeners.Remove(callback);
        }
    }

    private bool Handle(MessageBase message)
    {
        if (message is not StreamDataMessage data || data.StreamId != _streamId)
        {
            return false;
        }

        TaskCompletionSource signal;
        var overflow = false;
        lock (_lock)
        {
            if (_disposed || _eof) return true;
            if (data.Data.Length > MaxBufferedBytes - _bufferedBytes)
            {
                _failure = new IOException($"Channel stream {_streamId} exceeded its {MaxBufferedBytes}-byte unread buffer limit.");
                _eof = true;
                _buffer = null;
                _readOffset = _bufferedBytes = 0;
                overflow = true;
            }
            else
            {
                if (data.Data.Length > 0)
                {
                    _buffer ??= GC.AllocateUninitializedArray<byte>(MaxBufferedBytes);
                    var tail = (int)(((long)_readOffset + _bufferedBytes) % MaxBufferedBytes);
                    var first = Math.Min(data.Data.Length, MaxBufferedBytes - tail);
                    data.Data.AsSpan(0, first).CopyTo(_buffer.AsSpan(tail));
                    data.Data.AsSpan(first).CopyTo(_buffer);
                    _bufferedBytes += data.Data.Length;
                }

                _eof = data.Eof;
                _callbackPending = true;
                if (!_callbackRunning && _listeners.Count > 0)
                {
                    _callbackRunning = true;
                    _callbackTask = Task.Run(RunReadyCallbacks);
                }
            }

            signal = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
        if (overflow)
        {
            _channel.RemoveSystemMessageHandler(_handler);
            _channel.Abort(_failure!.Message);
        }

        return true;
    }

    private void RunReadyCallbacks()
    {
        while (true)
        {
            Action<int>[] listeners;
            int ready;
            lock (_lock)
            {
                if (_disposed || _failure is not null || !_callbackPending)
                {
                    _callbackRunning = false;
                    return;
                }

                _callbackPending = false;
                listeners = [.. _listeners];
                ready = _bufferedBytes;
            }

            foreach (var listener in listeners)
            {
                lock (_lock)
                {
                    if (_disposed) break;
                }

                try
                {
                    listener(ready);
                }
                catch (Exception ex)
                {
                    _channel.Log($"Channel stream {_streamId} ready callback failed: {ex}");
                }
            }
        }
    }

    /// <summary>Waits until data is available or the stream ended; returns 0 at end of stream.</summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_failure is not null) throw _failure;
                if (buffer.IsEmpty) return 0;
                if (_bufferedBytes > 0)
                {
                    return ReadBuffered(buffer.Span);
                }

                if (_eof || _channel.Completion.IsCompleted)
                {
                    return 0;
                }

                wait = _signal.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_failure is not null) throw _failure;
                if (buffer.IsEmpty) return 0;
                if (_bufferedBytes > 0) return ReadBuffered(buffer);
                if (_eof || _channel.Completion.IsCompleted) return 0;
                wait = _signal.Task;
            }

            wait.GetAwaiter().GetResult();
        }
    }

    private int ReadBuffered(Span<byte> buffer)
    {
        var copied = Math.Min(buffer.Length, _bufferedBytes);
        var first = Math.Min(copied, MaxBufferedBytes - _readOffset);
        _buffer.AsSpan(_readOffset, first).CopyTo(buffer);
        _buffer.AsSpan(0, copied - first).CopyTo(buffer[first..]);
        _readOffset = (int)(((long)_readOffset + copied) % MaxBufferedBytes);
        _bufferedBytes -= copied;
        return copied;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _channel.RemoveSystemMessageHandler(_handler);
            lock (_lock)
            {
                _disposed = true;
                _eof = true;
                _buffer = null;
                _bufferedBytes = _readOffset = 0;
                _listeners.Clear();
                _callbackPending = false;
                _signal.TrySetResult();
            }

            _closedRegistration.Dispose();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Writable stream that sends data as <see cref="StreamDataMessage"/>s, bz2-compressing chunks when worthwhile (upstream <c>RawChannelWriter</c>).</summary>
public sealed class RawChannelWriter : Stream
{
    public const int MaxChunkLength = 1024 * 16;
    public const int CompressionTries = 4;

    private readonly int _streamId;
    private readonly Channel _channel;
    private readonly int _mdu;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _closeLock = new();
    private bool _closeRequested;
    private bool _eofSent;
    private Task? _closeTask;

    public RawChannelWriter(int streamId, Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (streamId is < 0 or > StreamDataMessage.StreamIdMax) throw new ArgumentOutOfRangeException(nameof(streamId));
        _streamId = streamId;
        _channel = channel;
        _mdu = channel.Mdu - StreamDataMessage.HeaderLength;
    }

    /// <summary>Sends at most one chunk of <paramref name="data"/>; returns the number of bytes consumed (0 when the channel is not ready).</summary>
    public int WriteChunk(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closeRequested), this);
        if (!_operation.Wait(0)) return 0;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closeRequested), this);
            return WriteChunkCore(data);
        }
        finally
        {
            _operation.Release();
        }
    }

    private int WriteChunkCore(ReadOnlySpan<byte> data)
    {
        if (!_channel.IsReadyToSend) return 0;
        try
        {
            var chunkLength = Math.Min(data.Length, MaxChunkLength);
            data = data[..chunkLength];
            var tries = 1;
            var success = false;
            var segmentLength = 0;
            byte[] compressed = [];
            while (chunkLength > 32 && tries < CompressionTries)
            {
                segmentLength = chunkLength / tries;
                compressed = Bz2.Compress(data[..segmentLength]);
                if (compressed.Length < _mdu && compressed.Length < segmentLength)
                {
                    success = true;
                    break;
                }

                tries++;
            }

            byte[] chunk;
            int processed;
            if (success)
            {
                chunk = compressed;
                processed = segmentLength;
            }
            else
            {
                chunk = data[..Math.Min(data.Length, _mdu)].ToArray();
                processed = chunk.Length;
            }

            _channel.Send(new StreamDataMessage(_streamId, chunk, eof: false, compressed: success));
            return processed;
        }
        catch (ChannelException ex) when (ex.Type == ChannelExceptionType.LinkNotReady)
        {
            return 0;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closeRequested), this);
        cancellationToken.ThrowIfCancellationRequested();
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closeRequested), this);
            while (!buffer.IsEmpty)
            {
                await _channel.WaitForSendAsync(cancellationToken).ConfigureAwait(false);
                var written = WriteChunkCore(buffer.Span);
                if (written > 0)
                {
                    buffer = buffer[written..];
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer) => WriteAsync(buffer.ToArray()).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <summary>Signals end of stream to the remote reader once pending messages are out.</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_closeLock)
        {
            if (_closeTask is null || (!_eofSent && (_closeTask.IsCanceled || _closeTask.IsFaulted)))
            {
                Volatile.Write(ref _closeRequested, true);
                _closeTask = CloseCoreAsync(cancellationToken);
            }

            return _closeTask;
        }
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await _operation.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                while (!_eofSent && !_channel.Completion.IsCompleted)
                {
                    await _channel.WaitForSendAsync(deadline.Token).ConfigureAwait(false);
                    try
                    {
                        _channel.Send(new StreamDataMessage(_streamId, [], eof: true));
                        _eofSent = true;
                    }
                    catch (ChannelException ex) when (ex.Type == ChannelExceptionType.LinkNotReady)
                    {
                        // Another channel writer may have taken the available slot.
                    }
                }
            }
            catch (IOException) when (_channel.Completion.IsCompleted)
            {
                // Link teardown also ends this stream; there is no peer left to signal.
            }
            finally
            {
                _operation.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Timed out waiting to send channel stream EOF.");
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseAsync().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !Volatile.Read(ref _closeRequested);

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Factories for stream readers/writers over a channel (upstream <c>RNS.Buffer</c>).</summary>
public static class ChannelBuffer
{
    public static RawChannelReader CreateReader(int streamId, Channel channel, Action<int>? readyCallback = null,
        int maxBufferedBytes = RawChannelReader.DefaultMaxBufferedBytes)
    {
        var reader = new RawChannelReader(streamId, channel, maxBufferedBytes);
        if (readyCallback is not null)
        {
            reader.AddReadyCallback(readyCallback);
        }

        return reader;
    }

    public static RawChannelWriter CreateWriter(int streamId, Channel channel) => new(streamId, channel);

    public static (RawChannelReader Reader, RawChannelWriter Writer) CreateBidirectional(int receiveStreamId, int sendStreamId,
        Channel channel, Action<int>? readyCallback = null, int maxBufferedBytes = RawChannelReader.DefaultMaxBufferedBytes) =>
        (CreateReader(receiveStreamId, channel, readyCallback, maxBufferedBytes), CreateWriter(sendStreamId, channel));
}
