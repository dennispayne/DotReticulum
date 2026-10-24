using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Transport;

/// <summary>
/// Owns every link of a <see cref="TransportRouter"/>: pending/active registries, the outbound packet queue, an ordered callback dispatcher and the
/// watchdog tick. Background loops start lazily with the first link and stop with <see cref="StopAsync"/> (the router's run loop does this on exit).
/// </summary>
public sealed class LinkManager : IAsyncDisposable
{
    public const int WorkQueueCapacity = 256;
    public const int ObserverConcurrency = 4;
    public const int RequestConcurrency = 4;
    public const int MaximumLinks = 1024;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly TransportRouter _router;
    private readonly PacketInterfaceManager _manager;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Link> _pending = [];
    private readonly Dictionary<string, Link> _active = [];
    private readonly Channel<(Packet Packet, IPacketInterface? Interface)> _outbound = System.Threading.Channels.Channel.CreateBounded<(Packet, IPacketInterface?)>(WorkQueueCapacity);
    private readonly Channel<Func<ValueTask>> _callbacks = System.Threading.Channels.Channel.CreateBounded<Func<ValueTask>>(WorkQueueCapacity);
    private readonly Channel<Func<ValueTask>> _requests = System.Threading.Channels.Channel.CreateBounded<Func<ValueTask>>(WorkQueueCapacity);
    private readonly SemaphoreSlim _observerSlots = new(ObserverConcurrency, ObserverConcurrency);
    private readonly SemaphoreSlim _requestSlots = new(RequestConcurrency, RequestConcurrency);
    private readonly HashSet<Task> _invocations = [];
    private readonly HashSet<Task> _requiredCleanup = [];
    private ResourcePolicy _defaultResourcePolicy = new();

    /// <summary>Defaults for newly created links; existing links keep their policy.</summary>
    public ResourcePolicy DefaultResourcePolicy
    {
        get => Volatile.Read(ref _defaultResourcePolicy);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            Volatile.Write(ref _defaultResourcePolicy, value);
        }
    }
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
    public int QueuedOutboundPackets => _outbound.Reader.Count;
    public int QueuedCallbacks => _callbacks.Reader.Count;
    public int QueuedRequests => _requests.Reader.Count;
    public int ExecutingApplicationWork { get { lock (_gate) return _invocations.Count; } }

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
        if (destinationHash.Length != Destination.HashLength) throw new ArgumentException("Destination hash must contain 16 bytes.", nameof(destinationHash));
        var hops = _router.HopsTo(destinationHash) ?? Packet.HopLimit;
        var timeout = establishmentTimeout ?? ((Link.EstablishmentTimeoutPerHop * Math.Max(1, hops)) + _router.FirstHopTimeout(destinationHash));
        if (!double.IsFinite(timeout) || timeout <= 0) throw new ArgumentOutOfRangeException(nameof(establishmentTimeout));
        lock (_gate)
        {
            if (_router.IsIdentityBlocked(destinationIdentity.Hash)) throw new InvalidOperationException("The destination identity is blocked.");
            if (_active.Count + _pending.Count >= MaximumLinks) throw new InvalidOperationException("The link manager's link limit has been reached.");
            EnsureStarted();
            _suspended = false;
            var link = Link.CreateInitiator(this, destinationIdentity, destinationHash, hops, timeout);
            link.Established = established;
            link.Closed = closed;
            link.RequestTime = Now;
            _pending[link.LinkIdHex] = link;
            if (!_outbound.Writer.TryWrite((link.RequestPacket!, null)))
            {
                Report("Link outbound queue is full; link establishment rejected.");
                link.FailEstablishment();
            }
            return link;
        }
    }

    public Link? Find(ReadOnlySpan<byte> linkId)
    {
        var key = Convert.ToHexStringLower(linkId);
        lock (_gate) return _active.GetValueOrDefault(key) ?? _pending.GetValueOrDefault(key);
    }

    internal bool Enqueue(Packet packet, IPacketInterface? iface)
    {
        lock (_gate)
        {
            if (_stopping || _disposed || _suspended)
            {
                Report("Link packet rejected because the link manager is stopped.");
                return false;
            }
            EnsureStarted();
            if (_outbound.Writer.TryWrite((packet, iface))) return true;
            Report("Link outbound queue is full; packet rejected.");
            return false;
        }
    }

    internal void Dispatch(Action action) => DispatchAsync(() => { action(); return ValueTask.CompletedTask; });

    internal void DispatchAsync(Func<ValueTask> action)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                if (!_callbacks.Writer.TryWrite(action)) Report("Link callback queue is full; observer notification rejected.");
                return;
            }
            if (!_disposed && !_suspended)
            {
                EnsureStarted();
                if (!_callbacks.Writer.TryWrite(action)) Report("Link callback queue is full; observer notification rejected.");
                return;
            }
        }
        if (!StartInvocation(action, _observerSlots, out _)) Report("Link observer concurrency exhausted after shutdown; notification rejected.");
    }

    internal bool DispatchRequest(Action action) => DispatchRequestAsync(_ => { action(); return ValueTask.CompletedTask; });

    internal bool DispatchRequestAsync(Func<CancellationToken, ValueTask> action)
    {
        lock (_gate)
        {
            if (_stopping || _disposed || _suspended)
            {
                Report("Link request work rejected because the manager is stopped.");
                return false;
            }
            EnsureStarted();
            var cancellation = _cts!.Token;
            if (_requests.Writer.TryWrite(() => action(cancellation))) return true;
            Report("Link request queue is full; request rejected.");
            return false;
        }
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
            var key = Convert.ToHexStringLower(Link.LinkIdFromRequest(received.Packet));
            if (_active.ContainsKey(key) || _pending.ContainsKey(key)) return;
            if (_active.Count + _pending.Count >= MaximumLinks)
            {
                Report("Incoming link rejected because the link limit has been reached.");
                return;
            }
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
            _loops = [Task.Run(() => PumpAsync(cts.Token)),
                Task.Run(() => WorkLoopAsync(_callbacks.Reader, _observerSlots, cts.Token)),
                Task.Run(() => WorkLoopAsync(_requests.Reader, _requestSlots, cts.Token)),
                Task.Run(() => TickLoopAsync(cts.Token))];
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
            if (cts is not null) CancelOwnedWork(cts);
            try { await Task.WhenAll(loops).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            foreach (var link in Links) link.Teardown();
            while (_outbound.Reader.TryRead(out _)) { }
            while (_requests.Reader.TryRead(out _)) { }
            Task[] cleanup;
            lock (_gate) cleanup = [.. _requiredCleanup];
            await Task.WhenAll(cleanup).ConfigureAwait(false);
            while (_callbacks.Reader.TryRead(out var callback))
            {
                if (!StartInvocation(callback, _observerSlots, out _))
                    Report("Link observer concurrency exhausted during shutdown; notification rejected.");
            }
            Task[] executing;
            lock (_gate) executing = [.. _invocations];
            try { await Task.WhenAll(executing).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                Report("Application callbacks remain executing after shutdown; their bounded slots remain reserved until they return.");
            }
            cts?.Dispose();
            lock (_gate)
            {
                _cts = null;
                _loops = [];
                _stopping = false;
            }
        }
    }

    /// <summary>Terminal cleanup; unlike StopAsync, subsequent Connect calls are rejected.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private void Report(string message)
    {
        try { Log?.Invoke(message); }
        catch (Exception observer) { System.Diagnostics.Trace.TraceError("Link diagnostic observer failed: {0}; original: {1}", observer, message); }
    }

    private bool StartInvocation(Func<ValueTask> action, SemaphoreSlim slots, out Task task)
    {
        if (!slots.Wait(0))
        {
            task = Task.CompletedTask;
            return false;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        task = completion.Task;
        lock (_gate) _invocations.Add(task);
        _ = Task.Run(async () =>
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) { Report("Link application work failed: " + ex.Message); }
            finally
            {
                lock (_gate) _invocations.Remove(completion.Task);
                slots.Release();
                completion.TrySetResult();
            }
        });
        return true;
    }

    internal void CancelOwnedWork(CancellationTokenSource cancellation)
    {
        var task = cancellation.CancelAsync();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _invocations.Add(completion.Task);
        _ = ObserveCancellationAsync(task, cancellation, completion);
    }

    internal void ObserveRequiredCleanup(Task work, string description)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _requiredCleanup.Add(completion.Task);
        _ = ObserveOwnedWorkAsync(work, description, completion);
    }

    private async Task ObserveOwnedWorkAsync(Task work, string description, TaskCompletionSource completion)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception ex) { Report($"Link {description} failed: {ex.Message}"); }
        finally
        {
            lock (_gate) _requiredCleanup.Remove(completion.Task);
            completion.TrySetResult();
        }
    }

    private async Task ObserveCancellationAsync(Task task, CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { Report("Link cancellation callback failed: " + ex.Message); }
        finally
        {
            lock (_gate) _invocations.Remove(completion.Task);
            cancellation.Dispose();
            completion.TrySetResult();
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
                    if (packet.PacketType == PacketType.LinkRequest && Find(Link.LinkIdFromRequest(packet)) is not { Status: LinkStatus.Pending }) continue;
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
                    if (packet.PacketType == PacketType.LinkRequest)
                    {
                        var link = Find(Link.LinkIdFromRequest(packet));
                        if (link is { IsInitiator: true, Status: LinkStatus.Pending })
                        {
                            link.FailEstablishment();
                        }
                    }
                    try { Log?.Invoke($"Could not send link packet: {ex.Message}"); }
                    catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WorkLoopAsync(ChannelReader<Func<ValueTask>> queue, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            while (await queue.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                slots.Release();
                if (queue.TryRead(out var action) && !StartInvocation(action, slots, out _))
                    Report("Application work concurrency exhausted; queued work rejected.");
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
