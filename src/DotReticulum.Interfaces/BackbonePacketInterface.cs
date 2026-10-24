using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>A Linux/Android Backbone listener. Every accepted connection is a separately routable child.</summary>
public sealed class BackbonePacketInterface : ISpawningPacketInterface, IPacketInterfaceDiagnostics, IPacketInterfaceIngressFeedback
{
    private readonly IPEndPoint? _local;
    private readonly string? _listenHost;
    private readonly int _listenPort;
    private readonly BackbonePacketInterfaceOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<BackbonePeerEndpoint, BackboneAcceptedPacketInterface> _peers = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<ReadOnlyMemory<byte>> _empty = Channel.CreateBounded<ReadOnlyMemory<byte>>(1);
    private TcpListener? _listener;
    private Task? _acceptTask;
    private Task? _disposeTask;
    private Task? _startTask;
    private long _rx;
    private long _tx;
    private long _rejected;
    private bool _started;
    private bool _receiving;
    private bool _starting;
    private PacketInterfaceIngressQueue? _ingressQueue;
    private BackboneSharedIngressState? _shared;

    public BackbonePacketInterface(IPEndPoint localEndpoint, BackbonePacketInterfaceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        _local = new(localEndpoint.Address, localEndpoint.Port);
        _listenPort = localEndpoint.Port;
        _options = options ?? new();
        _options.Validate();
    }

    public BackbonePacketInterface(string? listenHost, int listenPort, BackbonePacketInterfaceOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(listenPort, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(listenPort, 65535);
        _options = options ?? new();
        _options.Validate();
        if (_options.Device is null) ArgumentException.ThrowIfNullOrWhiteSpace(listenHost);
        _listenHost = listenHost;
        _listenPort = listenPort;
    }

    public event Action<IPacketInterface>? ChildConnected;
    public bool SendsThroughChildren => true;
    public long Bitrate => _options.NominalBitrate;
    public int HardwareMtu => _options.HardwareMtu;
    public bool SupportsMtuDiscovery => true;
    public IPEndPoint? LocalEndpoint { get; private set; }
    public int ClientCount { get { lock (_gate) return _peers.Count; } }

    public void SetIngressQueue(PacketInterfaceIngressQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started || _starting) throw new InvalidOperationException("Bind Backbone ingress feedback before start.");
            _ingressQueue = queue;
        }
    }

    /// <summary>
    /// Supplies a standalone owner's data-queue occupancy for one periodic control evaluation.
    /// Manager-bound interfaces instead sample their shared live queue automatically.
    /// </summary>
    public void UpdateIngressQueueDepth(int depth, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        lock (_gate)
        {
            EnsureStarted();
            if (_ingressQueue is not null) throw new InvalidOperationException("Manager-bound Backbone ingress uses its live queue automatically.");
            _shared!.Evaluate(depth, capacity, BackbonePeerEndpoint.Now);
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started || _starting) throw new InvalidOperationException("The Backbone listener has already started.");
            BackbonePacketInterfaceOptions.RequirePlatform();
            _starting = true;
            _startTask = StartCoreAsync(cancellationToken);
            return new(_startTask);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            var local = await BackboneBindResolver.ResolveAsync(_local, _listenHost, _listenPort, _options, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                var listener = new TcpListener(local);
                try
                {
                    BackboneSocketOptions.ConfigureListener(listener.Server);
                    listener.Start(1);
                }
                catch { listener.Stop(); throw; }
                _listener = listener;
                LocalEndpoint = (IPEndPoint)listener.LocalEndpoint;
                _shared = BackboneSharedIngressState.Acquire(_ingressQueue, _options);
                _started = true;
                _acceptTask = Task.Run(AcceptAsync);
            }
        }
        finally { lock (_gate) _starting = false; }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var socket = await _listener!.AcceptSocketAsync(_lifetime.Token).ConfigureAwait(false);
                try { Admit(socket); }
                catch (Exception error)
                {
                    socket.Dispose();
                    System.Diagnostics.Trace.TraceError("Backbone admission failed: {0}", error);
                }
            }
        }
        catch (Exception error) when (_lifetime.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Backbone listener failed: {0}", error);
            _empty.Writer.TryComplete(error);
        }
    }

    private void Admit(Socket socket)
    {
        var remote = (IPEndPoint)socket.RemoteEndPoint!;
        var address = remote.Address.ToString();
        var accepted = BackbonePeerEndpoint.Now;
        BackbonePeerEndpoint endpoint;
        BackboneAcceptedPacketInterface child;
        lock (_gate)
        {
            if (_disposeTask is not null || _peers.Count >= _options.MaximumClients || _shared!.IsBlocked(address, accepted, _options))
            {
                _rejected++;
                socket.Dispose();
                return;
            }
            BackbonePeerEndpoint? captured = null;
            endpoint = new(socket, _options,
                (count, sent) =>
                {
                    if (sent) Interlocked.Add(ref _tx, count);
                    else Interlocked.Add(ref _rx, count);
                },
                () =>
                {
                    lock (_gate)
                    {
                        if (captured is not null)
                        {
                            _peers.Remove(captured);
                            _shared!.Unregister(captured);
                        }
                        _shared!.Disconnected(address, BackbonePeerEndpoint.Now - accepted, BackbonePeerEndpoint.Now, _options);
                    }
                });
            captured = endpoint;
            child = new(endpoint, remote.ToString(), _options, _shared!);
            _peers.Add(endpoint, child);
            _shared!.Register(endpoint);
            endpoint.Start();
        }
        var observed = false;
        if (ChildConnected is { } handlers)
        {
            foreach (Action<IPacketInterface> handler in handlers.GetInvocationList())
            {
                try { handler(child); observed = true; }
                catch (Exception error) { System.Diagnostics.Trace.TraceError("Backbone child observer failed: {0}", error); }
            }
        }
        if (!observed)
        {
            // Dispose is tracked by the endpoint itself; this continuation owns no additional worker.
            _ = endpoint.DisposeAsync();
        }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            EnsureStarted();
            if (_receiving) throw new InvalidOperationException("Only one receive enumerator is supported.");
            _receiving = true;
        }
        return _empty.Reader.ReadAllAsync(cancellationToken);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) EnsureStarted();
        throw new NotSupportedException("Send using the individual Backbone child interface.");
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        if (!_started) throw new InvalidOperationException("The Backbone listener has not started.");
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["rxb"] = Interlocked.Read(ref _rx), ["txb"] = Interlocked.Read(ref _tx),
            ["clients"] = _peers.Count, ["rejected_clients"] = _rejected,
            ["blocked_ip_count"] = _shared?.BlockedCount(BackbonePeerEndpoint.Now, _options) ?? 0,
            ["shared_ingress_peers"] = _shared?.PeerCount ?? 0,
            ["manager_ingress_feedback"] = _ingressQueue is not null,
            ["listen_address"] = LocalEndpoint?.ToString(), ["bitrate"] = Bitrate,
            ["hardware_mtu"] = HardwareMtu, ["status"] = _started && _disposeTask is null && _acceptTask is { IsCompleted: false },
            ["dotreticulum_bitrate_kind"] = "configured_estimate",
        };
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_startTask is not null)
        {
            try { await _startTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Backbone cleanup after failed start: {0}", error); }
        }
        _listener?.Stop();
        if (_acceptTask is not null) await _acceptTask.ConfigureAwait(false);
        BackbonePeerEndpoint[] peers;
        lock (_gate) peers = [.. _peers.Keys];
        foreach (var peer in peers) await peer.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (_shared is not null) await _shared.ReleaseAsync().ConfigureAwait(false);
        }
        finally { _empty.Writer.TryComplete(); }
    }
}

internal sealed class BackboneAcceptedPacketInterface(BackbonePeerEndpoint endpoint, string address,
    BackbonePacketInterfaceOptions options, BackboneSharedIngressState shared) :
    IPacketInterface, IPacketInterfaceDiagnostics, IPacketInterfaceIngressFeedback
{
    private int _receiving;
    public long Bitrate => options.NominalBitrate;
    public int HardwareMtu => options.HardwareMtu;
    public bool SupportsMtuDiscovery => true;
    public void SetIngressQueue(PacketInterfaceIngressQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (!ReferenceEquals(queue, shared.Queue))
            throw new InvalidOperationException("A Backbone child must use the same ingress queue as its owning listener.");
    }
    public ValueTask StartAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Accepted Backbone children are already started.");
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _receiving, 1) != 0) throw new InvalidOperationException("Only one receive enumerator is supported.");
        return endpoint.ReceiveAsync(cancellationToken);
    }
    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => endpoint.SendAsync(packet, cancellationToken);
    public ValueTask DisposeAsync() => endpoint.DisposeAsync();
    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        var values = new Dictionary<string, object?>(endpoint.GetDiagnostics()) { ["remote_endpoint"] = address };
        return values;
    }
    public override string ToString() => $"BackboneClient[{address}]";
}
