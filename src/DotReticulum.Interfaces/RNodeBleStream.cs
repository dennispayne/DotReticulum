namespace DotReticulum.Interfaces;

internal sealed class RNodeBleStream : Stream
{
    private readonly IRNodeBleGatt _gatt;
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _reader = new(1, 1);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _changed = Signal();
    private Task? _dispose;
    private Exception? _failure;
    private int _head;
    private int _count;
    private bool _disposed;

    private RNodeBleStream(IRNodeBleGatt gatt, int receiveBufferBytes)
    {
        if (receiveBufferBytes is < 512 or > 1048576) throw new ArgumentOutOfRangeException(nameof(receiveBufferBytes));
        if (gatt.MaxWritePayload is < 1 or > 512) throw new IOException("Invalid BLE write payload bound.");
        _gatt = gatt;
        _buffer = new byte[receiveBufferBytes];
        gatt.Received += Receive;
        gatt.Disconnected += Fail;
    }

    internal static async ValueTask<RNodeBleStream> OpenAsync(IRNodeBleGatt gatt, int receiveBufferBytes,
        CancellationToken cancellationToken)
    {
        RNodeBleStream? stream = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream = new(gatt, receiveBufferBytes);
            await gatt.StartNotificationsAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return stream;
        }
        catch
        {
            if (stream is null) await gatt.DisposeAsync().ConfigureAwait(false);
            else await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Receive(ReadOnlyMemory<byte> data)
    {
        lock (_gate)
        {
            if (_disposed || _failure is not null) return;
            if (data.Length > 512 || data.Length > _buffer.Length - _count)
            {
                Fail(new IOException("RNode BLE notification buffer overflow; the connection must be reopened."));
                return;
            }
            if (data.IsEmpty) return;
            var tail = (_head + _count) % _buffer.Length;
            var first = Math.Min(data.Length, _buffer.Length - tail);
            data.Span[..first].CopyTo(_buffer.AsSpan(tail));
            data.Span[first..].CopyTo(_buffer);
            _count += data.Length;
            _changed.TrySetResult();
        }
    }

    private void Fail(Exception exception)
    {
        lock (_gate)
        {
            if (_disposed || _failure is not null) return;
            _failure = exception;
            _count = 0;
            _changed.TrySetResult();
            _stop.Cancel();
        }
    }

    private void ThrowUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null) throw new IOException("RNode BLE connection failed.", _failure);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _reader.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    ThrowUnavailable();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (buffer.IsEmpty) return 0;
                    if (_count != 0)
                    {
                        var length = Math.Min(buffer.Length, _count);
                        var first = Math.Min(length, _buffer.Length - _head);
                        _buffer.AsSpan(_head, first).CopyTo(buffer.Span);
                        _buffer.AsSpan(0, length - first).CopyTo(buffer.Span[first..]);
                        _head = (_head + length) % _buffer.Length;
                        _count -= length;
                        if (_count == 0) _changed = Signal();
                        return length;
                    }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _reader.Release(); }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (_gate) ThrowUnavailable();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try { await _writer.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_gate) ThrowUnavailable();
            throw;
        }
        var attempted = false;
        try
        {
            lock (_gate) ThrowUnavailable();
            while (!buffer.IsEmpty)
            {
                linked.Token.ThrowIfCancellationRequested();
                var length = Math.Min(buffer.Length, _gatt.MaxWritePayload);
                attempted = true;
                await _gatt.WriteWithoutResponseAsync(buffer[..length], linked.Token).ConfigureAwait(false);
                buffer = buffer[length..];
            }
        }
        catch (Exception exception)
        {
            // A cancelled or failed GATT call may already have transmitted bytes.
            // Tear down the session rather than replaying the frame's remainder.
            if (attempted) Fail(exception);
            throw;
        }
        finally { _writer.Release(); }
    }

    public override ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true;
            _count = 0;
            _changed.TrySetResult();
            _stop.Cancel();
            _gatt.Received -= Receive;
            _gatt.Disconnected -= Fail;
            _dispose = DisposeCoreAsync();
            return new(_dispose);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try { await _gatt.DisposeAsync().ConfigureAwait(false); }
        finally { _stop.Dispose(); _writer.Release(); }
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override bool CanRead => !_disposed;
    public override bool CanWrite => !_disposed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { lock (_gate) ThrowUnavailable(); }
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
