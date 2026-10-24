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
    private readonly bool _resolveLocal;
    private readonly string? _listenHost;
    private readonly string? _device;
    private readonly bool _preferIpv6;
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
    private readonly SemaphoreSlim _broadcastLock = new(1, 1);
    private readonly bool _i2pTunneled;
    private string? _lastError;
    private long _peerFailures;

    private readonly bool _perClient;

    public TcpServerPacketInterface(IPEndPoint localEndpoint, int maxClients = DefaultMaxClients, bool perClientInterfaces = false,
        long nominalBitrate = 10_000_000, int? fixedMtu = null, bool i2pTunneled = false)
    {
        _perClient = perClientInterfaces;
        _i2pTunneled = i2pTunneled;
        FixedMtu = fixedMtu.HasValue;
        ArgumentNullException.ThrowIfNull(localEndpoint);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxClients, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(nominalBitrate, 1);
        if (fixedMtu is < Packet.Mtu or > InterfaceAccessCode.MaximumPacketSize) throw new ArgumentOutOfRangeException(nameof(fixedMtu));
        Bitrate = nominalBitrate;
        HardwareMtu = fixedMtu ?? PacketInterfaceCapabilities.MtuForBitrate(nominalBitrate);
        _local = localEndpoint;
        _maxClients = maxClients;
        _received = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(ReceiveQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Resolves a hostname or network device asynchronously at start, not during construction.</summary>
    public TcpServerPacketInterface(string? listenHost, int port, string? device = null, bool preferIpv6 = false,
        int maxClients = DefaultMaxClients, bool perClientInterfaces = false, long nominalBitrate = 10_000_000,
        int? fixedMtu = null, bool i2pTunneled = false)
        : this(new IPEndPoint(IPAddress.Any, port), maxClients, perClientInterfaces, nominalBitrate, fixedMtu, i2pTunneled)
    {
        _listenHost = string.IsNullOrWhiteSpace(listenHost) && string.IsNullOrWhiteSpace(device)
            ? IPAddress.Any.ToString()
            : listenHost;
        _device = device;
        _preferIpv6 = preferIpv6;
        _resolveLocal = true;
    }

    /// <summary>Raised in per-client mode for each accepted connection; the child arrives connected and started.</summary>
    public event Action<IPacketInterface>? ChildConnected;

    public bool SendsThroughChildren => _perClient;
    public long Bitrate { get; }
    public int HardwareMtu { get; }
    public bool SupportsMtuDiscovery => true;
    public bool FixedMtu { get; }

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
            ["bitrate"] = Bitrate,
            ["hardware_mtu"] = HardwareMtu,
            ["dotreticulum_bitrate_kind"] = "configured_estimate",
            ["rejected_clients"] = _rejectedClients,
            ["listen_address"] = LocalEndpoint?.ToString(),
            ["status"] = LocalEndpoint is not null && _disposeTask is null,
            ["last_error"] = Volatile.Read(ref _lastError),
            ["peer_failures"] = Interlocked.Read(ref _peerFailures),
            ["fixed_mtu"] = FixedMtu,
            ["i2p_tunneled"] = _i2pTunneled,
        };
    }

    private sealed class Client(HdlcStreamEndpoint endpoint)
    {
        public HdlcStreamEndpoint Endpoint { get; } = endpoint;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
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
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        IPEndPoint local;
        try
        {
            local = _resolveLocal
                ? await TcpAddressResolver.ResolveListenEndpointAsync(_listenHost, _local.Port, _device, _preferIpv6, linked.Token).ConfigureAwait(false)
                : _local;
        }
        catch (Exception error)
        {
            Report(error);
            throw;
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            linked.Token.ThrowIfCancellationRequested();
            var listener = new TcpListener(local);
            try
            {
                if (local.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = false;
                listener.Start();
            }
            catch (Exception error)
            {
                Report(error);
                listener.Stop();
                throw;
            }

            _listener = listener;
            LocalEndpoint = (IPEndPoint)listener.LocalEndpoint;
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener));
        }
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
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!_started) throw new InvalidOperationException("The interface has not been started.");
        }
        if (_perClient) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _broadcastLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await BroadcastAsync(packet, linked.Token).ConfigureAwait(false); }
        finally { _broadcastLock.Release(); }
    }

    private async ValueTask BroadcastAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
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
        if (packet.IsEmpty || packet.Length > HardwareMtu + ((packet.Span[0] & 0x80) != 0 ? 64 : 0))
            throw new ArgumentException("Packet exceeds the bearer frame bound.", nameof(packet));
        if ((packet.Span[0] & 0x80) == 0 && !Packet.TryParse(packet, out _, HardwareMtu))
            throw new ArgumentException("Not a valid Reticulum packet.", nameof(packet));
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
        catch (Exception error)
        {
            ReportPeerFailure(error);
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
            Report(ex);
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
                TcpSocketSettings.Apply(socket, _i2pTunneled);
                client = new Client(new HdlcStreamEndpoint(new NetworkStream(socket, ownsSocket: true),
                    received: length => Interlocked.Add(ref _rxBytes, length),
                    sent: length => Interlocked.Add(ref _txBytes, length),
                    maxFrameLength: HardwareMtu + 64, authenticatedFrames: true, unprotectedFrameLength: HardwareMtu));
            }
            catch (Exception error) when (error is SocketException or IOException)
            {
                ReportPeerFailure(error);
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

                var child = new TcpServerClientInterface(client.Endpoint, () => RemoveClientAsync(client), socket.RemoteEndPoint!.ToString()!,
                    Bitrate, HardwareMtu);
                var observed = false;
                foreach (Action<IPacketInterface> observer in handler.GetInvocationList())
                {
                    try { observer(child); observed = true; }
                    catch (Exception error) { ReportPeerFailure(error); }
                }
                if (!observed) _ = RemoveClientAsync(client);
                return;
            }

            _clientTasks.RemoveAll(task => task.IsCompleted);
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
        catch (Exception error) when (token.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceInformation("TCP peer receive stopped during shutdown: {0}", error.Message);
        }
        catch (Exception error)
        {
            ReportPeerFailure(error);
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
        catch (Exception error)
        {
            ReportPeerFailure(error);
        }
    }

    private void ReportPeerFailure(Exception error)
    {
        Interlocked.Increment(ref _peerFailures);
        Report(error);
    }

    private void Report(Exception error)
    {
        Volatile.Write(ref _lastError, error.Message);
        System.Diagnostics.Trace.TraceError("TCP server interface: {0}", error);
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
    }
}
