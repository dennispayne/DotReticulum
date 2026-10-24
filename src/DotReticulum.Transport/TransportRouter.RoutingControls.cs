using System.Collections.Concurrent;
using DotReticulum.Core;

namespace DotReticulum.Transport;

/// <summary>Upstream per-interface announce, origin-mode and recursive path-request controls.</summary>
public sealed record InterfaceAnnounceOptions
{
    public int Gravity { get; init; }
    public AnnounceRateLimit? RateLimit { get; init; }
    public double AnnounceCap { get; init; } = 0.02;
    public int QueueCapacity { get; init; } = 4096;
    public TimeSpan QueueLifetime { get; init; } = TimeSpan.FromHours(3);
    public bool AnnouncesFromInternal { get; init; } = true;
    public bool AnnouncesToInternal { get; init; }
    public Func<bool>? IngressLimitAnnounces { get; init; }
    public Func<bool>? IngressLimitPathRequests { get; init; }
    public Func<bool>? EgressLimitPathRequests { get; init; }
    public int HeldAnnounceCapacity { get; init; } = 256;
    public bool RecursivePathRequests { get; init; }
    public InterfaceIngressOptions Ingress { get; init; } = new();
}

public sealed partial class TransportRouter
{
    private static readonly InterfaceAnnounceOptions DefaultAnnounceOptions = new();
    private readonly ConcurrentDictionary<IPacketInterface, InterfaceMode> _routingModes = new();
    private readonly ConcurrentDictionary<IPacketInterface, InterfaceAnnounceOptions> _interfaceAnnounceOptions = new();
    private readonly ConcurrentDictionary<IPacketInterface, InterfaceTrafficController> _interfaceTrafficControllers = new();
    private readonly Dictionary<IPacketInterface, InterfaceAnnounceState> _interfaceAnnounceState = new();
    private readonly Dictionary<string, DateTimeOffset> _lastPathRequests = new();
    private readonly Dictionary<string, PathRecovery> _pathRecoveries = new();
    private readonly SemaphoreSlim _recoveryDrain = new(1, 1);

    public void SetInterfaceAnnounceOptions(IPacketInterface iface, InterfaceAnnounceOptions options)
    {
        ArgumentNullException.ThrowIfNull(iface);
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.AnnounceCap) || options.AnnounceCap is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.QueueCapacity < 1 || options.HeldAnnounceCapacity < 0 || options.QueueLifetime <= TimeSpan.Zero
            || options.RateLimit is { } rate && (rate.Target <= TimeSpan.Zero || rate.Grace < 0 || rate.Penalty < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(options));
        var ingress = options.Ingress;
        ArgumentNullException.ThrowIfNull(ingress);
        if (ingress.BurstHold < TimeSpan.Zero || ingress.NewTime < TimeSpan.Zero || ingress.BurstPenalty < TimeSpan.Zero
            || ingress.HeldReleaseInterval < TimeSpan.Zero
            || new[] { ingress.BurstFrequencyNew, ingress.BurstFrequency, ingress.PathRequestBurstFrequencyNew,
                ingress.PathRequestBurstFrequency, ingress.EgressPathRequestFrequency }.Any(value => !double.IsFinite(value) || value < 0))
            throw new ArgumentOutOfRangeException(nameof(options));
        _interfaceAnnounceOptions[iface] = options;
        TrafficController(iface).Configure(ingress);
    }

    public InterfaceAnnounceOptions GetInterfaceAnnounceOptions(IPacketInterface iface) =>
        _interfaceAnnounceOptions.GetValueOrDefault(iface, DefaultAnnounceOptions);

    private InterfaceTrafficController TrafficController(IPacketInterface iface) => _interfaceTrafficControllers.GetOrAdd(iface,
        source => new InterfaceTrafficController(_time, GetInterfaceAnnounceOptions(source).Ingress));

    public InterfaceTrafficControlInfo GetInterfaceTrafficControl(IPacketInterface iface) => TrafficController(iface).Snapshot();

    public bool ShouldIngressLimitPathRequests(IPacketInterface iface) => GetInterfaceAnnounceOptions(iface).IngressLimitPathRequests?.Invoke()
        ?? TrafficController(iface).ShouldIngressLimitPathRequests();

    public bool ShouldIngressLimitAnnounces(IPacketInterface iface) => GetInterfaceAnnounceOptions(iface).IngressLimitAnnounces?.Invoke()
        ?? TrafficController(iface).ShouldIngressLimitAnnounces();

    public bool ShouldEgressLimitPathRequests(IPacketInterface iface) => GetInterfaceAnnounceOptions(iface).EgressLimitPathRequests?.Invoke()
        ?? TrafficController(iface).ShouldEgressLimitPathRequests();
    public int QueuedAnnounces(IPacketInterface iface)
    {
        lock (_gate) return _interfaceAnnounceState.TryGetValue(iface, out var state) ? state.Queue.Count : 0;
    }

    public int HeldIngressAnnounces(IPacketInterface iface)
    {
        lock (_gate) return _interfaceAnnounceState.TryGetValue(iface, out var state) ? state.Held.Count : 0;
    }

    public int PendingPathRecoveries
    {
        get { lock (_gate) return _pathRecoveries.Count; }
    }

    public int PendingPathDiscoveries
    {
        get { lock (_gate) return _discovery.Count; }
    }

    private TimeSpan PathLifetimeFor(IPacketInterface? source) => source is null ? _options.PathLifetime
        : GetInterfaceMode(source) switch
        {
            InterfaceMode.AccessPoint => _options.AccessPointPathLifetime,
            InterfaceMode.Roaming => _options.RoamingPathLifetime,
            _ => _options.PathLifetime,
        };

    private TimeSpan MediumDiscoveryTimeout()
    {
        var slowest = _manager.Interfaces.Where(iface => iface.Bitrate > 0).Select(iface => iface.Bitrate).DefaultIfEmpty(long.MaxValue).Min();
        return TimeSpan.FromSeconds(Math.Max(_options.DiscoveryTimeout.TotalSeconds, Packet.Mtu * 8.0 * 2 / Math.Max(5, slowest) + 6));
    }

    public void NotifyLinkProofValidated(byte[] destinationHash, int proofHops)
    {
        ArgumentNullException.ThrowIfNull(destinationHash);
        if (proofHops is < 0 or >= Packet.HopLimit) return;
        Engine.RebalancePath(destinationHash, proofHops);
    }

    /// <summary>Called for a failed outgoing handshake, never for an intentionally closed active link.</summary>
    public void NotifyLinkEstablishmentFailed(byte[] destinationHash, IPacketInterface? attachedInterface = null)
    {
        ArgumentNullException.ThrowIfNull(destinationHash);
        if (destinationHash.Length != Destination.HashLength) throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destinationHash));
        if (_options.TransportEnabled) return;
        Engine.ExpirePath(destinationHash);
        if (!IsSharedClient) QueuePathRecovery(destinationHash, null);
    }

    private void RecoverForwardedLink(LinkEntry entry)
    {
        var path = Engine.GetPath(entry.DestinationHash);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (path is not null && _lastPathRequests.TryGetValue(Convert.ToHexStringLower(entry.DestinationHash), out var last)
                && now - last < _options.PathRequestMinimumInterval) return;
        }
        if (path is not null && entry.TakenHops != 0 && path.Hops != 1 && entry.TakenHops != 1) return;
        var blocked = path is not null && entry.TakenHops != 0 ? entry.ReceivedInterface : null;
        if (blocked is not null && _options.TransportEnabled && GetInterfaceMode(blocked) != InterfaceMode.Boundary)
            Engine.SetPathResponsiveness(entry.DestinationHash, PathResponsiveness.Unresponsive);
        if (!_options.TransportEnabled) Engine.ExpirePath(entry.DestinationHash);
        QueuePathRecovery(entry.DestinationHash, blocked, path is null);
    }

    private void QueuePathRecovery(byte[] destinationHash, IPacketInterface? excluded, bool bypassThrottle = false)
    {
        var key = Convert.ToHexStringLower(destinationHash);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_disposed || _pathRecoveries.ContainsKey(key) || _pathRecoveries.Count >= _options.ControlCapacity) return;
            if (!bypassThrottle && _lastPathRequests.TryGetValue(key, out var last) && now - last < _options.PathRequestMinimumInterval) return;
            _pathRecoveries[key] = new PathRecovery(destinationHash.ToArray(), excluded);
            RecordPathRequestLocked(key, now);
        }
    }

    private void RecordPathRequest(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) RecordPathRequestLocked(Convert.ToHexStringLower(destinationHash), _time.GetUtcNow());
    }

    private void RecordPathRequestLocked(string key, DateTimeOffset now)
    {
        _lastPathRequests[key] = now;
        if (_lastPathRequests.Count > _options.ControlCapacity)
            _lastPathRequests.Remove(_lastPathRequests.MinBy(entry => entry.Value).Key);
    }

    public async ValueTask FlushPathRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await _recoveryDrain.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PathRecovery[] pending;
            lock (_gate) pending = _pathRecoveries.Values.ToArray();
            foreach (var recovery in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tag = System.Security.Cryptography.RandomNumberGenerator.GetBytes(Destination.HashLength);
                foreach (var iface in _manager.Interfaces)
                {
                    if (ReferenceEquals(iface, recovery.Excluded)) continue;
                    try { await RequestPathAsync(recovery.Destination, iface, tag, cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log?.Invoke($"Could not rediscover path on {Describe(iface)}: {ex.Message}");
                    }
                }
                lock (_gate) _pathRecoveries.Remove(Convert.ToHexStringLower(recovery.Destination));
            }
        }
        finally { _recoveryDrain.Release(); }
    }

    private bool ReserveRecursivePathRequest(IPacketInterface iface, int packetSize)
    {
        var options = GetInterfaceAnnounceOptions(iface);
        if (ShouldEgressLimitPathRequests(iface)) return false;
        lock (_gate)
        {
            var state = AnnounceStateLocked(iface);
            var now = _time.GetUtcNow();
            if (state.Queue.Count != 0 || now < state.AllowedAt) return false;
            if (iface.Bitrate > 0) state.AllowedAt = now + AnnounceWait(iface, options, packetSize);
            return true;
        }
    }

    private bool CanBroadcastAnnounce(IPacketInterface target, IPacketInterface? origin, bool local)
    {
        var targetMode = GetInterfaceMode(target);
        var targetOptions = GetInterfaceAnnounceOptions(target);
        if (!local && origin is null) return false;
        var originMode = origin is null ? InterfaceMode.Full : GetInterfaceMode(origin);
        if (!local && !targetOptions.AnnouncesFromInternal && originMode == InterfaceMode.Internal) return false;
        if (targetMode == InterfaceMode.AccessPoint) return false;
        if (local) return true;
        return targetMode switch
        {
            InterfaceMode.Internal => originMode != InterfaceMode.Boundary || GetInterfaceAnnounceOptions(origin!).AnnouncesToInternal,
            InterfaceMode.Roaming => originMode is not (InterfaceMode.Roaming or InterfaceMode.Boundary),
            InterfaceMode.Boundary => originMode != InterfaceMode.Roaming,
            _ => true,
        };
    }

    private async ValueTask SendAnnounceControlledAsync(Packet packet, IPacketInterface? excluded, IPacketInterface? onlyOn,
        CancellationToken cancellationToken, bool applyModeToTarget = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Engine.GetPath(packet.DestinationHash.Span);
        bool local;
        lock (_gate) local = _locals.ContainsKey(Convert.ToHexStringLower(packet.DestinationHash.Span));
        // A locally created, unregistered announce still has a zero-hop local origin.
        local |= packet.Hops == 0 && path is null;
        var origin = path?.Interface ?? excluded;
        var targets = onlyOn is null ? _manager.Interfaces : new[] { onlyOn };
        foreach (var iface in targets)
        {
            if (!_manager.IsOutgoing(iface)) continue;
            if (onlyOn is null && ReferenceEquals(iface, excluded)) continue;
            if ((onlyOn is null || applyModeToTarget) && !CanBroadcastAnnounce(iface, origin, local)) continue;
            var mode = GetInterfaceMode(iface);
            if ((onlyOn is null || applyModeToTarget) && packet.Hops > 0
                && mode is (InterfaceMode.Full or InterfaceMode.PointToPoint or InterfaceMode.Gateway) && iface.Bitrate > 0)
            {
                var options = GetInterfaceAnnounceOptions(iface);
                var now = _time.GetUtcNow();
                lock (_gate)
                {
                    var state = AnnounceStateLocked(iface);
                    if (state.Queue.Count > 0 || now <= state.AllowedAt)
                    {
                        QueueAnnounceLocked(state, options, packet, now);
                        continue;
                    }
                    state.AllowedAt = now + AnnounceWait(iface, options, packet.ToArray().Length);
                }
            }
            try
            {
                await _manager.SendToAsync(packet, iface, cancellationToken).ConfigureAwait(false);
                TrafficController(iface).SentAnnounce(packet.ToArray().Length);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log?.Invoke($"Could not transmit announce on {Describe(iface)}: {ex.Message}"); }
        }
    }

    private static TimeSpan AnnounceWait(IPacketInterface iface, InterfaceAnnounceOptions options, int size) =>
        TimeSpan.FromSeconds(size * 8.0 / iface.Bitrate / options.AnnounceCap);

    private static void QueueAnnounceLocked(InterfaceAnnounceState state, InterfaceAnnounceOptions options, Packet packet, DateTimeOffset now)
    {
        var key = Convert.ToHexStringLower(packet.DestinationHash.Span);
        if (!Announce.TryParse(packet, out var announce)) return;
        if (state.Queue.TryGetValue(key, out var existing))
        {
            if (announce.Emitted > existing.Emitted) state.Queue[key] = new QueuedAnnounce(packet, now, announce.Emitted);
        }
        else if (state.Queue.Count < options.QueueCapacity) state.Queue[key] = new QueuedAnnounce(packet, now, announce.Emitted);
    }

    private async ValueTask FlushInterfaceAnnounceQueuesAsync(CancellationToken cancellationToken)
    {
        KeyValuePair<IPacketInterface, InterfaceAnnounceState>[] states;
        lock (_gate) states = _interfaceAnnounceState.ToArray();
        foreach (var (iface, state) in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_manager.IsOutgoing(iface)) continue;
            var options = GetInterfaceAnnounceOptions(iface);
            QueuedAnnounce? selected;
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                foreach (var key in state.Queue.Where(entry => now > entry.Value.QueuedAt + options.QueueLifetime).Select(entry => entry.Key).ToArray())
                    state.Queue.Remove(key);
                if (state.Queue.Count == 0 || now < state.AllowedAt || state.Sending || iface.Bitrate <= 0) continue;
                selected = state.Queue.Values.OrderBy(entry => entry.Packet.Hops).ThenBy(entry => entry.QueuedAt).First();
                state.Sending = true;
                state.AllowedAt = now + AnnounceWait(iface, options, selected.Packet.ToArray().Length);
            }
            try
            {
                await _manager.SendToAsync(selected.Packet, iface, cancellationToken).ConfigureAwait(false);
                TrafficController(iface).SentAnnounce(selected.Packet.ToArray().Length);
                lock (_gate)
                {
                    var key = Convert.ToHexStringLower(selected.Packet.DestinationHash.Span);
                    if (state.Queue.GetValueOrDefault(key) == selected) state.Queue.Remove(key);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log?.Invoke($"Could not drain announce queue on {Describe(iface)}: {ex.Message}"); }
            finally { lock (_gate) state.Sending = false; }
        }
    }

    private bool TryHoldIngressAnnounce(ReceivedPacket received)
    {
        if (received.Packet.PacketType != PacketType.Announce || Engine.GetPath(received.Packet.DestinationHash.Span) is not null) return false;
        var key = Convert.ToHexStringLower(received.Packet.DestinationHash.Span);
        lock (_gate)
        {
            if (_lastPathRequests.ContainsKey(key) || _discovery.ContainsKey(key)) return false;
        }
        var options = GetInterfaceAnnounceOptions(received.Source);
        if (!ShouldIngressLimitAnnounces(received.Source)) return false;
        if (received.Packet.Hops + InboundHopIncrement(received.Source) >= AnnounceEngine.MaxHops - 1) return true;
        var validation = Announce.Validate(received.Packet);
        using var identity = validation.Identity;
        if (!validation.IsValid || identity is null || IsIdentityBlocked(identity.Hash)) return true;
        lock (_gate)
        {
            var held = AnnounceStateLocked(received.Source).Held;
            if (held.TryGetValue(key, out var previous))
            {
                Announce.TryParse(previous.Packet, out var oldAnnounce);
                Announce.TryParse(received.Packet, out var newAnnounce);
                if (newAnnounce!.Emitted > oldAnnounce!.Emitted) held[key] = received;
            }
            else if (held.Count < options.HeldAnnounceCapacity) held[key] = received;
        }
        return true;
    }

    private bool PrepareAnnounceIngress(ReceivedPacket received)
    {
        var validation = Announce.Validate(received.Packet);
        using var identity = validation.Identity;
        if (!validation.IsValid || identity is null || IsIdentityBlocked(identity.Hash)) return false;
        TrafficController(received.Source).ReceivedAnnounce(received.Raw.Length);
        return !TryHoldIngressAnnounce(received);
    }

    private async ValueTask ReleaseHeldIngressAsync(CancellationToken cancellationToken)
    {
        KeyValuePair<IPacketInterface, InterfaceAnnounceState>[] states;
        lock (_gate) states = _interfaceAnnounceState.ToArray();
        foreach (var (iface, state) in states)
        {
            if (!_manager.Contains(iface) || GetInterfaceAnnounceOptions(iface).IngressLimitAnnounces?.Invoke() == true) continue;
            ReceivedPacket? received;
            lock (_gate) received = state.Held.Values.OrderBy(entry => entry.Packet.Hops).FirstOrDefault();
            if (received is not null && TrafficController(iface).ReserveHeldRelease())
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate) state.Held.Remove(Convert.ToHexStringLower(received.Packet.DestinationHash.Span));
                if (_inboundWake is not null) QueueHeldIngressRelease(received);
                else
                {
                    try { await HandleAsync(received, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        lock (_gate) state.Held.TryAdd(Convert.ToHexStringLower(received.Packet.DestinationHash.Span), received);
                        throw;
                    }
                }
            }
        }
    }

    private InterfaceAnnounceState AnnounceStateLocked(IPacketInterface iface)
    {
        if (!_interfaceAnnounceState.TryGetValue(iface, out var state)) _interfaceAnnounceState[iface] = state = new();
        return state;
    }

    private void DetachRoutingControls(IPacketInterface iface)
    {
        _routingModes.TryRemove(iface, out _);
        _interfaceAnnounceOptions.TryRemove(iface, out _);
        _interfaceTrafficControllers.TryRemove(iface, out _);
        lock (_gate) _interfaceAnnounceState.Remove(iface);
        lock (_inboundGate)
        {
            foreach (var queue in _inboundQueues)
            {
                var remaining = queue.Where(entry => !ReferenceEquals(entry.Received.Source, iface)).ToArray();
                queue.Clear();
                foreach (var entry in remaining) queue.Enqueue(entry);
            }
            foreach (var key in _inflightPathRequests.Where(entry => ReferenceEquals(entry.Value.Source, iface)).Select(entry => entry.Key).ToArray())
                _inflightPathRequests.Remove(key);
            foreach (var request in _inflightPathRequests.Values) request.Waiting.Remove(iface);
        }
    }

    private void ClearRoutingControls()
    {
        lock (_gate)
        {
            _interfaceAnnounceState.Clear();
            _lastPathRequests.Clear();
            _pathRecoveries.Clear();
        }
        lock (_inboundGate)
        {
            foreach (var queue in _inboundQueues) queue.Clear();
            _inflightPathRequests.Clear();
        }
    }

    private void CullRoutingControls()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var key in _lastPathRequests.Where(entry => now - entry.Value > TimeSpan.FromSeconds(45)).Select(entry => entry.Key).ToArray())
                _lastPathRequests.Remove(key);
        }
        lock (_inboundGate)
            foreach (var key in _inflightPathRequests.Where(entry => now - entry.Value.Created > TimeSpan.FromSeconds(45)).Select(entry => entry.Key).ToArray())
                _inflightPathRequests.Remove(key);
        foreach (var iface in _routingModes.Keys.Concat(_interfaceAnnounceOptions.Keys).Concat(_interfaceTrafficControllers.Keys)
            .Distinct().Where(iface => !_manager.Contains(iface)).ToArray())
            DetachRoutingControls(iface);
    }

    private sealed record PathRecovery(byte[] Destination, IPacketInterface? Excluded);
    private sealed record QueuedAnnounce(Packet Packet, DateTimeOffset QueuedAt, long Emitted);
    private sealed class InterfaceAnnounceState
    {
        public DateTimeOffset AllowedAt { get; set; }
        public bool Sending { get; set; }
        public Dictionary<string, QueuedAnnounce> Queue { get; } = new();
        public Dictionary<string, ReceivedPacket> Held { get; } = new();
    }
}
