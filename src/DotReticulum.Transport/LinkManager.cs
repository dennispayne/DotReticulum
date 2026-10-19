using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Transport;

/// <summary>
/// Owns every link of a <see cref="TransportRouter"/>: pending/active registries, the outbound packet queue, an ordered callback dispatcher and the
/// watchdog tick. Background loops start lazily with the first link and stop with <see cref="StopAsync"/> (the router's run loop does this on exit).
/// </summary>
public sealed class LinkManager
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
        EnsureStarted();
        var hops = _router.HopsTo(destinationHash) ?? 1;
        var timeout = establishmentTimeout ?? ((Link.EstablishmentTimeoutPerHop * Math.Max(1, hops)) + 1);
        var link = Link.CreateInitiator(this, destinationIdentity, destinationHash, hops, timeout);
        link.Established = established;
        link.Closed = closed;
        link.RequestTime = Now;
        lock (_gate) _pending[link.LinkIdHex] = link;
        Enqueue(link.RequestPacket!, null);
        return link;
    }

    public Link? Find(ReadOnlySpan<byte> linkId)
    {
        var key = Convert.ToHexStringLower(linkId);
        lock (_gate) return _active.GetValueOrDefault(key) ?? _pending.GetValueOrDefault(key);
    }

    internal void Enqueue(Packet packet, IPacketInterface? iface)
    {
        EnsureStarted();
        _outbound.Writer.TryWrite((packet, iface));
    }

    internal void Dispatch(Action action)
    {
        EnsureStarted();
        _callbacks.Writer.TryWrite(action);
    }

    internal void Activate(Link link)
    {
        lock (_gate)
        {
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

        EnsureStarted();
        var link = Link.ValidateRequest(this, destination, received);
        if (link is null)
        {
            return;
        }

        lock (_gate) _active[link.LinkIdHex] = link;
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
        if (_cts is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _loops = [Task.Run(() => PumpAsync(cts.Token)), Task.Run(() => CallbackLoopAsync(cts.Token)), Task.Run(() => TickLoopAsync(cts.Token))];
            _cts = cts;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task[] loops;
        lock (_gate)
        {
            cts = _cts;
            loops = _loops;
            _cts = null;
            _loops = [];
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        cts.Dispose();
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
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Link callback failed: " + ex.Message);
                }
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
