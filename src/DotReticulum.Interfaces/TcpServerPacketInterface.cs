using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>
/// Listens for TCP connections (as upstream's TCPServerInterface) and exchanges HDLC-framed packets with every
/// connected client: packets from all clients are merged into one receive stream; sends go to every client.
/// Connections beyond <c>maxClients</c> are accepted and immediately closed (upstream has no cap; this is a hardening).
/// With <c>perClientInterfaces</c> the server instead spawns one <see cref="IPacketInterface"/> per connection (upstream's
/// spawned sub-interfaces) through <see cref="ChildConnected"/>; the server itself then neither receives nor sends, so a
/// router can answer a client on that client only and never echo a packet back to its source.
/// </summary>
public sealed class TcpServerPacketInterface : ISpawningPacketInterface, IPacketInterfaceDiagnostics
{
    public const int DefaultMaxClients = 64;
    private const int ReceiveQueueCapacity = 256;

    private readonly IPEndPoint _local;
    private readonly int _maxClients;
    private readonly Channel<ReadOnlyMemory<byte>> _received;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _gate = new();
    private readonly List<Client> _clients = [];
    private readonly List<Task> _clientTasks = [];
    private TcpListener? _listener;
    private Task? _acceptTask;
    private bool _started;
    private Task? _disposeTask;
    private int _receiving;
    private long _rxBytes;
    private long _txBytes;
    private long _rejectedClients;

    private readonly bool _perClient;

    public TcpServerPacketInterface(IPEndPoint localEndpoint, int maxClients = DefaultMaxClients, bool perClientInterfaces = false)
    {
        _perClient = perClientInterfaces;
        ArgumentNullException.ThrowIfNull(localEndpoint);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxClients, 1);
        _local = localEndpoint;
        _maxClients = maxClients;
        _received = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(ReceiveQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Raised in per-client mode for each accepted connection; the child arrives connected and started.</summary>
    public event Action<IPacketInterface>? ChildConnected;

    public bool SendsThroughChildren => _perClient;

    /// <summary>The bound listening endpoint (resolves an ephemeral port 0); null before start.</summary>
    public IPEndPoint? LocalEndpoint { get; private set; }

    public int ClientCount
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["rxb"] = Interlocked.Read(ref _rxBytes),
            ["txb"] = Interlocked.Read(ref _txBytes),
            ["clients"] = _clients.Count,
            ["bitrate"] = 10_000_000L,
            ["dotreticulum_bitrate_kind"] = "configured_estimate",
            ["rejected_clients"] = _rejectedClients,
            ["listen_address"] = LocalEndpoint?.ToString(),
            ["status"] = _started && _disposeTask is null,
        };
    }

    private sealed class Client(HdlcStreamEndpoint endpoint)
    {
        public HdlcStreamEndpoint Endpoint { get; } = endpoint;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started)
            {
                throw new InvalidOperationException("The interface has already been started.");
            }

            _started = true;
            var listener = new TcpListener(_local);
            try
            {
                listener.Start();
            }
            catch
            {
                listener.Stop();
                throw;
            }

            _listener = listener;
            LocalEndpoint = (IPEndPoint)listener.LocalEndpoint;
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener));
        }

        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!_started)
            {
                throw new InvalidOperationException("The interface has not been started.");
            }
        }

        if (Interlocked.Exchange(ref _receiving, 1) != 0)
        {
            throw new InvalidOperationException("ReceiveAsync supports a single enumerator.");
        }

        return _received.Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>Sends to every connected client; a client that fails is dropped without failing the send.</summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (_perClient)
        {
            return; // Each client is its own interface in this mode.
        }

        Client[] snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!_started)
            {
                throw new InvalidOperationException("The interface has not been started.");
            }

            snapshot = [.. _clients];
        }

        // Validates up front so malformed input is an argument error rather than a per-client failure.
        _ = HdlcPacketFraming.Encode(packet);
        var bytes = packet.ToArray();
        var sends = new Task[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++)
        {
            sends[i] = SendToClientAsync(snapshot[i], bytes, cancellationToken);
        }

        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    private async Task SendToClientAsync(Client client, byte[] packet, CancellationToken cancellationToken)
    {
        try
        {
            await client.Endpoint.SendAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await RemoveClientAsync(client).ConfigureAwait(false);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        var token = _disposeCts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync(token).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                {
                    continue;
                }

                AdmitClient(socket);
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested || ex is ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _received.Writer.TryComplete(ex);
        }
    }

    private void AdmitClient(Socket socket)
    {
        Client client;
        lock (_gate)
        {
            if (_disposeTask is not null || _clients.Count >= _maxClients)
            {
                _rejectedClients++;
                socket.Dispose();
                return;
            }

            try
            {
                socket.NoDelay = true;
                client = new Client(new HdlcStreamEndpoint(new NetworkStream(socket, ownsSocket: true),
                    received: length => Interlocked.Add(ref _rxBytes, length),
                    sent: length => Interlocked.Add(ref _txBytes, length)));
            }
            catch (SocketException)
            {
                socket.Dispose();
                return;
            }

            _clients.Add(client);
            if (_perClient)
            {
                var handler = ChildConnected;
                if (handler is null)
                {
                    _ = RemoveClientAsync(client);
                    return;
                }

                handler(new TcpServerClientInterface(client.Endpoint, () => RemoveClientAsync(client), socket.RemoteEndPoint!.ToString()!));
                return;
            }

            _clientTasks.Add(Task.Run(() => ReadClientAsync(client)));
        }
    }

    private async Task ReadClientAsync(Client client)
    {
        var token = _disposeCts.Token;
        try
        {
            await foreach (var packet in client.Endpoint.ReceiveAsync(token).ConfigureAwait(false))
            {
                await _received.Writer.WriteAsync(packet, token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A broken client connection only removes that client.
        }
        finally
        {
            await RemoveClientAsync(client).ConfigureAwait(false);
        }
    }

    private async Task RemoveClientAsync(Client client)
    {
        lock (_gate)
        {
            _clients.Remove(client);
        }

        try
        {
            await client.Endpoint.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort teardown of a connection that is already gone.
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Yield so the caller's lock is released before any real work runs.
        await Task.Yield();
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        TcpListener? listener;
        Task? acceptTask;
        Client[] clients;
        Task[] clientTasks;
        lock (_gate)
        {
            listener = _listener;
            acceptTask = _acceptTask;
            clients = [.. _clients];
            clientTasks = [.. _clientTasks];
        }

        listener?.Stop();
        foreach (var client in clients)
        {
            await RemoveClientAsync(client).ConfigureAwait(false);
        }

        if (acceptTask is not null)
        {
            await acceptTask.ConfigureAwait(false);
        }

        await Task.WhenAll(clientTasks).ConfigureAwait(false);
        _received.Writer.TryComplete();
        _disposeCts.Dispose();
    }
}
