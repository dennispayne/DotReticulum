using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>Accepts TCP peers and exchanges HDLC-framed Reticulum packets.</summary>
public sealed class TcpServerPacketInterface : IPacketInterface
{
    private readonly IPEndPoint _localEndPoint;
    private readonly int _maximumClients;
    private readonly Channel<ReadOnlyMemory<byte>> _incoming =
        Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly ConcurrentDictionary<TcpClient, SemaphoreSlim> _clients = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _clientTasks = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private volatile bool _disposed;

    public TcpServerPacketInterface(IPEndPoint localEndPoint, int maximumClients = 64)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        if (maximumClients <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumClients));
        _localEndPoint = localEndPoint;
        _maximumClients = maximumClients;
    }

    public IPEndPoint? LocalEndPoint => _listener?.LocalEndpoint as IPEndPoint;
    public int ConnectedClientCount => _clients.Count;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_listener is not null)
            throw new InvalidOperationException("The TCP server interface has already been started.");

        var listener = new TcpListener(_localEndPoint);
        listener.Start();
        _listener = listener;
        _acceptLoop = AcceptClientsAsync(listener, _lifetime.Token);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_listener is null)
            throw new InvalidOperationException("The TCP server interface has not been started.");
        ObjectDisposedException.ThrowIf(_disposed, this);

        await foreach (var packet in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return packet;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        if (_listener is null)
            throw new InvalidOperationException("The TCP server interface has not been started.");
        var frame = HdlcPacketFraming.Encode(packet.Span);
        var sends = _clients.Select(pair => SendToClientAsync(pair.Key, pair.Value, frame, cancellationToken));
        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _listener?.Stop();
        foreach (var client in _clients.Keys)
            client.Dispose();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await Task.WhenAll(_clientTasks.Values).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _incoming.Writer.TryComplete();
        _lifetime.Dispose();
    }

    private async Task AcceptClientsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                if (_clients.Count >= _maximumClients)
                {
                    client.Dispose();
                    continue;
                }

                client.NoDelay = true;
                var writeLock = new SemaphoreSlim(1, 1);
                _clients[client] = writeLock;
                _clientTasks[client] = ReceiveClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    private async Task ReceiveClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var decoder = new HdlcPacketFraming.Decoder();
        var readBuffer = new byte[4096];
        var stream = client.GetStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    break;

                var packets = new Queue<byte[]>();
                decoder.Push(readBuffer.AsSpan(0, count), packet => packets.Enqueue(packet));
                while (packets.TryDequeue(out var packet))
                    await _incoming.Writer.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (ChannelClosedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _clients.TryRemove(client, out _);
            _clientTasks.TryRemove(client, out _);
            client.Dispose();
        }
    }

    private static async Task SendToClientAsync(
        TcpClient client,
        SemaphoreSlim writeLock,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await client.GetStream().WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
