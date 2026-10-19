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
    private readonly int _streamId;
    private readonly Channel _channel;
    private readonly Func<MessageBase, bool> _handler;
    private readonly object _lock = new();
    private readonly List<Action<int>> _listeners = [];
    private byte[] _buffer = [];
    private bool _eof;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RawChannelReader(int streamId, Channel channel)
    {
        _streamId = streamId;
        _channel = channel;
        _handler = Handle;
        channel.RegisterSystemMessageType<StreamDataMessage>();
        channel.AddMessageHandler(_handler);
    }

    /// <summary>Number of bytes buffered and ready to read.</summary>
    public int Available
    {
        get
        {
            lock (_lock)
            {
                return _buffer.Length;
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
                return _eof;
            }
        }
    }

    public void AddReadyCallback(Action<int> callback)
    {
        lock (_lock)
        {
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

        Action<int>[] listeners;
        int ready;
        TaskCompletionSource signal;
        lock (_lock)
        {
            _buffer = [.. _buffer, .. data.Data];
            if (data.Eof)
            {
                _eof = true;
            }

            ready = _buffer.Length;
            listeners = [.. _listeners];
            signal = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
        foreach (var listener in listeners)
        {
            _ = Task.Run(() => listener(ready));
        }

        return true;
    }

    /// <summary>Waits until data is available or the stream ended; returns 0 at end of stream.</summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_buffer.Length > 0)
                {
                    var n = Math.Min(buffer.Length, _buffer.Length);
                    _buffer.AsSpan(0, n).CopyTo(buffer.Span);
                    _buffer = _buffer[n..];
                    return n;
                }

                if (_eof || buffer.Length == 0)
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
        var temp = new byte[buffer.Length];
        var n = ReadAsync(temp).AsTask().GetAwaiter().GetResult();
        temp.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _channel.RemoveMessageHandler(_handler);
            lock (_lock)
            {
                _listeners.Clear();
            }
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => true;

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
    private bool _eof;
    private bool _closed;

    public RawChannelWriter(int streamId, Channel channel)
    {
        _streamId = streamId;
        _channel = channel;
        _mdu = channel.Mdu - StreamDataMessage.HeaderLength;
    }

    /// <summary>Sends at most one chunk of <paramref name="data"/>; returns the number of bytes consumed (0 when the channel is not ready).</summary>
    public int WriteChunk(ReadOnlySpan<byte> data)
    {
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

            _channel.Send(new StreamDataMessage(_streamId, chunk, _eof, success));
            return processed;
        }
        catch (ChannelException ex) when (ex.Type == ChannelExceptionType.LinkNotReady)
        {
            return 0;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            var written = WriteChunk(buffer.Span);
            if (written > 0)
            {
                buffer = buffer[written..];
                continue;
            }

            if (_channel.Link.Status == LinkStatus.Closed)
            {
                throw new IOException("The link was closed while writing to the channel stream.");
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer) => WriteAsync(buffer.ToArray()).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <summary>Signals end of stream to the remote reader once pending messages are out.</summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !_channel.IsReadyToSend)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        _eof = true;
        while (DateTime.UtcNow < deadline && _channel.Link.Status != LinkStatus.Closed)
        {
            try
            {
                _channel.Send(new StreamDataMessage(_streamId, [], eof: true));
                return;
            }
            catch (ChannelException ex) when (ex.Type == ChannelExceptionType.LinkNotReady)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
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

    public override bool CanWrite => true;

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
    public static RawChannelReader CreateReader(int streamId, Channel channel, Action<int>? readyCallback = null)
    {
        var reader = new RawChannelReader(streamId, channel);
        if (readyCallback is not null)
        {
            reader.AddReadyCallback(readyCallback);
        }

        return reader;
    }

    public static RawChannelWriter CreateWriter(int streamId, Channel channel) => new(streamId, channel);

    public static (RawChannelReader Reader, RawChannelWriter Writer) CreateBidirectional(int receiveStreamId, int sendStreamId, Channel channel, Action<int>? readyCallback = null) =>
        (CreateReader(receiveStreamId, channel, readyCallback), CreateWriter(sendStreamId, channel));
}
