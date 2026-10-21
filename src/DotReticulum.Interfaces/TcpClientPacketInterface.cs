using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>
/// Connects to a Reticulum <c>TCPServerInterface</c> and exchanges HDLC-framed packets. There is no reconnect policy:
/// when the connection ends the receive stream completes (clean close) or faults (I/O error).
/// </summary>
public sealed class TcpClientPacketInterface : IPacketInterface, IPacketInterfaceDiagnostics
{
    private readonly string _host;
    private readonly int _port;
    private readonly long _bitrate;
    private readonly object _gate = new();
    private HdlcStreamEndpoint? _endpoint;
    private bool _started;
    private bool _disposed;

    public TcpClientPacketInterface(string host, int port, long nominalBitrate = 10_000_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(nominalBitrate, 1);
        _host = host;
        _port = port;
        _bitrate = nominalBitrate;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
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

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            await socket.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var endpoint = new HdlcStreamEndpoint(new NetworkStream(socket, ownsSocket: true));
        lock (_gate)
        {
            if (!_disposed)
            {
                _endpoint = endpoint;
                return;
            }
        }

        await endpoint.DisposeAsync().ConfigureAwait(false);
        throw new ObjectDisposedException(GetType().FullName);
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) =>
        GetEndpoint().ReceiveAsync(cancellationToken);

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
        GetEndpoint().SendAsync(packet, cancellationToken);

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["rxb"] = _endpoint?.ReceivedBytes ?? 0,
            ["txb"] = _endpoint?.SentBytes ?? 0,
            ["target_host"] = _host,
            ["target_port"] = _port,
            ["bitrate"] = _bitrate,
            ["dotreticulum_bitrate_kind"] = "configured_estimate",
            ["status"] = _endpoint is not null && !_disposed && _endpoint.IsConnected,
        };
    }

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
