using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Transport;

/// <summary>
/// Owns every link of a <see cref="TransportRouter"/>: pending/active registries, the outbound packet queue, an ordered callback dispatcher and the
/// watchdog tick. Background loops start lazily with the first link and stop with <see cref="StopAsync"/> (the router's run loop does this on exit).
/// </summary>
public sealed class LinkManager : IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly TransportRouter _router;
    private readonly PacketInterfaceManager _manager;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Link> _pending = [];
    private readonly Dictionary<string, Link> _active = [];
    private readonly Channel<(Packet Packet, IPacketInterface? Interface)> _outbound = System.Threading.Channels.Channel.CreateUnbounded<(Packet, IPacketInterface?)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Action> _callbacks = System.Threading.Channels.Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private CancellationTokenSource? _cts;
    private Task[] _loops = [];
    private bool _stopping;
    private bool _disposed;
    private bool _suspended;
    private Task? _stopTask;

    internal LinkManager(TransportRouter router, PacketInterfaceManager manager, TimeProvider time)
    {
        _router = router;
        _manager = manager;
        _time = time;
    }

    /// <summary>Monotonic seconds.</summary>
    public double Now => _time.GetTimestamp() / (double)_time.TimestampFrequency;

    public double UnixNow => _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    public Action<string>? Log => _router.Log;

    public TransportRouter Router => _router;

    public IReadOnlyList<Link> Links
    {
        get
        {
            lock (_gate) return [.. _pending.Values, .. _active.Values];
        }
    }

    /// <summary>Initiates a link to a destination whose identity is known. The request goes out along the router's path for <paramref name="destinationHash"/>.</summary>
    public Link Connect(Identity destinationIdentity, byte[] destinationHash, Action<Link>? established = null, Action<Link>? closed = null,
        double? establishmentTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(destinationIdentity);
        ArgumentNullException.ThrowIfNull(destinationHash);
        var hops = _router.HopsTo(destinationHash) ?? 1;
        var timeout = establishmentTimeout ?? ((Link.EstablishmentTimeoutPerHop * Math.Max(1, hops)) + 1);
        lock (_gate)
        {
            if (_router.IsIdentityBlocked(destinationIdentity.Hash)) throw new InvalidOperationException("The destination identity is blocked.");
            EnsureStarted();
            _suspended = false;
            var link = Link.CreateInitiator(this, destinationIdentity, destinationHash, hops, timeout);
            link.Established = established;
            link.Closed = closed;
            link.RequestTime = Now;
            _pending[link.LinkIdHex] = link;
            _outbound.Writer.TryWrite((link.RequestPacket!, null));
            return link;
        }
    }

    public Link? Find(ReadOnlySpan<byte> linkId)
    {
        var key = Convert.ToHexStringLower(linkId);
        lock (_gate) return _active.GetValueOrDefault(key) ?? _pending.GetValueOrDefault(key);
    }

    internal void Enqueue(Packet packet, IPacketInterface? iface)
    {
        lock (_gate)
        {
            if (_stopping || _disposed || _suspended) return;
            EnsureStarted();
            _outbound.Writer.TryWrite((packet, iface));
        }
    }

    internal void Dispatch(Action action)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                _callbacks.Writer.TryWrite(action);
                return;
            }
            if (!_disposed && !_suspended)
            {
                EnsureStarted();
                _callbacks.Writer.TryWrite(action);
                return;
            }
        }
        InvokeCallback(action);
    }

    internal void Activate(Link link)
    {
        lock (_gate)
        {
            if (_stopping || _disposed) return;
            _pending.Remove(link.LinkIdHex);
            _active[link.LinkIdHex] = link;
        }
    }

    internal void Remove(Link link)
    {
        lock (_gate)
        {
            _pending.Remove(link.LinkIdHex);
            _active.Remove(link.LinkIdHex);
        }
    }

    /// <summary>LINKREQUEST addressed to a local destination (upstream <c>Destination.incoming_link_request</c>).</summary>
    internal void HandleLinkRequest(ReceivedPacket received, LocalDestination destination)
    {
        if (!destination.AcceptLinks)
        {
            return;
        }

        Link? link;
        lock (_gate)
        {
            if (_stopping || _disposed) return;
            EnsureStarted();
            _suspended = false;
            link = Link.ValidateRequest(this, destination, received);
            if (link is null) return;
            _active[link.LinkIdHex] = link;
        }
        Log?.Invoke($"Incoming link request {link.LinkIdHex} accepted");
    }

    /// <summary>LRPROOF for a link we initiated. Returns true when it belonged to a pending local link.</summary>
    internal bool HandleLinkProof(ReceivedPacket received)
    {
        Link? link;
        lock (_gate) _pending.TryGetValue(Convert.ToHexStringLower(received.Packet.DestinationHash.Span), out link);
        if (link is null)
        {
            return false;
        }

        link.ValidateProof(received);
        return true;
    }

    /// <summary>A packet addressed to one of our active links. Returns true when consumed.</summary>
    internal bool HandleLinkPacket(ReceivedPacket received)
    {
        Link? link;
        lock (_gate) _active.TryGetValue(Convert.ToHexStringLower(received.Packet.DestinationHash.Span), out link);
        if (link is null)
        {
            return false;
        }

        link.Receive(received);
        return true;
    }

    /// <summary>Runs one watchdog pass at <paramref name="now"/> (the background loop calls this every 100 ms).</summary>
    public void Tick(double now)
    {
        foreach (var link in Links)
        {
            try
            {
                link.Tick(now);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Link {link.LinkIdHex} watchdog error: {ex.Message}");
            }
        }
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping) throw new InvalidOperationException("The link manager is stopping.");
            if (_cts is not null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _loops = [Task.Run(() => PumpAsync(cts.Token)), Task.Run(() => CallbackLoopAsync(cts.Token)), Task.Run(() => TickLoopAsync(cts.Token))];
            _cts = cts;
        }
    }

    /// <summary>Closes all links and settles their work. New links may be opened after this task completes.</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopping) return _stopTask!;
            _stopping = true;
            _suspended = true;
            return _stopTask = Task.Run(StopCoreAsync);
        }
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? cts;
        Task[] loops;
        lock (_gate)
        {
            cts = _cts;
            loops = _loops;
        }
        try
        {
            if (cts is not null) await cts.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(loops).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            foreach (var link in Links) link.Teardown();
            while (_outbound.Reader.TryRead(out _)) { }
            while (true)
            {
                Action? callback;
                lock (_gate)
                {
                    if (!_callbacks.Reader.TryRead(out callback))
                    {
                        _cts = null;
                        _loops = [];
                        _stopping = false;
                        break;
                    }
                }
                InvokeCallback(callback);
            }
            cts?.Dispose();
        }
    }

    /// <summary>Terminal cleanup; unlike StopAsync, subsequent Connect calls are rejected.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private void InvokeCallback(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            try { Log?.Invoke("Link callback failed: " + ex.Message); }
            catch { /* A diagnostic observer must not prevent settlement. */ }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (packet, iface) in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (iface is null)
                    {
                        await _router.SendAsync(packet, ct).ConfigureAwait(false);
                    }
                    else if (_manager.Contains(iface))
                    {
                        await _manager.SendToAsync(packet, iface, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log?.Invoke($"Could not send link packet: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CallbackLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var action in _callbacks.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                InvokeCallback(action);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Tick(Now);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
