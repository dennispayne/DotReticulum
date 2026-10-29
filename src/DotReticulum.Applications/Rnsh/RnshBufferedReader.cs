using System.Threading.Channels;

namespace DotReticulum.Applications.Rnsh;

// Darwin drains a session leader's controlling terminal before reporting exit.
// Read ahead independently of the caller, with bounded backpressure.
internal sealed class RnshBufferedReader : Stream
{
    private readonly Stream _source;
    private readonly bool _discard;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<ReadOnlyMemory<byte>> _pending = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
    private readonly Task _pump;
    private ReadOnlyMemory<byte> _current;
    private bool _disposed;

    internal RnshBufferedReader(Stream source, bool discard = false)
    {
        _source = source;
        _discard = discard;
        _pump = PumpAsync();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                var buffer = new byte[4096];
                var count = await _source.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (!_discard)
                    await _pending.Writer.WriteAsync(buffer.AsMemory(0, count), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pending.Writer.TryComplete(ex);
            if (_discard) throw;
        }
        finally
        {
            _pending.Writer.TryComplete();
            await _source.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0) return 0;
        if (_current.IsEmpty)
        {
            if (!await _pending.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            if (!_pending.Reader.TryRead(out _current)) throw new IOException("rnsh output reader has multiple consumers.");
        }
        var count = Math.Min(buffer.Length, _current.Length);
        _current[..count].CopyTo(buffer);
        _current = _current[count..];
        return count;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _pump.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
