using System.IO.Ports;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>
/// HDLC packet interface over a serial port (via System.IO.Ports) or any caller-provided duplex <see cref="Stream"/>.
/// Framing is the simplified HDLC used by upstream's SerialInterface. Unlike upstream there is no idle-timeout that
/// resets a partial frame; a corrupt or truncated frame is dropped when the next flag arrives.
/// </summary>
public sealed class SerialPacketInterface : IPacketInterface
{
    private readonly Func<(Stream Stream, IDisposable? Owner)> _open;
    private readonly object _gate = new();
    private readonly Stream? _providedStream;
    private HdlcStreamEndpoint? _endpoint;
    private bool _started;
    private bool _disposed;

    /// <summary>Uses a real serial port (8 data bits, no parity, 1 stop bit by default, as upstream).</summary>
    public SerialPacketInterface(string portName, int baudRate = 9600, Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentOutOfRangeException.ThrowIfLessThan(baudRate, 1);
        _open = () =>
        {
            var port = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
            try
            {
                port.Open();
                return (port.BaseStream, port);
            }
            catch
            {
                port.Dispose();
                throw;
            }
        };
    }

    /// <summary>Uses a duplex stream (for tests, pseudo-terminals or other byte pipes). The interface takes ownership.</summary>
    public SerialPacketInterface(Stream duplexStream)
    {
        ArgumentNullException.ThrowIfNull(duplexStream);
        if (!duplexStream.CanRead || !duplexStream.CanWrite)
        {
            throw new ArgumentException("The stream must be readable and writable.", nameof(duplexStream));
        }

        _providedStream = duplexStream;
        _open = () => (duplexStream, null);
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The interface has already been started.");
            }

            _started = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (stream, owner) = _open();
        var endpoint = new HdlcStreamEndpoint(stream, owner);
        lock (_gate)
        {
            if (!_disposed)
            {
                _endpoint = endpoint;
                return ValueTask.CompletedTask;
            }
        }

        return DisposeThenThrowAsync(endpoint);
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) =>
        GetEndpoint().ReceiveAsync(cancellationToken);

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
        GetEndpoint().SendAsync(packet, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        HdlcStreamEndpoint? endpoint;
        lock (_gate)
        {
            _disposed = true;
            endpoint = _endpoint;
        }

        if (endpoint is not null)
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
        }
        else if (_providedStream is not null)
        {
            await _providedStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask DisposeThenThrowAsync(HdlcStreamEndpoint endpoint)
    {
        await endpoint.DisposeAsync().ConfigureAwait(false);
        throw new ObjectDisposedException(GetType().FullName);
    }

    private HdlcStreamEndpoint GetEndpoint()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _endpoint ?? throw new InvalidOperationException("The interface has not been started.");
        }
    }
}
