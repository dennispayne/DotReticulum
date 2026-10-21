using DotReticulum.Core;

namespace DotReticulum.Transport;

public sealed partial class TransportRouter
{
    private readonly HashSet<IPacketInterface> _localClientInterfaces = [];
    private volatile bool _isSharedClient;

    public bool IsSharedClient => _isSharedClient;

    /// <summary>Upstream shared_connection_disappeared: settle all links and invalidate owner-dependent routing without stopping the bearer.</summary>
    public void SharedConnectionDisappeared()
    {
        if (!IsSharedClient) return;
        foreach (var link in Links.Links) link.Teardown();
        DropAllPaths();
        Engine.ClearPendingRebroadcasts();
        lock (_gate)
        {
            _reverse.Clear();
            _linkTable.Clear();
            _discovery.Clear();
        }
    }

    /// <summary>Upstream shared_connection_reappeared: reannounce registered SINGLE destinations as path responses.</summary>
    public async ValueTask SharedConnectionReappearedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSharedClient) return;
        LocalDestination[] destinations;
        lock (_gate) destinations = _locals.Values.Where(local => local.Destination.Type == DestinationType.Single).ToArray();
        foreach (var destination in destinations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AnnounceAsync(destination, pathResponse: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sets the shared-instance role before starting the dispatcher. Shared bearers do not add a network hop.</summary>
    public void ConfigureSharedClient(bool isSharedClient)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runCancellation is not null) throw new InvalidOperationException("Configure the shared role before starting the dispatcher.");
            _isSharedClient = isSharedClient;
        }
    }

    public void RegisterLocalClientInterface(IPacketInterface iface) => SetLocalClientInterface(iface, true);

    /// <summary>Classifies an owner's adopted shared-server child. Unregistering also removes its stale paths and links.</summary>
    public void SetLocalClientInterface(IPacketInterface iface, bool isLocalClient)
    {
        ArgumentNullException.ThrowIfNull(iface);
        if (!isLocalClient)
        {
            DetachInterface(iface);
            return;
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _localClientInterfaces.Add(iface);
            _modes[iface] = InterfaceMode.Internal;
        }
    }

    public bool IsLocalClientInterface(IPacketInterface iface)
    {
        lock (_gate) return _localClientInterfaces.Contains(iface);
    }

    private bool HasLocalClients
    {
        get { lock (_gate) return _localClientInterfaces.Count != 0; }
    }

    internal int InboundHopIncrement(IPacketInterface iface) => IsSharedClient || IsLocalClientInterface(iface) ? 0 : 1;

    private async ValueTask RelaySharedAnnounceAsync(ReceivedPacket received, CancellationToken cancellationToken)
    {
        if (IsSharedClient) return;
        var packet = received.Packet;
        var fromLocal = IsLocalClientInterface(received.Source);
        var hops = packet.Hops + InboundHopIncrement(received.Source);
        if (hops >= Packet.HopLimit) return;
        var outgoing = Packet.Create(PacketType.Announce, DestinationType.Single, packet.DestinationHash, packet.Payload,
            0, HeaderType.Header2, TransportId, TransportType.Transport, packet.ContextFlag, (byte)hops, packet.ReservedBit);
        foreach (var iface in _manager.Interfaces)
        {
            if (ReferenceEquals(iface, received.Source)) continue;
            if (!IsLocalClientInterface(iface) && (!fromLocal || packet.Context == Announce.PathResponseContext)) continue;
            await _manager.SendToAsync(outgoing, iface, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RelaySharedPlainAsync(ReceivedPacket received, CancellationToken cancellationToken)
    {
        if (IsSharedClient) return;
        var fromLocal = IsLocalClientInterface(received.Source);
        foreach (var iface in _manager.Interfaces)
        {
            if (ReferenceEquals(iface, received.Source) || (!fromLocal && !IsLocalClientInterface(iface))) continue;
            await _manager.SendToAsync(received.Packet, iface, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RelayUnknownLocalPacketAsync(ReceivedPacket received, CancellationToken cancellationToken)
    {
        foreach (var iface in _manager.Interfaces)
        {
            if (ReferenceEquals(iface, received.Source)) continue;
            await _manager.SendToAsync(received.Packet, iface, cancellationToken).ConfigureAwait(false);
        }
    }
}
