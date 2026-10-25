using System.Threading.Channels;

namespace DotReticulum.Core;

/// <summary>
/// Owns a set of <see cref="IPacketInterface"/>s: starts them once, merges their received packets into one bounded
/// channel (wait-on-full backpressure), drops malformed packets, and fans out sends. Disposing the manager disposes
/// every interface.
/// </summary>
public sealed class PacketInterfaceManager : IAsyncDisposable
{
    public const int DefaultChannelCapacity = 256;

    private readonly List<IPacketInterface> _interfaces;
    private readonly HashSet<IPacketInterface> _children = [];
    private readonly Dictionary<IPacketInterface, InterfaceState> _states = [];
    private readonly Dictionary<ISpawningPacketInterface, Action<IPacketInterface>> _subscriptions = [];
    private readonly Dictionary<IReconnectingPacketInterface, Action> _connectionSubscriptions = [];
    private readonly Dictionary<IReconnectingPacketInterface, Action> _disconnectionSubscriptions = [];
    private readonly SemaphoreSlim _management = new(1, 1);
    private readonly bool _keepAlive;
    private bool _completed;
    private bool _receiversLaunched;
    private readonly Channel<ReceivedPacket> _channel;
    private readonly PacketInterfaceIngressQueue _ingressQueue;
    private readonly CancellationTokenSource _receiversCts = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _receiverTasks = [];
    private bool _started;
    private Task? _disposeTask;
    private int _activeReceivers;

    public PacketInterfaceManager(IEnumerable<IPacketInterface> interfaces, int channelCapacity = DefaultChannelCapacity, bool keepAlive = false)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);
        _interfaces = [.. interfaces];
        _keepAlive = keepAlive;
        ChannelCapacity = channelCapacity;
        foreach (var iface in _interfaces)
        {
            ArgumentNullException.ThrowIfNull(iface);
        }

        if (_interfaces.Distinct().Count() != _interfaces.Count)
        {
            throw new ArgumentException("The same interface was supplied more than once.", nameof(interfaces));
        }

        _channel = Channel.CreateBounded<ReceivedPacket>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        _ingressQueue = new PacketInterfaceIngressQueue(() => QueuedPackets, channelCapacity);
        foreach (var iface in _interfaces)
        {
            Register(iface, null);
        }
    }

    public int ChannelCapacity { get; }
    public int QueuedPackets => _channel.Reader.Count;
    public double QueuePressure => (double)QueuedPackets / ChannelCapacity;
    public void SetIngressQueueSource(Func<int> depth, int capacity)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _ingressQueue.SetSource(depth, capacity);
        }
    }
    public void NotifyIngressQueuePressure() => _ingressQueue.NotifyPressure();
    public event Action<IPacketInterface, Exception>? Diagnostic;
    public event Action<IPacketInterface>? InterfaceRemoved;
    public event Action<IPacketInterface>? InterfaceConnected;
    public event Action<IPacketInterface>? InterfaceDisconnected;

    /// <summary>Configures broadcast/attached-interface selection; direct routed transmission is unaffected.</summary>
    public void SetInterfaceOutgoing(IPacketInterface iface, bool outgoing)
    {
        ArgumentNullException.ThrowIfNull(iface);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started) throw new InvalidOperationException("Configure outgoing selection before starting the manager.");
            if (!_states.TryGetValue(iface, out var state) || state.Parent is not null)
                throw new ArgumentException("The interface is not an owned top-level interface.", nameof(iface));
            state.Outgoing = outgoing;
        }
    }

    public bool IsOutgoing(IPacketInterface iface)
    {
        ArgumentNullException.ThrowIfNull(iface);
        lock (_gate) return _states.TryGetValue(iface, out var state) && state.IsOutgoing;
    }

    /// <summary>Transfers ownership of an access code before start; spawned peers inherit the parent's code.</summary>
    public void SetInterfaceAccessCode(IPacketInterface iface, InterfaceAccessCode? accessCode)
    {
        ArgumentNullException.ThrowIfNull(iface);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started) throw new InvalidOperationException("Configure access codes before starting the manager.");
            if (!_states.TryGetValue(iface, out var state) || state.Parent is not null)
                throw new ArgumentException("The interface is not an owned top-level interface.", nameof(iface));
            if (ReferenceEquals(state.AccessCode, accessCode)) return;
            state.AccessCode?.Dispose();
            state.AccessCode = accessCode;
        }
    }

    public IReadOnlyList<PacketInterfaceStatistics> GetStatistics()
    {
        lock (_gate) return _states.Values.Select(s => s.Snapshot()).ToArray();
    }

    /// <summary>Records a parsed packet rejected by transport packet filtering.</summary>
    public void RecordPacketFilterHit(IPacketInterface source)
    {
        ArgumentNullException.ThrowIfNull(source);
        InterfaceState state;
        lock (_gate)
        {
            if (!_states.TryGetValue(source, out state!))
                throw new ArgumentException("The source interface is not owned by this manager.", nameof(source));
        }
        state.Filtered();
    }

    /// <summary>Records a parsed packet accepted by transport packet filtering.</summary>
    public void RecordAcceptedPacket(IPacketInterface source)
    {
        ArgumentNullException.ThrowIfNull(source);
        InterfaceState state;
        lock (_gate)
        {
            if (!_states.TryGetValue(source, out state!))
                throw new ArgumentException("The source interface is not owned by this manager.", nameof(source));
        }
        state.Accepted();
    }

    /// <summary>Records a malformed or otherwise protocol-invalid packet at the transport boundary.</summary>
    public void RecordProtocolViolation(IPacketInterface source)
    {
        ArgumentNullException.ThrowIfNull(source);
        InterfaceState state;
        lock (_gate)
        {
            if (!_states.TryGetValue(source, out state!))
                throw new ArgumentException("The source interface is not owned by this manager.", nameof(source));
        }
        state.ProtocolViolation();
    }

    private sealed class InterfaceState(IPacketInterface iface, InterfaceState? parent, CancellationToken lifetime, bool isolateFailure = false)
    {
        public IPacketInterface Interface { get; } = iface;
        public InterfaceState? Parent { get; } = parent;
        public bool Isolated { get; } = isolateFailure;
        public bool Outgoing { get; set; } = iface is not IPacketInterfaceChildPolicy policy || policy.Outgoing;
        public bool IsOutgoing => Outgoing && (Parent is null
            || Interface is IPacketInterfaceChildPolicy { InheritParentOutgoing: false } || Parent.IsOutgoing);
        private bool CountParentTraffic => Interface is not IPacketInterfaceChildPolicy { CountParentTraffic: false };
        public InterfaceAccessCode? AccessCode { get; set; } =
            iface is IPacketInterfaceChildPolicy { InheritParentAccessCode: false } ? null : parent?.AccessCode;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public Task Receiver { get; set; } = Task.CompletedTask;
        public long Rxb, Txb, RxPackets, TxPackets, Arxb, Atxb, Arxc, Atxc, Malformed, FilteredPackets, AcceptedPackets;
        public long ProtocolViolations, SendFailures, FailedBytes, Waits, IfacViolations;
        public int Pending;
        public PacketInterfaceStatistics Snapshot() => new(Interface, Parent?.Interface,
            Interlocked.Read(ref Rxb), Interlocked.Read(ref Txb), Interlocked.Read(ref RxPackets), Interlocked.Read(ref TxPackets),
            Interlocked.Read(ref Arxb), Interlocked.Read(ref Atxb), Interlocked.Read(ref Arxc), Interlocked.Read(ref Atxc),
            Interlocked.Read(ref Malformed), Interlocked.Read(ref SendFailures), Interlocked.Read(ref FailedBytes),
            Interlocked.Read(ref Waits), Volatile.Read(ref Pending))
        {
            IfacViolations = Interlocked.Read(ref IfacViolations),
            FilteredPackets = Interlocked.Read(ref FilteredPackets),
            AcceptedPackets = Interlocked.Read(ref AcceptedPackets),
            ProtocolViolations = Interlocked.Read(ref ProtocolViolations),
            IfacSize = AccessCode?.Size,
            IfacNetworkName = AccessCode?.NetworkName,
            IfacSignature = AccessCode?.Signature.ToArray(),
            Outgoing = IsOutgoing,
        };

        public void RejectedAccessCode()
        {
            Interlocked.Increment(ref IfacViolations);
            if (CountParentTraffic) Parent?.RejectedAccessCode();
        }

        public void ProtocolViolation()
        {
            Interlocked.Increment(ref ProtocolViolations);
            if (CountParentTraffic) Parent?.ProtocolViolation();
        }

        public void Filtered()
        {
            Interlocked.Increment(ref FilteredPackets);
            if (CountParentTraffic) Parent?.Filtered();
        }

        public void Accepted()
        {
            Interlocked.Increment(ref AcceptedPackets);
            if (CountParentTraffic) Parent?.Accepted();
        }

        public void Received(int length, Packet? packet)
        {
            Interlocked.Add(ref Rxb, length);
            Interlocked.Increment(ref RxPackets);
            if (packet is null) Interlocked.Increment(ref Malformed);
            else if (packet.PacketType == PacketType.Announce)
            {
                Interlocked.Add(ref Arxb, length);
                Interlocked.Increment(ref Arxc);
            }
            if (CountParentTraffic) Parent?.Received(length, packet);
        }

        public void Sent(int length, bool announce, bool failed)
        {
            if (failed)
            {
                Interlocked.Increment(ref SendFailures);
                Interlocked.Add(ref FailedBytes, length);
            }
            else
            {
                Interlocked.Add(ref Txb, length);
                Interlocked.Increment(ref TxPackets);
                if (announce)
                {
                    Interlocked.Add(ref Atxb, length);
                    Interlocked.Increment(ref Atxc);
                }
            }
            if (CountParentTraffic) Parent?.Sent(length, announce, failed);
        }
    }

    private void Report(IPacketInterface iface, Exception error)
    {
        System.Diagnostics.Trace.TraceError("Packet interface {0}: {1}", iface, error);
        if (Diagnostic is not { } handlers) return;
        foreach (Action<IPacketInterface, Exception> handler in handlers.GetInvocationList())
        {
            try { handler(iface, error); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("Interface diagnostic observer failed: {0}", ex); }
        }
    }

    private void NotifyRemoved(IPacketInterface iface)
    {
        if (InterfaceRemoved is not { } handlers) return;
        foreach (Action<IPacketInterface> handler in handlers.GetInvocationList())
        {
            try { handler(iface); }
            catch (Exception error) { Report(iface, error); }
        }
    }

    private void Register(IPacketInterface iface, InterfaceState? parent, bool isolateFailure = false)
    {
        if (iface is IPacketInterfaceIngressFeedback feedback) feedback.SetIngressQueue(_ingressQueue);
        _states.Add(iface, new InterfaceState(iface, parent, _receiversCts.Token, isolateFailure));
        if (iface is ISpawningPacketInterface spawner)
        {
            Action<IPacketInterface> handler = child => AdoptChild(spawner, child);
            _subscriptions.Add(spawner, handler);
            spawner.ChildConnected += handler;
        }
        if (iface is IReconnectingPacketInterface reconnecting)
        {
            Action handler = () => NotifyConnected(iface);
            _connectionSubscriptions.Add(reconnecting, handler);
            reconnecting.Connected += handler;
            Action disconnected = () =>
            {
                if (InterfaceDisconnected is not { } observers) return;
                foreach (Action<IPacketInterface> observer in observers.GetInvocationList())
                {
                    try { observer(iface); }
                    catch (Exception error) { Report(iface, error); }
                }
            };
            _disconnectionSubscriptions.Add(reconnecting, disconnected);
            reconnecting.Disconnected += disconnected;
        }
    }

    private void NotifyConnected(IPacketInterface iface)
    {
        if (InterfaceConnected is not { } observers) return;
        foreach (Action<IPacketInterface> observer in observers.GetInvocationList())
        {
            try { observer(iface); }
            catch (Exception error) { Report(iface, error); }
        }
    }

    private void LaunchReceiver(IPacketInterface iface)
    {
        var state = _states[iface];
        Interlocked.Increment(ref _activeReceivers);
        state.Receiver = Task.Run(() => ReceiveLoopAsync(state), CancellationToken.None);
        _receiverTasks.Add(state.Receiver);
        _ = state.Receiver.ContinueWith(t =>
        {
            lock (_gate) _receiverTasks.Remove(t);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Starts and transfers ownership of a new top-level interface without interrupting other receivers.</summary>
    public async ValueTask AttachAsync(IPacketInterface iface, CancellationToken cancellationToken = default, bool isolateFailure = false,
        InterfaceAccessCode? accessCode = null, bool outgoing = true)
    {
        ArgumentNullException.ThrowIfNull(iface);
        await _management.WaitAsync(cancellationToken).ConfigureAwait(false);
        var registered = false;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                if (!_started || !_receiversLaunched || _completed) throw new InvalidOperationException("The manager is not running.");
                if (_interfaces.Contains(iface)) throw new ArgumentException("The interface is already attached.", nameof(iface));
                Register(iface, null, isolateFailure);
                _states[iface].AccessCode = accessCode;
                _states[iface].Outgoing = outgoing;
                _interfaces.Add(iface);
                registered = true;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _receiversCts.Token);
            await iface.StartAsync(linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                LaunchReceiver(iface);
            }
        }
        catch (Exception error)
        {
            if (registered)
            {
                try { await DetachCoreAsync(iface).ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            }
            throw;
        }
        finally { _management.Release(); }
    }

    /// <summary>Removes an interface and its accepted peers, cancelling blocked receives before joining them.</summary>
    public async ValueTask<bool> DetachAsync(IPacketInterface iface, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(iface);
        await _management.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            return await DetachCoreAsync(iface).ConfigureAwait(false);
        }
        finally { _management.Release(); }
    }

    private async Task<bool> DetachCoreAsync(IPacketInterface iface)
    {
        InterfaceState[] removed;
        lock (_gate)
        {
            if (!_states.TryGetValue(iface, out var root)) return false;
            removed = _states.Values.Where(s => ReferenceEquals(s, root) || ReferenceEquals(s.Parent, root)).ToArray();
            foreach (var state in removed)
            {
                _interfaces.Remove(state.Interface);
                _states.Remove(state.Interface);
                _children.Remove(state.Interface);
                if (state.Interface is ISpawningPacketInterface spawner && _subscriptions.Remove(spawner, out var handler))
                    spawner.ChildConnected -= handler;
                if (state.Interface is IReconnectingPacketInterface reconnecting && _connectionSubscriptions.Remove(reconnecting, out var connected))
                    reconnecting.Connected -= connected;
                if (state.Interface is IReconnectingPacketInterface recovering && _disconnectionSubscriptions.Remove(recovering, out var disconnected))
                    recovering.Disconnected -= disconnected;
            }
        }
        var errors = new List<Exception>();
        foreach (var state in removed)
        {
            NotifyRemoved(state.Interface);
            await state.Cancellation.CancelAsync().ConfigureAwait(false);
            try { await state.Interface.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Report(state.Interface, ex); errors.Add(ex); }
        }
        await Task.WhenAll(removed.Select(s => s.Receiver)).ConfigureAwait(false);
        foreach (var state in removed)
        {
            state.Cancellation.Dispose();
            if (state.Parent is null) state.AccessCode?.Dispose();
        }
        if (errors.Count > 0) throw new AggregateException("Interface detach failed.", errors);
        return true;
    }

    /// <summary>A snapshot of the current interfaces, including children spawned by <see cref="ISpawningPacketInterface"/>s.</summary>
    public IReadOnlyList<IPacketInterface> Interfaces
    {
        get
        {
            lock (_gate)
            {
                return [.. _interfaces];
            }
        }
    }

    /// <summary>Whether <paramref name="iface"/> is currently owned by (and not yet removed from) this manager.</summary>
    public bool Contains(IPacketInterface iface)
    {
        lock (_gate)
        {
            return _interfaces.Contains(iface);
        }
    }

    private void AdoptChild(ISpawningPacketInterface parent, IPacketInterface child)
    {
        var adopted = false;
        lock (_gate)
        {
            if (_disposeTask is null && _states.TryGetValue(parent, out var parentState) && !_interfaces.Contains(child))
            {
                _interfaces.Add(child);
                _children.Add(child);
                Register(child, parentState);
                if (_receiversLaunched)
                {
                    LaunchReceiver(child);
                }

                adopted = true;
            }
        }

        if (adopted)
        {
            if (child is IReconnectingPacketInterface { IsConnected: true }) NotifyConnected(child);
            return;
        }
        _ = DisposeRejectedChildAsync(child);
    }

    private async Task DisposeRejectedChildAsync(IPacketInterface child)
    {
        try { await child.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Report(child, ex); }
    }
    /// <summary>
    /// Starts every interface in order, then begins receiving. If any start fails (or is cancelled) the manager disposes
    /// all interfaces, becomes unusable, and the original exception is rethrown. May be called only once.
    /// </summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started)
            {
                throw new InvalidOperationException("The manager has already been started.");
            }

            _started = true;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _receiversCts.Token);
            foreach (var iface in Interfaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(IsDisposing, this);
                if (Contains(iface) && !IsChild(iface))
                {
                    await iface.StartAsync(linked.Token).ConfigureAwait(false);
                }
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                _receiversLaunched = true;
                foreach (var iface in _interfaces)
                {
                    LaunchReceiver(iface);
                }

                if (_interfaces.Count == 0 && !_keepAlive)
                {
                    _completed = true;
                    _channel.Writer.TryComplete();
                }
            }
        }
        catch (Exception ex)
        {
            _channel.Writer.TryComplete(ex);
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeEx)
            {
                throw new AggregateException(ex, disposeEx);
            }

            throw;
        }
    }

    /// <summary>Yields valid packets from all interfaces. The sequence faults if a receiver faults.</summary>
    public async IAsyncEnumerable<Packet> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var received in ReceiveWithSourceAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return received.Packet;
        }
    }

    /// <summary>Like <see cref="ReceiveAsync"/> but includes the arrival interface.</summary>
    public async IAsyncEnumerable<ReceivedPacket> ReceiveWithSourceAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var received in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Contains(received.Source)) yield return received;
        }
    }

    /// <summary>Sends to every interface. Failures on one interface do not prevent delivery to the others.</summary>
    public ValueTask SendAsync(Packet packet, CancellationToken cancellationToken = default) =>
        SendCoreAsync(packet, null, cancellationToken);

    /// <summary>Sends to every interface except <paramref name="excluded"/> (typically the ingress interface).</summary>
    public ValueTask SendExceptAsync(Packet packet, IPacketInterface excluded, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        if (!Contains(excluded))
        {
            throw new ArgumentException("The excluded interface is not owned by this manager.", nameof(excluded));
        }

        return SendCoreAsync(packet, excluded, cancellationToken);
    }

    /// <summary>Sends to exactly one interface (a routed unicast hop). Throws if the interface is not (or no longer) owned by this manager.</summary>
    public ValueTask SendToAsync(Packet packet, IPacketInterface target, CancellationToken cancellationToken = default) =>
        SendRawToAsync(packet?.ToArray() ?? throw new ArgumentNullException(nameof(packet)), target, cancellationToken);

    /// <summary>As <see cref="SendToAsync"/> for already serialised wire bytes.</summary>
    public async ValueTask SendRawToAsync(ReadOnlyMemory<byte> raw, IPacketInterface target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!_started)
            {
                throw new InvalidOperationException("The manager has not been started.");
            }

            if (!_interfaces.Contains(target))
            {
                throw new ArgumentException("The target interface is not owned by this manager.", nameof(target));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await SendOneAsync(target, raw, cancellationToken).ConfigureAwait(false);
    }

    private bool IsChild(IPacketInterface iface)
    {
        lock (_gate)
        {
            return _children.Contains(iface);
        }
    }

    private bool IsDisposing
    {
        get
        {
            lock (_gate)
            {
                return _disposeTask is not null;
            }
        }
    }

    private async ValueTask SendCoreAsync(Packet packet, IPacketInterface? excluded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!_started)
            {
                throw new InvalidOperationException("The manager has not been started.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = packet.ToArray();
        var targets = Interfaces;
        var pending = new List<Task>(targets.Count);
        foreach (var iface in targets)
        {
            if (ReferenceEquals(iface, excluded))
            {
                continue;
            }

            if (iface is ISpawningPacketInterface { SendsThroughChildren: true }) continue;
            if (!IsOutgoing(iface)) continue;
            pending.Add(SendOneAsync(iface, bytes, cancellationToken));
        }

        var all = Task.WhenAll(pending);
        try
        {
            await all.ConfigureAwait(false);
        }
        catch
        {
            if (all.Exception is { InnerExceptions.Count: > 1 } aggregate)
            {
                throw aggregate;
            }

            throw;
        }
    }

    private async Task SendOneAsync(IPacketInterface iface, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        InterfaceState state;
        lock (_gate)
        {
            if (!_states.TryGetValue(iface, out state!)) throw new ArgumentException("The interface is no longer attached.", nameof(iface));
        }
        Interlocked.Increment(ref state.Pending);
        var attemptedLength = bytes.Length;
        try
        {
            if (bytes.Length > PacketMtu(iface))
                throw new ArgumentException("Packet exceeds the interface packet MTU.", nameof(bytes));
            ReadOnlyMemory<byte> frame = state.AccessCode is { } code ? code.Protect(bytes.Span) : bytes;
            attemptedLength = frame.Length;
            await iface.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            state.Sent(frame.Length, bytes.Length > 0 && (bytes.Span[0] & 3) == (byte)PacketType.Announce, false);
        }
        catch (Exception ex)
        {
            state.Sent(attemptedLength, false, true);
            if (ex is not OperationCanceledException) Report(iface, ex);
            throw;
        }
        finally { Interlocked.Decrement(ref state.Pending); }
    }

    private async Task ReceiveLoopAsync(InterfaceState state)
    {
        var iface = state.Interface;
        var token = state.Cancellation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            await foreach (var raw in iface.ReceiveAsync(token).ConfigureAwait(false))
            {
                ReadOnlyMemory<byte> decoded = raw;
                if (state.AccessCode is { } code)
                {
                    if (raw.Length > iface.HardwareMtu + code.Size)
                    {
                        state.ProtocolViolation();
                        state.Received(raw.Length, null);
                        continue;
                    }
                    if (!code.TryUnprotect(raw.Span, out var authenticated))
                    {
                        state.RejectedAccessCode();
                        state.Received(raw.Length, null);
                        continue;
                    }
                    decoded = authenticated!;
                }
                else if (raw.Length < 3)
                {
                    state.ProtocolViolation();
                    state.Received(raw.Length, null);
                    continue;
                }
                else if ((raw.Span[0] & 0x80) != 0)
                {
                    state.RejectedAccessCode();
                    state.Received(raw.Length, null);
                    continue;
                }
                if (!Packet.TryParse(decoded, out var packet, PacketMtu(iface)))
                {
                    state.ProtocolViolation();
                    state.Received(raw.Length, null);
                    continue;
                }

                state.Received(raw.Length, packet);
                var received = new ReceivedPacket(decoded, packet, iface);
                if (!_channel.Writer.TryWrite(received))
                {
                    Interlocked.Increment(ref state.Waits);
                    await _channel.Writer.WriteAsync(received, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception ex) when (state.Parent is not null || state.Isolated)
        {
            Report(iface, ex);
        }
        catch (Exception ex)
        {
            Report(iface, ex);
            lock (_gate) _completed = true;
            _channel.Writer.TryComplete(ex);
            try
            {
                await _receiversCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            var ownsCleanup = false;
            if (state.Parent is not null)
            {
                lock (_gate)
                {
                    _interfaces.Remove(iface);
                    _children.Remove(iface);
                    ownsCleanup = _states.Remove(iface);
                }

                if (ownsCleanup) try
                    {
                        NotifyRemoved(iface);
                        await iface.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Report(iface, ex);
                    }
                if (ownsCleanup) state.Cancellation.Dispose();
            }

            if (Interlocked.Decrement(ref _activeReceivers) == 0 && !_keepAlive)
            {
                lock (_gate) _completed = true;
                _channel.Writer.TryComplete();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            task = _disposeTask;
        }

        return new ValueTask(task);
    }

    private static int PacketMtu(IPacketInterface iface) =>
        iface.SupportsMtuDiscovery ? iface.HardwareMtu : Math.Min(Packet.Mtu, iface.HardwareMtu);

    private async Task DisposeCoreAsync()
    {
        // Yield so the caller's lock is released before any real work runs.
        await Task.Yield();
        try
        {
            await _receiversCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        _channel.Writer.TryComplete();

        await _management.WaitAsync().ConfigureAwait(false);
        try
        {
            var errors = new List<Exception>();
            IPacketInterface[] toDispose;
            lock (_gate)
            {
                toDispose = [.. _interfaces];
            }

            lock (_gate)
            {
                foreach (var (spawner, handler) in _subscriptions) spawner.ChildConnected -= handler;
                _subscriptions.Clear();
                foreach (var (iface, handler) in _connectionSubscriptions) iface.Connected -= handler;
                _connectionSubscriptions.Clear();
                foreach (var (iface, handler) in _disconnectionSubscriptions) iface.Disconnected -= handler;
                _disconnectionSubscriptions.Clear();
            }

            foreach (var iface in toDispose)
            {
                try
                {
                    await iface.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            Task[] receivers;
            lock (_gate)
            {
                receivers = [.. _receiverTasks];
            }

            // Receiver loops never throw (they convert faults to channel completion).
            await Task.WhenAll(receivers).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var state in _states.Values)
                {
                    state.Cancellation.Dispose();
                    if (state.Parent is null) state.AccessCode?.Dispose();
                }
                _states.Clear();
                _interfaces.Clear();
                _children.Clear();
            }
            _receiversCts.Dispose();

            if (errors.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }
            else if (errors.Count > 1)
            {
                throw new AggregateException(errors);
            }
        }
        finally { _management.Release(); }
    }
}
