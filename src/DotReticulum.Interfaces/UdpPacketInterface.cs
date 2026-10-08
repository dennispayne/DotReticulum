using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>Exchanges one complete Reticulum packet per UDP datagram with a peer.</summary>
public sealed class UdpPacketInterface : IPacketInterface
{
    private readonly IPEndPoint _localEndPoint;
    private readonly IPEndPoint _remoteEndPoint;
    private UdpClient? _client;

    public UdpPacketInterface(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;
    }

    public IPEndPoint? LocalEndPoint => _client?.Client.LocalEndPoint as IPEndPoint;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is not null)
            throw new InvalidOperationException("The UDP interface has already been started.");

        _client = new UdpClient(_localEndPoint);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = _client ?? throw new InvalidOperationException("The UDP interface has not been started.");
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (result.RemoteEndPoint.Equals(_remoteEndPoint)
                && Packet.TryParse(result.Buffer, out _))
                yield return result.Buffer;
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        var client = _client ?? throw new InvalidOperationException("The UDP interface has not been started.");
        if (!Packet.TryParse(packet, out _))
            throw new ArgumentException("Data must contain one complete Reticulum packet.", nameof(packet));

        await client.SendAsync(packet, _remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }
}
