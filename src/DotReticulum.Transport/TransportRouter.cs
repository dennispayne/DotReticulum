using System.Security.Cryptography;
using DotReticulum.Core;

namespace DotReticulum.Transport;

/// <summary>Upstream interface modes (<c>Interface.MODE_*</c>); they steer path discovery and path lifetimes.</summary>
public enum InterfaceMode : byte
{
    Full = 1,
    PointToPoint = 2,
    AccessPoint = 3,
    Roaming = 4,
    Boundary = 5,
    Gateway = 6,
    Internal = 7,
}

public sealed class TransportRouterOptions
{
    /// <summary>Upstream <c>enable_transport</c>: rebroadcast announces, answer path requests from the path table and forward packets for others.</summary>
    public bool TransportEnabled { get; init; }

    /// <summary>The transport identity; its hash is the transport id. Generated (ephemeral) when null.</summary>
    public Identity? TransportIdentity { get; init; }

    public AnnounceRateLimit? RateLimit { get; init; }

    /// <summary>Total packet-hash filter capacity; two generations are rotated at half (upstream <c>hashlist_maxsize</c>).</summary>
    public int HashlistCapacity { get; init; } = 1_000_000;

    public int PathRequestTagCapacity { get; init; } = 16_000;

    public int ReverseTableCapacity { get; init; } = 65_536;

    public TimeSpan ReverseTimeout { get; init; } = TimeSpan.FromMinutes(8);

    public TimeSpan PathRequestGrace { get; init; } = TimeSpan.FromSeconds(0.4);

    public TimeSpan RoamingGrace { get; init; } = TimeSpan.FromSeconds(1.5);

    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan PathLifetime { get; init; } = TimeSpan.FromDays(7);

    public TimeSpan AccessPointPathLifetime { get; init; } = TimeSpan.FromDays(1);

    public TimeSpan RoamingPathLifetime { get; init; } = TimeSpan.FromHours(6);

    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan CullInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Runtime blackhole policy, evaluated using the authenticated identity hash.</summary>
    public Func<byte[], bool>? IsIdentityBlocked { get; init; }
}

/// <summary>A destination hosted by this node. Delivery of packets addressed to it goes to <see cref="PacketReceived"/>.</summary>
public sealed class LocalDestination(Identity identity, Destination destination)
{
    public Identity Identity { get; } = identity;
    public Destination Destination { get; } = destination;
    public byte[]? AppData { get; init; }
    public DestinationRatchets? Ratchets { get; init; }

    /// <summary>Receives non-announce packets whose destination hash is this destination (payload still encrypted).</summary>
    public Func<ReceivedPacket, ValueTask>? PacketReceived { get; set; }

    /// <summary>Decrypts a token addressed to this destination, honouring ratchets (null when it cannot be decrypted).</summary>
    public byte[]? Decrypt(ReadOnlySpan<byte> token) => Ratchets is { } ratchets ? ratchets.Decrypt(Identity, token) : Identity.Decrypt(token);

    /// <summary>Upstream <c>accepts_links</c>: whether incoming link requests to this destination are accepted.</summary>
    public bool AcceptLinks { get; set; }

    /// <summary>Called once an incoming link is established (after the initiator's RTT packet).</summary>
    public Action<Link>? LinkEstablished { get; set; }

    /// <summary>Whether data packets received over this destination's links are proven automatically.</summary>
    public ProofStrategy ProofStrategy { get; set; } = ProofStrategy.None;

    /// <summary>Consulted for <see cref="ProofStrategy.App"/>: return true to prove the packet.</summary>
    public Func<Packet, bool>? ProofRequested { get; set; }

    /// <summary>Upstream <c>set_max_request_size</c>: requests larger than this (bytes) are ignored. Null means unlimited.</summary>
    public int? MaxRequestSize { get; set; }

    private readonly Dictionary<string, RequestHandler> _requestHandlers = [];

    /// <summary>Upstream <c>register_request_handler</c>.</summary>
    public void RegisterRequestHandler(string path, Func<RequestContext, object?> generator, RequestPolicy allow = RequestPolicy.None,
        IReadOnlyCollection<byte[]>? allowedList = null, bool autoCompress = true)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Invalid path specified", nameof(path));
        ArgumentNullException.ThrowIfNull(generator);
        lock (_requestHandlers) _requestHandlers[PathKey(path)] = new RequestHandler(path, generator, allow, allowedList, autoCompress);
    }

    public bool DeregisterRequestHandler(string path)
    {
        lock (_requestHandlers) return _requestHandlers.Remove(PathKey(path));
    }

    internal RequestHandler? FindRequestHandler(ReadOnlySpan<byte> pathHash)
    {
        lock (_requestHandlers) return _requestHandlers.GetValueOrDefault(Convert.ToHexStringLower(pathHash));
    }

    internal bool HasRequestHandlers
    {
        get { lock (_requestHandlers) return _requestHandlers.Count > 0; }
    }

    internal static byte[] PathHash(string path) => Identity.TruncatedHash(System.Text.Encoding.UTF8.GetBytes(path));

    private static string PathKey(string path) => Convert.ToHexStringLower(PathHash(path));
}

/// <summary>
/// The routing half of upstream <c>Transport</c>: packet filtering/deduplication, path requests and responses (including cached-announce replay
/// and discovery requests), announce rebroadcast with HEADER_2 transport rewriting (via <see cref="AnnounceEngine"/>), forwarding of packets that
/// name this node as next hop, the reverse table that carries proofs back, outbound path selection and expiry. Link-table behaviour is not part of
/// the link table (link requests forwarded as transport, link proofs validated and relayed, link-addressed packets relayed) and delivery of link
/// traffic to the <see cref="LinkManager"/> (<see cref="Links"/>).
/// </summary>
public sealed partial class TransportRouter : IAsyncDisposable
{
    public static readonly byte[] PathRequestHash = Destination.Create(DestinationType.Plain, "rnstransport", ["path", "request"]).Hash;

    private readonly PacketInterfaceManager _manager;
    private readonly TransportRouterOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, LocalDestination> _locals = new();
    private readonly Dictionary<IPacketInterface, InterfaceMode> _modes = new();
    private readonly Dictionary<string, ReverseEntry> _reverse = new();
    private readonly Dictionary<string, LinkEntry> _linkTable = new();
    private readonly Dictionary<string, DiscoveryEntry> _discovery = new();
    private HashSet<string> _hashes = [];
    private HashSet<string> _hashesPrev = [];
    private HashSet<string> _tags = [];
    private HashSet<string> _tagsPrev = [];
    private CancellationTokenSource? _runCancellation;
    private TaskCompletionSource? _runCompletion;
    private volatile bool _disposed;
    private readonly bool _ownsTransportIdentity;

    public TransportRouter(PacketInterfaceManager manager, TransportRouterOptions? options = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _options = options ?? new TransportRouterOptions();
        _time = _options.TimeProvider ?? TimeProvider.System;
        _ownsTransportIdentity = _options.TransportIdentity is null;
        TransportIdentity = _options.TransportIdentity ?? Identity.Generate();
        Engine = new AnnounceEngine(new AnnounceEngineOptions
        {
            TransportEnabled = _options.TransportEnabled,
            TransportId = TransportIdentity.Hash,
            RateLimit = _options.RateLimit,
        }, timeProvider: _time);
        Links = new LinkManager(this, _manager, _time);
    }

    public Identity TransportIdentity { get; }

    public byte[] TransportId => TransportIdentity.Hash;

    public AnnounceEngine Engine { get; }

    /// <summary>Links terminating at this node (initiated or accepted).</summary>
    public LinkManager Links { get; }

    public int LinkTableCount
    {
        get { lock (_gate) return _linkTable.Count; }
    }

    public bool TransportEnabled => _options.TransportEnabled;

    public bool IsIdentityBlocked(byte[] identityHash) => _options.IsIdentityBlocked?.Invoke(identityHash) == true;

    /// <summary>Releases routing state and local links that depended on a detached interface or child peer.</summary>
    public void DetachInterface(IPacketInterface iface)
    {
        ArgumentNullException.ThrowIfNull(iface);
        if (IsSharedClient) SharedConnectionDisappeared();
        var affectedLinks = Links.Links.Where(link => ReferenceEquals(link.AttachedInterface, iface)
            || (link.AttachedInterface is null && ReferenceEquals(Engine.GetPath(link.DestinationHash)?.Interface, iface))).ToArray();
        Engine.CullPaths(source => ReferenceEquals(source, iface) ? null : TimeSpan.MaxValue);
        lock (_gate)
        {
            _modes.Remove(iface);
            _localClientInterfaces.Remove(iface);
            foreach (var key in _reverse.Where(kv => ReferenceEquals(kv.Value.Received, iface) || ReferenceEquals(kv.Value.Outbound, iface)).Select(kv => kv.Key).ToArray()) _reverse.Remove(key);
            foreach (var key in _linkTable.Where(kv => ReferenceEquals(kv.Value.ReceivedInterface, iface) || ReferenceEquals(kv.Value.NextHopInterface, iface)).Select(kv => kv.Key).ToArray()) _linkTable.Remove(key);
            foreach (var entry in _discovery.Values) entry.Interfaces.Remove(iface);
        }
        foreach (var link in affectedLinks) link.Teardown();
    }

    /// <summary>Optional diagnostic sink (one line per event).</summary>
    public Action<string>? Log
    {
        get => Engine.Log;
        set => Engine.Log = value;
    }

    public int ReverseTableCount
    {
        get { lock (_gate) return _reverse.Count; }
    }

    public void SetInterfaceMode(IPacketInterface iface, InterfaceMode mode)
    {
        lock (_gate) _modes[iface] = mode;
    }

    public InterfaceMode GetInterfaceMode(IPacketInterface iface)
    {
        lock (_gate) return _modes.TryGetValue(iface, out var m) ? m : InterfaceMode.Full;
    }

    public void RegisterLocalDestination(LocalDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate) _locals[Convert.ToHexStringLower(destination.Destination.Hash)] = destination;
        Engine.RegisterLocalDestination(destination.Destination.Hash);
    }

    public void DeregisterLocalDestination(LocalDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate) _locals.Remove(Convert.ToHexStringLower(destination.Destination.Hash));
        Engine.DeregisterLocalDestination(destination.Destination.Hash);
    }

    public bool HasPath(ReadOnlySpan<byte> destinationHash) => Engine.GetPath(destinationHash) is not null;

    /// <summary>Hops to the destination, or null when no path is known.</summary>
    public int? HopsTo(ReadOnlySpan<byte> destinationHash) => Engine.GetPath(destinationHash)?.Hops;

    /// <summary>Runs the receive loop and the announce/cull timers until cancelled or the manager's receive stream ends.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TaskCompletionSource completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runCancellation is not null) throw new InvalidOperationException("The router is already running.");
            _runCancellation = linked;
            _runCompletion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Task[] workers = [];
        try
        {
            workers = [AnnounceLoopAsync(linked.Token), CullLoopAsync(linked.Token), ReceiveLoopAsync(linked.Token)];
            await await Task.WhenAny(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await linked.CancelAsync().ConfigureAwait(false);
                try { await Task.WhenAll(workers).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            finally
            {
                try { await Links.StopAsync().ConfigureAwait(false); }
                finally
                {
                    FailReceipts();
                    lock (_gate)
                    {
                        _runCancellation = null;
                        completion.TrySetResult();
                    }
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var received in _manager.ReceiveWithSourceAsync(cancellationToken).ConfigureAwait(false))
        {
            try { await HandleAsync(received, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log?.Invoke($"Error while processing a packet: {ex.Message}"); }
        }
    }

    /// <summary>Stops the current dispatcher and links. The externally owned interface manager is not disposed; RunAsync can be called again.</summary>
    public async Task StopAsync()
    {
        Task? completion;
        lock (_gate)
        {
            _runCancellation?.Cancel();
            completion = _runCompletion?.Task;
        }
        if (completion is not null) await completion.ConfigureAwait(false);
        await Links.StopAsync().ConfigureAwait(false);
        FailReceipts();
    }

    /// <summary>Terminal router cleanup. The constructor-supplied interface manager remains owned by the caller.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            try { await Links.DisposeAsync().ConfigureAwait(false); }
            finally { if (_ownsTransportIdentity) TransportIdentity.Dispose(); }
        }
    }

    /// <summary>Processes one received packet (the receive loop calls this; exposed for deterministic tests).</summary>
    public async ValueTask HandleAsync(ReceivedPacket received, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var packet = received.Packet;
        if (!Filter(packet)) return;
        var fromLocalClient = IsLocalClientInterface(received.Source);
        var canRelayLinks = _options.TransportEnabled || HasLocalClients;
        var linkAddressed = canRelayLinks && IsLinkTableTraffic(packet);
        if (!linkAddressed && (packet.Context != 0xFF || packet.PacketType != PacketType.Proof)) RememberHash(packet);

        if (packet.PacketType == PacketType.Announce)
        {
            if (HandleAnnounce(received))
                await RelaySharedAnnounceAsync(received, cancellationToken).ConfigureAwait(false);
            return;
        }

        var hops = packet.Hops + InboundHopIncrement(received.Source);
        if (hops >= Packet.HopLimit) return;

        if (linkAddressed)
        {
            await ForwardLinkTrafficAsync(received, (byte)hops, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (packet.PacketType == PacketType.Proof && packet.Context == LinkContext.LrProof)
        {
            if (canRelayLinks && HasLinkEntry(packet.DestinationHash.Span))
            {
                await RelayLinkProofAsync(received, (byte)hops, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Links.HandleLinkProof(received);
            }

            return;
        }

        var forLocalClient = Engine.GetPath(packet.DestinationHash.Span)?.Interface is { } destinationInterface && IsLocalClientInterface(destinationInterface);
        if ((!IsSharedClient && _options.TransportEnabled && packet.HeaderType == HeaderType.Header2)
            || (packet.DestinationType == DestinationType.Single && (fromLocalClient || forLocalClient)
                && Engine.GetPath(packet.DestinationHash.Span) is not null))
        {
            var routed = received;
            if (packet.HeaderType == HeaderType.Header1)
            {
                var transported = Packet.Create(packet.PacketType, packet.DestinationType, packet.DestinationHash, packet.Payload,
                    packet.Context, HeaderType.Header2, TransportId, TransportType.Transport, packet.ContextFlag, packet.Hops, packet.ReservedBit);
                routed = new ReceivedPacket(transported.ToArray(), transported, received.Source);
            }
            await ForwardAsync(routed, (byte)hops, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (packet.PacketType == PacketType.LinkRequest)
        {
            DeliverLinkRequest(received);
            return;
        }

        if (packet.DestinationType == DestinationType.Link && Links.HandleLinkPacket(received)) return;

        switch (packet.PacketType)
        {
            case PacketType.Data:
                if (packet.DestinationType == DestinationType.Plain && packet.DestinationHash.Span.SequenceEqual(PathRequestHash))
                {
                    await HandlePathRequestAsync(received, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (packet.DestinationType == DestinationType.Plain)
                    await RelaySharedPlainAsync(received, cancellationToken).ConfigureAwait(false);
                else if (fromLocalClient && packet.DestinationType == DestinationType.Single)
                    await RelayUnknownLocalPacketAsync(received, cancellationToken).ConfigureAwait(false);
                await DeliverLocalAsync(received).ConfigureAwait(false);
                break;
            case PacketType.Proof when packet.Context != 0xFF:
                if (!await RouteProofAsync(received, (byte)hops, cancellationToken).ConfigureAwait(false) && !TryValidateReceipt(received)) await DeliverLocalAsync(received).ConfigureAwait(false);
                break;
            default:
                await DeliverLocalAsync(received).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Sends a locally originated packet along the best known path (HEADER_2 with the next hop's transport id when more than one hop away),
    /// or broadcasts it on every interface when no path is known. Announces and PLAIN/GROUP packets are always broadcast.
    /// </summary>
    public async ValueTask SendAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.PacketType != PacketType.Announce && packet.DestinationType is DestinationType.Single or DestinationType.Link
            && packet.HeaderType == HeaderType.Header1 && Engine.GetPath(packet.DestinationHash.Span) is { Interface: { } iface } path && _manager.Contains(iface))
        {
            var outgoing = packet;
            if (path.Hops > 1 && Engine.GetNextHop(packet.DestinationHash.Span) is { } nextHop)
            {
                outgoing = Packet.Create(packet.PacketType, packet.DestinationType, packet.DestinationHash, packet.Payload, packet.Context,
                    HeaderType.Header2, nextHop, TransportType.Transport, packet.ContextFlag, packet.Hops, packet.ReservedBit);
            }

            await _manager.SendToAsync(outgoing, iface, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _manager.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Announces a local destination on every interface (or only <paramref name="onlyOn"/>), optionally as a path response.</summary>
    public async ValueTask AnnounceAsync(LocalDestination destination, IPacketInterface? onlyOn = null, bool pathResponse = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        var packet = Announce.Create(destination.Identity, destination.Destination, destination.AppData, destination.Ratchets, pathResponse);
        if (onlyOn is null) await _manager.SendAsync(packet, cancellationToken).ConfigureAwait(false);
        else await _manager.SendToAsync(packet, onlyOn, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Upstream <c>Transport.request_path</c>: broadcasts a path request (carrying our transport id when transport is enabled).</summary>
    public ValueTask RequestPathAsync(ReadOnlySpan<byte> destinationHash, IPacketInterface? onInterface = null, byte[]? tag = null, CancellationToken cancellationToken = default)
    {
        if (destinationHash.Length != Destination.HashLength) throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destinationHash));
        tag ??= RandomNumberGenerator.GetBytes(Destination.HashLength);
        var data = _options.TransportEnabled ? [.. destinationHash, .. TransportId, .. tag] : (byte[])[.. destinationHash, .. tag];
        var packet = Packet.Create(PacketType.Data, DestinationType.Plain, PathRequestHash, data);
        return onInterface is null ? _manager.SendAsync(packet, cancellationToken) : _manager.SendToAsync(packet, onInterface, cancellationToken);
    }

    private bool Filter(Packet packet)
    {
        if (!IsSharedClient && packet.HeaderType == HeaderType.Header2 && packet.PacketType != PacketType.Announce
            && !packet.TransportId.Span.SequenceEqual(TransportId)) return false;

        if (packet.Context is 0xFA or 0x03 or 0x05 or 0x01 or 0x08 or 0x0E) return true;

        if (packet.DestinationType is DestinationType.Plain or DestinationType.Group)
        {
            if (packet.PacketType == PacketType.Announce) return false;
            return packet.Hops <= 1;
        }

        if (!Seen(packet)) return true;
        return packet.PacketType == PacketType.Announce && packet.DestinationType == DestinationType.Single;
    }

    private bool Seen(Packet packet)
    {
        var key = Convert.ToBase64String(packet.CalculateHash());
        lock (_gate) return _hashes.Contains(key) || _hashesPrev.Contains(key);
    }

    private void RememberHash(Packet packet)
    {
        var key = Convert.ToBase64String(packet.CalculateHash());
        lock (_gate)
        {
            _hashes.Add(key);
            if (_hashes.Count > Math.Max(1, _options.HashlistCapacity / 2))
            {
                _hashesPrev = _hashes;
                _hashes = [];
            }
        }
    }

    private bool RememberTag(string tag)
    {
        lock (_gate)
        {
            if (_tags.Contains(tag) || _tagsPrev.Contains(tag)) return false;
            _tags.Add(tag);
            if (_tags.Count > Math.Max(1, _options.PathRequestTagCapacity / 2))
            {
                _tagsPrev = _tags;
                _tags = [];
            }

            return true;
        }
    }

    private bool HandleAnnounce(ReceivedPacket received)
    {
        if (Announce.TryParse(received.Packet, out var announce) && IsIdentityBlocked(Identity.TruncatedHash(announce.PublicKey))) return false;
        var fromLocalClient = IsLocalClientInterface(received.Source);
        var result = Engine.Process(received.Packet, received.Source, InboundHopIncrement(received.Source),
            queueRebroadcast: !fromLocalClient && !IsSharedClient);
        if (result.Outcome is not (AnnounceOutcome.Accepted or AnnounceOutcome.AcceptedRateLimited))
        {
            if (result.Outcome == AnnounceOutcome.Invalid) Log?.Invoke($"Dropped invalid announce from {Describe(received.Source)}");
            return false;
        }

        Log?.Invoke($"Destination {Convert.ToHexStringLower(received.Packet.DestinationHash.Span)} is now {result.Hops} hops away on {Describe(received.Source)}");
        if (!_options.TransportEnabled && !fromLocalClient) return true;

        DiscoveryEntry? waiting;
        var key = Convert.ToHexStringLower(received.Packet.DestinationHash.Span);
        lock (_gate)
        {
            _discovery.Remove(key, out waiting);
        }

        if (waiting is null) return true;
        foreach (var target in waiting.Interfaces)
        {
            if (_manager.Contains(target)) Engine.QueuePathResponse(received.Packet.DestinationHash.Span, target, TimeSpan.Zero);
        }
        return true;
    }

    private async ValueTask ForwardAsync(ReceivedPacket received, byte hops, CancellationToken cancellationToken)
    {
        var packet = received.Packet;
        if (packet.PacketType == PacketType.Announce) return;
        var destination = packet.DestinationHash.ToArray();
        var path = Engine.GetPath(destination);
        if (path?.Interface is not { } outbound || Engine.GetNextHop(destination) is not { } nextHop || !_manager.Contains(outbound))
        {
            bool isLocal;
            lock (_gate) isLocal = _locals.ContainsKey(Convert.ToHexStringLower(destination));
            if (isLocal)
            {
                if (packet.PacketType == PacketType.LinkRequest) DeliverLinkRequest(received);
                else await DeliverLocalAsync(received).ConfigureAwait(false);
                return;
            }

            Log?.Invoke($"Got packet in transport but no known path to {Convert.ToHexStringLower(destination)}; dropping");
            return;
        }

        var raw = received.Raw.ToArray();
        byte[] forwarded;
        if (path.Hops > 1)
        {
            forwarded = (byte[])raw.Clone();
            forwarded[1] = hops;
            nextHop.CopyTo(forwarded.AsSpan(2));
        }
        else if (path.Hops == 1)
        {
            forwarded = new byte[raw.Length - Destination.HashLength];
            forwarded[0] = (byte)(raw[0] & 0x0F);
            forwarded[1] = hops;
            raw.AsSpan(2 + Destination.HashLength).CopyTo(forwarded.AsSpan(2));
        }
        else
        {
            forwarded = (byte[])raw.Clone();
            forwarded[1] = hops;
        }

        if (packet.PacketType == PacketType.LinkRequest)
        {
            ClampLinkRequestMtu(forwarded);
            var now = _time.GetUtcNow();
            var remaining = Math.Max(1, path.Hops);
            var entry = new LinkEntry(now, nextHop, outbound, path.Hops, received.Source, hops, destination, false, now.AddSeconds(Link.EstablishmentTimeoutPerHop * remaining));
            lock (_gate) _linkTable[Convert.ToHexStringLower(Link.LinkIdFromRequest(packet))] = entry;
            await _manager.SendRawToAsync(forwarded, outbound, cancellationToken).ConfigureAwait(false);
            Engine.TouchPath(destination);
            return;
        }

        var key = Convert.ToHexStringLower(packet.CalculateTruncatedHash());
        lock (_gate)
        {
            _reverse[key] = new ReverseEntry(received.Source, outbound, _time.GetUtcNow());
            if (_reverse.Count > _options.ReverseTableCapacity) TrimReverse();
        }

        await _manager.SendRawToAsync(forwarded, outbound, cancellationToken).ConfigureAwait(false);
        Engine.TouchPath(destination);
    }

    private bool HasLinkEntry(ReadOnlySpan<byte> linkId)
    {
        var key = Convert.ToHexStringLower(linkId);
        lock (_gate) return _linkTable.ContainsKey(key);
    }

    private bool IsLinkTableTraffic(Packet packet) =>
        packet.PacketType is not (PacketType.Announce or PacketType.LinkRequest)
        && !(packet.PacketType == PacketType.Proof && packet.Context == LinkContext.LrProof)
        && HasLinkEntry(packet.DestinationHash.Span);

    /// <summary>The link MTU is fixed at 500 here, so a larger MTU signalled by the initiator is lowered on the way through (path-MTU clamp).</summary>
    private static void ClampLinkRequestMtu(byte[] raw)
    {
        var headerSize = (raw[0] & 0x40) != 0 ? Packet.Header2Size : Packet.Header1Size;
        if (raw.Length - headerSize != Link.EcPubSize + Link.SignallingSize) return;
        var signalling = raw.AsSpan(raw.Length - Link.SignallingSize);
        if (Link.MtuFromSignalling(signalling) <= Packet.Mtu) return;
        Link.SignallingBytes(Packet.Mtu, Link.ModeFromSignalling(signalling)).CopyTo(signalling);
    }

    private void DeliverLinkRequest(ReceivedPacket received)
    {
        LocalDestination? local;
        lock (_gate) _locals.TryGetValue(Convert.ToHexStringLower(received.Packet.DestinationHash.Span), out local);
        if (local is not null && local.Destination.Type == received.Packet.DestinationType) Links.HandleLinkRequest(received, local);
    }

    /// <summary>Relays a packet addressed to a link that passes through this node (upstream link-table forwarding).</summary>
    private async ValueTask ForwardLinkTrafficAsync(ReceivedPacket received, byte hops, CancellationToken cancellationToken)
    {
        var packet = received.Packet;
        IPacketInterface? outbound = null;
        lock (_gate)
        {
            if (!_linkTable.TryGetValue(Convert.ToHexStringLower(packet.DestinationHash.Span), out var entry) || !entry.Validated) return;
            if (ReferenceEquals(entry.NextHopInterface, entry.ReceivedInterface))
            {
                if (hops == entry.RemainingHops || hops == entry.TakenHops) outbound = entry.NextHopInterface;
            }
            else if (ReferenceEquals(received.Source, entry.NextHopInterface))
            {
                if (hops == entry.RemainingHops) outbound = entry.ReceivedInterface;
            }
            else if (ReferenceEquals(received.Source, entry.ReceivedInterface))
            {
                if (hops == entry.TakenHops) outbound = entry.NextHopInterface;
            }

            if (outbound is null) return;
            entry.Timestamp = _time.GetUtcNow();
        }

        if (!_manager.Contains(outbound)) return;
        var raw = received.Raw.ToArray();
        raw[1] = hops;
        RememberHash(packet);
        await _manager.SendRawToAsync(raw, outbound, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates a link-request proof against the destination's announced identity and relays it towards the initiator.</summary>
    private async ValueTask RelayLinkProofAsync(ReceivedPacket received, byte hops, CancellationToken cancellationToken)
    {
        var packet = received.Packet;
        LinkEntry? entry;
        lock (_gate) _linkTable.TryGetValue(Convert.ToHexStringLower(packet.DestinationHash.Span), out entry);
        if (entry is null) return;
        if (!ReferenceEquals(received.Source, entry.NextHopInterface))
        {
            Log?.Invoke("Link request proof received on wrong interface, not transporting it");
            return;
        }

        var data = packet.Payload.Span;
        if ((data.Length != 96 && data.Length != 96 + Link.SignallingSize) || Engine.Known.Recall(entry.DestinationHash) is not { } peer)
        {
            return;
        }

        var signalling = data.Length == 99 ? data[96..] : [];
        byte[] signed = [.. packet.DestinationHash.Span, .. data[64..96], .. peer.ExportPublicKey()[32..], .. signalling];
        if (!Crypto.Ed25519.Verify(peer.ExportPublicKey()[32..], signed, data[..64]))
        {
            Log?.Invoke($"Invalid link request proof in transport for link {Convert.ToHexStringLower(packet.DestinationHash.Span)}, dropping proof");
            return;
        }

        lock (_gate)
        {
            if (hops != entry.RemainingHops)
            {
                if (entry.Validated) return;
                entry.RemainingHops = hops;
            }

            entry.Validated = true;
        }

        if (!_manager.Contains(entry.ReceivedInterface)) return;
        var raw = received.Raw.ToArray();
        raw[1] = hops;
        await _manager.SendRawToAsync(raw, entry.ReceivedInterface, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> RouteProofAsync(ReceivedPacket received, byte hops, CancellationToken cancellationToken)
    {
        if (!_options.TransportEnabled && !HasLocalClients) return false;
        var key = Convert.ToHexStringLower(received.Packet.DestinationHash.Span);
        ReverseEntry? entry;
        lock (_gate)
        {
            if (!_reverse.Remove(key, out entry)) return false;
        }

        if (!ReferenceEquals(received.Source, entry.Outbound))
        {
            Log?.Invoke("Proof received on the wrong interface, not transporting it");
            return true;
        }

        if (!_manager.Contains(entry.Received)) return true;
        var raw = received.Raw.ToArray();
        raw[1] = hops;
        await _manager.SendRawToAsync(raw, entry.Received, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask DeliverLocalAsync(ReceivedPacket received)
    {
        LocalDestination? local;
        lock (_gate) _locals.TryGetValue(Convert.ToHexStringLower(received.Packet.DestinationHash.Span), out local);
        if (local?.PacketReceived is { } handler && local.Destination.Type == received.Packet.DestinationType)
        {
            if (received.Packet.PacketType == PacketType.Data) await ProveIfRequiredAsync(local, received).ConfigureAwait(false);
            await handler(received).ConfigureAwait(false);
        }
    }

    private async ValueTask HandlePathRequestAsync(ReceivedPacket received, CancellationToken cancellationToken)
    {
        var data = received.Packet.Payload.ToArray();
        if (data.Length < Destination.HashLength) return;
        var destination = data[..Destination.HashLength].ToArray();
        byte[]? requestor = null;
        byte[] tag = [];
        if (data.Length > Destination.HashLength * 2)
        {
            requestor = data[Destination.HashLength..(Destination.HashLength * 2)].ToArray();
            tag = data[(Destination.HashLength * 2)..];
        }
        else if (data.Length > Destination.HashLength)
        {
            tag = data[Destination.HashLength..];
        }

        if (tag.Length == 0) return;
        if (tag.Length > Destination.HashLength) tag = tag[..Destination.HashLength];
        if (!RememberTag(Convert.ToHexStringLower(destination) + Convert.ToHexStringLower(tag))) return;

        var attached = received.Source;
        var mode = GetInterfaceMode(attached);
        var key = Convert.ToHexStringLower(destination);
        Log?.Invoke($"Path request for {key} on {Describe(attached)}");

        LocalDestination? local;
        lock (_gate) _locals.TryGetValue(key, out local);
        if (local is not null)
        {
            await AnnounceAsync(local, attached, pathResponse: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        var path = Engine.GetPath(destination);
        var fromLocalClient = IsLocalClientInterface(attached);
        var forLocalClient = path?.Interface is { } pathInterface && IsLocalClientInterface(pathInterface);
        if (!_options.TransportEnabled && !fromLocalClient && !forLocalClient && !HasLocalClients) return;
        if (path is not null)
        {
            if (!_options.TransportEnabled && !fromLocalClient && !forLocalClient) return;
            if (mode == InterfaceMode.Roaming && ReferenceEquals(path.Interface, attached)) return;
            if (requestor is not null && Engine.GetNextHop(destination) is { } nextHop && nextHop.AsSpan().SequenceEqual(requestor)) return;
            var delay = _options.PathRequestGrace + (mode == InterfaceMode.Roaming ? _options.RoamingGrace : TimeSpan.Zero);
            Engine.QueuePathResponse(destination, attached, delay);
            return;
        }

        var searchModes = fromLocalClient || HasLocalClients || mode is InterfaceMode.AccessPoint or InterfaceMode.Gateway or InterfaceMode.Roaming or InterfaceMode.Internal or InterfaceMode.Boundary;
        if (!searchModes) return;

        lock (_gate)
        {
            if (_discovery.TryGetValue(key, out var existing))
            {
                if (!existing.Interfaces.Contains(attached)) existing.Interfaces.Add(attached);
                return;
            }

            _discovery[key] = new DiscoveryEntry(_time.GetUtcNow() + _options.DiscoveryTimeout) { Interfaces = { attached } };
        }

        foreach (var other in _manager.Interfaces)
        {
            if (ReferenceEquals(other, attached)) continue;
            if (!_options.TransportEnabled && !fromLocalClient && !IsLocalClientInterface(other)) continue;
            if (mode == InterfaceMode.Boundary && GetInterfaceMode(other) is not (InterfaceMode.Boundary or InterfaceMode.Gateway)) continue;
            try { await RequestPathAsync(destination, other, tag, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log?.Invoke($"Could not forward path request on {Describe(other)}: {ex.Message}"); }
        }
    }

    private async Task AnnounceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.AnnounceInterval, _time);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) await FlushAnnouncesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One pass of upstream's announce job: transmits every announce and path response that is due.</summary>
    public async ValueTask FlushAnnouncesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var due in Engine.CollectDueRebroadcasts())
        {
            try
            {
                if (due.TargetInterface is { } target)
                {
                    if (_manager.Contains(target)) await _manager.SendToAsync(due.Packet, target, cancellationToken).ConfigureAwait(false);
                }
                else if (due.ExcludedInterface is { } excluded && _manager.Contains(excluded))
                {
                    await _manager.SendExceptAsync(due.Packet, excluded, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _manager.SendAsync(due.Packet, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log?.Invoke($"Could not transmit announce: {ex.Message}");
            }
        }
    }
    private async Task CullLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.CullInterval, _time);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) Cull();
    }

    /// <summary>One pass of upstream's table culling: expired or interface-less paths, stale reverse entries and discovery requests.</summary>
    public void Cull()
    {
        var removed = Engine.CullPaths(source =>
        {
            if (source is null) return _options.PathLifetime;
            if (!_manager.Contains(source)) return null;
            return GetInterfaceMode(source) switch
            {
                InterfaceMode.AccessPoint => _options.AccessPointPathLifetime,
                InterfaceMode.Roaming => _options.RoamingPathLifetime,
                _ => _options.PathLifetime,
            };
        });
        if (removed > 0) Log?.Invoke($"Removed {removed} path(s)");

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var key in _reverse.Where(kv => now - kv.Value.Created > _options.ReverseTimeout
                || !_manager.Contains(kv.Value.Received) || !_manager.Contains(kv.Value.Outbound)).Select(kv => kv.Key).ToArray())
            {
                _reverse.Remove(key);
            }

            foreach (var key in _discovery.Where(kv => now > kv.Value.Timeout).Select(kv => kv.Key).ToArray()) _discovery.Remove(key);

            // Validated links expire after 1.25 * the stale time without traffic; unvalidated ones when the proof is overdue.
            foreach (var key in _linkTable.Where(kv => (kv.Value.Validated
                ? now - kv.Value.Timestamp > TimeSpan.FromSeconds(Link.StaleFactor * Link.KeepaliveMax * 1.25)
                : now > kv.Value.ProofTimeout)
                    || !_manager.Contains(kv.Value.ReceivedInterface) || !_manager.Contains(kv.Value.NextHopInterface)).Select(kv => kv.Key).ToArray())
            {
                _linkTable.Remove(key);
            }
            foreach (var iface in _modes.Keys.Where(i => !_manager.Contains(i)).ToArray()) _modes.Remove(iface);
            _localClientInterfaces.RemoveWhere(iface => !_manager.Contains(iface));
        }

        ExpireReceipts();
    }

    private void TrimReverse()
    {
        var drop = _reverse.OrderBy(kv => kv.Value.Created).Take(Math.Max(1, _options.ReverseTableCapacity / 10)).Select(kv => kv.Key).ToArray();
        foreach (var key in drop) _reverse.Remove(key);
    }

    private static string Describe(IPacketInterface iface) => iface.GetType().Name;

    private sealed record ReverseEntry(IPacketInterface Received, IPacketInterface Outbound, DateTimeOffset Created);

    private sealed class LinkEntry(DateTimeOffset timestamp, byte[] nextHop, IPacketInterface nextHopInterface, int remainingHops,
        IPacketInterface receivedInterface, int takenHops, byte[] destinationHash, bool validated, DateTimeOffset proofTimeout)
    {
        public DateTimeOffset Timestamp { get; set; } = timestamp;
        public byte[] NextHop { get; } = nextHop;
        public IPacketInterface NextHopInterface { get; } = nextHopInterface;
        public int RemainingHops { get; set; } = remainingHops;
        public IPacketInterface ReceivedInterface { get; } = receivedInterface;
        public int TakenHops { get; } = takenHops;
        public byte[] DestinationHash { get; } = destinationHash;
        public bool Validated { get; set; } = validated;
        public DateTimeOffset ProofTimeout { get; } = proofTimeout;
    }

    private sealed class DiscoveryEntry(DateTimeOffset timeout)
    {
        public DateTimeOffset Timeout { get; } = timeout;
        public List<IPacketInterface> Interfaces { get; } = [];
    }
}
