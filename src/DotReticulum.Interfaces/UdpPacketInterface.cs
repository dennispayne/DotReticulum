using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>
/// One packet per UDP datagram, as upstream's UDPInterface. Binds <c>localEndpoint</c> for receiving and sends every
/// packet to <c>peerEndpoint</c> (a unicast peer or a broadcast address; SO_BROADCAST is enabled like upstream).
/// Datagrams from any source are accepted.
/// </summary>
public sealed class UdpPacketInterface : IPacketInterface, IPacketInterfaceDiagnostics
{
    // Upstream UDPInterface HW_MTU; larger datagrams are still handed over and then dropped by the manager's parser.
    private const int ReceiveBufferSize = 1064;

    private readonly IPEndPoint _local;
    private readonly IPEndPoint _peer;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _gate = new();
    private Socket? _socket;
    private bool _started;
    private bool _disposed;
    private int _receiving;
    private long _receivedBytes;
    private long _sentBytes;

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["rxb"] = Interlocked.Read(ref _receivedBytes),
            ["txb"] = Interlocked.Read(ref _sentBytes),
            ["bitrate"] = 10_000_000L,
            ["dotreticulum_bitrate_kind"] = "configured_estimate",
            ["listen_address"] = LocalEndpoint?.ToString(),
            ["forward_address"] = _peer.ToString(),
            ["status"] = _started && !_disposed,
        };
    }

    public UdpPacketInterface(IPEndPoint localEndpoint, IPEndPoint peerEndpoint)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        ArgumentNullException.ThrowIfNull(peerEndpoint);
        _local = localEndpoint;
        _peer = peerEndpoint;
    }

    /// <summary>The bound local endpoint (resolves an ephemeral port 0); null before start.</summary>
    public IPEndPoint? LocalEndpoint { get; private set; }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The interface has already been started.");
            }

            _started = true;
            var socket = new Socket(_local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.EnableBroadcast = true;
                socket.Bind(_local);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            LocalEndpoint = (IPEndPoint)socket.LocalEndPoint!;
            _socket = socket;
        }

        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        var socket = GetSocket();
        if (Interlocked.Exchange(ref _receiving, 1) != 0)
        {
            throw new InvalidOperationException("ReceiveAsync supports a single enumerator.");
        }

        return ReceiveCoreAsync(socket, cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveCoreAsync(
        Socket socket,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var buffer = new byte[ReceiveBufferSize];
        EndPoint remote = _local.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            int length;
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, linked.Token).ConfigureAwait(false);
                length = result.ReceivedBytes;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows reports an earlier ICMP port-unreachable (our send had no listener) on the next receive.
                continue;
            }
            catch (Exception) when (_disposeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (length > 0)
            {
                Interlocked.Add(ref _receivedBytes, length);
                yield return buffer.AsMemory(0, length).ToArray();
            }
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        var socket = GetSocket();
        if (packet.IsEmpty || packet.Length > Packet.Mtu)
        {
            throw new ArgumentException("A packet must be 1 to 500 bytes.", nameof(packet));
        }

        var sent = await socket.SendToAsync(packet, SocketFlags.None, _peer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _sentBytes, sent);
        if (sent != packet.Length) throw new IOException($"Incomplete UDP datagram send: {sent} of {packet.Length} bytes.");
    }

    public ValueTask DisposeAsync()
    {
        Socket? socket;
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            socket = _socket;
        }

        _disposeCts.Cancel();
        socket?.Dispose();
        return ValueTask.CompletedTask;
    }

    private Socket GetSocket()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _socket ?? throw new InvalidOperationException("The interface has not been started.");
        }
    }
}
