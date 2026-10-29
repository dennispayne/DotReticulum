using System.IO.Ports;

namespace DotReticulum.Interfaces;

/// <summary>
/// An exclusively owned 115200-baud maintenance stream. Opening does not probe,
/// configure, start the radio, or send packet data.
/// </summary>
public sealed class RNodeMaintenanceConnection : Stream
{
    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private int _disposed;

    private RNodeMaintenanceConnection(Stream stream, IDisposable? owner)
    {
        _stream = stream;
        _owner = owner;
    }

    public static async ValueTask<RNodeMaintenanceConnection> OpenSerialAsync(string port, CancellationToken cancellationToken = default)
    {
        var connection = await RNodeDevice.SerialFactory(port)(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(connection.Stream, connection.Owner);
        }
        catch
        {
            connection.Stream.Dispose();
            connection.Owner?.Dispose();
            throw;
        }
    }

    /// <summary>Requests nRF52 DFU by opening at 1200 baud and closing; does not flash firmware.</summary>
    public static async Task EnterDfuAsync(string port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        if (port.Contains("://", StringComparison.Ordinal))
            throw new NotSupportedException("DFU entry requires a local USB serial port.");
        cancellationToken.ThrowIfCancellationRequested();
        using var serial = NativeSerialPort.Create(port, 1200, Parity.None, 8, StopBits.One, 100, 1000);
        serial.Open();
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
    }

    public override bool CanRead => _disposed == 0 && _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _disposed == 0 && _stream.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _stream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.Register(Dispose);
        try { return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.Register(Dispose);
        try { await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _stream.Dispose(); }
            finally { _owner?.Dispose(); }
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
