using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>Connects to one TCP peer using Reticulum's HDLC packet framing.</summary>
public sealed class TcpClientPacketInterface : IPacketInterface
{
    private readonly IPEndPoint _remoteEndPoint;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _client;
    private int _receiving;
    private volatile bool _disposed;

    public TcpClientPacketInterface(IPEndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        _remoteEndPoint = remoteEndPoint;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default) =>
        ConnectAsync(cancellationToken);

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null)
            throw new InvalidOperationException("The TCP client interface has already been started.");

        var client = new TcpClient(_remoteEndPoint.AddressFamily) { NoDelay = true };
        try
        {
            await client.ConnectAsync(_remoteEndPoint, cancellationToken).ConfigureAwait(false);
            _client = client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = _client ?? throw new InvalidOperationException("The TCP client interface has not been started.");
        if (Interlocked.Exchange(ref _receiving, 1) != 0)
            throw new InvalidOperationException("Only one receive operation is allowed.");

        var decoder = new HdlcPacketFraming.Decoder();
        var readBuffer = new byte[4096];
        var decodedPackets = new Queue<byte[]>();
        var stream = client.GetStream();
        try
        {
            while (true)
            {
                int count;
                try
                {
                    count = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }
                catch (IOException) when (_disposed)
                {
                    yield break;
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                    yield break;
                }

                if (count == 0)
                    yield break;

                decoder.Push(readBuffer.AsSpan(0, count), packet => decodedPackets.Enqueue(packet));
                while (decodedPackets.TryDequeue(out var packet))
                    yield return packet;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _receiving, 0);
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        var client = _client ?? throw new InvalidOperationException("The TCP client interface has not been started.");
        var frame = HdlcPacketFraming.Encode(packet.Span);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await client.GetStream().WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }
}
