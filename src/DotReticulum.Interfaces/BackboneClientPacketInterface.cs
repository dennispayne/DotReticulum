using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>A persistent Linux/Android Backbone initiator with bounded per-connection queues.</summary>
public sealed class BackboneClientPacketInterface : IReconnectingPacketInterface, IPacketInterfaceDiagnostics, IPacketInterfaceIngressFeedback
{
    private readonly string _host;
    private readonly int _port;
    private readonly BackbonePacketInterfaceOptions _options;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<ReadOnlyMemory<byte>> _received;
    private BackbonePeerEndpoint? _endpoint;
    private Task? _worker;
    private Task? _disposeTask;
    private bool _started;
    private int _receiving;
    private int _attempts;
    private long _rx;
    private long _tx;
    private PacketInterfaceIngressQueue? _ingressQueue;
    private BackboneSharedIngressState? _shared;

    public BackboneClientPacketInterface(string host, int port, BackbonePacketInterfaceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        _host = host;
        _port = port;
        _options = options ?? new();
        _options.Validate();
        _received = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(_options.ReceiveQueueFrames)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    }

    public long Bitrate => _options.NominalBitrate;
    public int HardwareMtu => _options.HardwareMtu;
    public bool SupportsMtuDiscovery => true;
    public bool IsConnected { get { lock (_gate) return _disposeTask is null && _endpoint is { IsConnected: true }; } }
    public event Action? Connected;
    public event Action? Disconnected;

    public void SetIngressQueue(PacketInterfaceIngressQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started) throw new InvalidOperationException("Bind Backbone ingress feedback before start.");
            _ingressQueue = queue;
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started) throw new InvalidOperationException("The Backbone initiator has already started.");
            BackbonePacketInterfaceOptions.RequirePlatform();
            _shared = BackboneSharedIngressState.Acquire(_ingressQueue, _options);
            _started = true;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { await ConnectAsync(linked.Token).ConfigureAwait(false); }
        catch (Exception error) when (!linked.IsCancellationRequested && error is SocketException or IOException or OperationCanceledException)
        {
            Report(error);
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _worker = Task.Run(RunAsync);
        }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_options.ConnectTimeout);
        var addresses = await Dns.GetHostAddressesAsync(_host, timeout.Token).ConfigureAwait(false);
        var selected = addresses.FirstOrDefault(a => a.AddressFamily ==
            (_options.PreferIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork)) ?? addresses.FirstOrDefault()
            ?? throw new IOException("No usable Backbone target address.");
        var socket = new Socket(selected.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(selected, _port), timeout.Token).ConfigureAwait(false);
            BackbonePeerEndpoint? captured = null;
            var endpoint = new BackbonePeerEndpoint(socket, _options,
                (count, sent) =>
                {
                    if (sent) Interlocked.Add(ref _tx, count);
                    else Interlocked.Add(ref _rx, count);
                }, () => { if (captured is not null) _shared!.Unregister(captured); });
            captured = endpoint;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                _endpoint = endpoint;
                _shared!.Register(endpoint);
                endpoint.Start();
            }
        }
        catch { socket.Dispose(); throw; }
        Notify(Connected);
    }

    private async Task RunAsync()
    {
        Exception? failure = null;
        try
        {
            var attempts = 0;
            while (!_lifetime.IsCancellationRequested)
            {
                BackbonePeerEndpoint? endpoint;
                lock (_gate) endpoint = _endpoint;
                if (endpoint is null)
                {
                    if (_options.MaximumReconnectAttempts is { } max && attempts >= max)
                        throw new IOException("Maximum Backbone reconnection attempts reached.");
                    await Task.Delay(_options.ReconnectDelay, _lifetime.Token).ConfigureAwait(false);
                    attempts++;
                    Interlocked.Increment(ref _attempts);
                    try { await ConnectAsync(_lifetime.Token).ConfigureAwait(false); }
                    catch (Exception error) when (!_lifetime.IsCancellationRequested && error is SocketException or IOException or OperationCanceledException)
                    { Report(error); }
                    continue;
                }
                attempts = 0;
                try
                {
                    await foreach (var frame in endpoint.ReceiveAsync(_lifetime.Token).ConfigureAwait(false))
                        await _received.Writer.WriteAsync(frame, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (!_lifetime.IsCancellationRequested && error is SocketException or IOException)
                { Report(error); }
                finally
                {
                    lock (_gate) if (ReferenceEquals(_endpoint, endpoint)) _endpoint = null;
                    await endpoint.DisposeAsync().ConfigureAwait(false);
                    if (!_lifetime.IsCancellationRequested) Notify(Disconnected);
                }
            }
        }
        catch (Exception error) when (_lifetime.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception error) { failure = error; Report(error); }
        finally { _received.Writer.TryComplete(failure); }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        lock (_gate) EnsureStarted();
        if (Interlocked.Exchange(ref _receiving, 1) != 0) throw new InvalidOperationException("Only one receive enumerator is supported.");
        return ReadAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await using var reader = _received.Reader.ReadAllAsync(linked.Token).GetAsyncEnumerator();
        while (true)
        {
            bool more;
            try { more = await reader.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !token.IsCancellationRequested) { yield break; }
            if (!more) yield break;
            yield return reader.Current;
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureStarted();
            return (_endpoint ?? throw new IOException("The Backbone initiator is offline.")).SendAsync(packet, cancellationToken);
        }
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        if (!_started) throw new InvalidOperationException("The Backbone initiator has not started.");
    }

    private static void Report(Exception error) => System.Diagnostics.Trace.TraceError("Backbone initiator: {0}", error);
    private static void Notify(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error) { Report(error); }
        }
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate)
        {
            var values = _endpoint is null ? new Dictionary<string, object?>() : new(_endpoint.GetDiagnostics());
            values["rxb"] = Interlocked.Read(ref _rx);
            values["txb"] = Interlocked.Read(ref _tx);
            values["status"] = IsConnected;
            values["target_host"] = _host;
            values["target_port"] = _port;
            values["reconnect_attempts"] = Volatile.Read(ref _attempts);
            values["bitrate"] = Bitrate;
            values["hardware_mtu"] = HardwareMtu;
            values["shared_ingress_peers"] = _shared?.PeerCount ?? 0;
            values["manager_ingress_feedback"] = _ingressQueue is not null;
            return values;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        BackbonePeerEndpoint? endpoint;
        Task? worker;
        lock (_gate) { endpoint = _endpoint; worker = _worker; }
        if (endpoint is not null) await endpoint.DisposeAsync().ConfigureAwait(false);
        if (worker is not null) await worker.ConfigureAwait(false);
        try
        {
            if (_shared is not null) await _shared.ReleaseAsync().ConfigureAwait(false);
        }
        finally { _received.Writer.TryComplete(); }
    }
}
