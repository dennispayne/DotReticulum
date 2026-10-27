using DotReticulum.Applications.Runtime;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rnsd;

public sealed partial class RnsdHost
{
    /// <summary>Local packet snapshot with missing values resolved from its authenticated shared owner, if any.</summary>
    public ValueTask<PacketPhysicalLayer> GetPacketPhysicalLayerAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _manager.ResolvePacketPhysicalLayerAsync(packet, cancellationToken);
    }

    private async ValueTask<PacketPhysicalLayer> QuerySharedPhysicalLayerAsync(byte[] hash, CancellationToken cancellationToken)
    {
        async ValueTask<double?> Query(string path)
        {
            var result = await _rpcClient!.CallAsync(new()
            {
                ["get"] = path, ["packet_hash"] = hash,
            }, cancellationToken).ConfigureAwait(false);
            return result is null ? null : MsgPack.ToDouble(result);
        }
        return new(await Query("packet_rssi").ConfigureAwait(false),
            await Query("packet_snr").ConfigureAwait(false), await Query("packet_q").ConfigureAwait(false));
    }

    public ValueTask<object?> CallManagementAsync(Dictionary<object, object?> call, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _rpcClient is { } client ? client.CallAsync(call, cancellationToken) : HandleManagementAsync(ManagementRequest.Parse(call), cancellationToken);
    }

    private object? HandleReadOnlyManagement(ManagementRequest request)
    {
        if (request.Operation != "get") throw new InvalidDataException("Remote management only permits read-only requests.");
        byte[] Hash(string key = "destination_hash") => (byte[])request.Arguments[key]!;
        return request.Path switch
        {
            "interface_stats" => GetStatistics(),
            "path_table" => new HostStateProvider(this).CaptureState().Paths
                .Where(p => request.Arguments.GetValueOrDefault("max_hops") is not long hops || p.Hops <= hops)
                .Select(p => (object?)new Dictionary<object, object?>
                {
                    ["hash"] = p.DestinationHash, ["timestamp"] = p.Timestamp, ["via"] = p.ReceivedFrom,
                    ["hops"] = p.Hops, ["expires"] = p.Expires,
                    ["interface"] = ResolveInterfaceName(p.InterfaceHash),
                }).ToArray(),
            "rate_table" => Router.Engine.GetRateTable().Select(rate => (object?)new Dictionary<object, object?>
            {
                ["hash"] = rate.Hash, ["last"] = rate.Last, ["rate_violations"] = rate.RateViolations,
                ["blocked_until"] = rate.BlockedUntil, ["timestamps"] = rate.Timestamps.Cast<object?>().ToArray(),
            }).ToArray(),
            "next_hop" => Router.Engine.GetNextHop(Hash()),
            "next_hop_if_name" => Router.Engine.GetPath(Hash())?.Interface is { } iface ? InterfaceName(iface) : "None",
            "first_hop_timeout" => Router.FirstHopTimeout(Hash()),
            "lowest_interface_bitrate" => Router.GetLowestInterfaceBitrate(),
            "medium_path_timeout" => Router.GetMediumPathTimeout(),
            "link_count" => Router.LinkTableCount,
            "active_link_count" => Router.ActiveLinkTableCount,
            "packet_rssi" => _manager.GetPacketPhysicalLayer(Hash("packet_hash")).Rssi,
            "packet_snr" => _manager.GetPacketPhysicalLayer(Hash("packet_hash")).Snr,
            "packet_q" => _manager.GetPacketPhysicalLayer(Hash("packet_hash")).Quality,
            "profiling_results" => null,
            "blackholed_identities" => _blackholes.PublicationValue(),
            "is_blackholed" => _blackholes.IsBlackholed(Hash("identity_hash")),
            _ => throw new InvalidDataException($"Unsupported management query '{request.Path}'."),
        };
    }

    private async ValueTask<object?> HandleManagementAsync(ManagementRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Operation == "get") return HandleReadOnlyManagement(request);
        byte[] Hash(string key = "destination_hash") => (byte[])request.Arguments[key]!;
        switch (request.Operation)
        {
            case "destination_data":
                return request.Path switch
                {
                    "used" => Router.Engine.Known.UseDestination(Hash()),
                    "retain" => Router.Engine.Known.RetainDestination(Hash()),
                    "unretain" => Router.Engine.Known.UnretainDestination(Hash()),
                    _ => throw new InvalidDataException("Unsupported destination-data operation."),
                };
            case "identity_data":
                return Router.Engine.Known.RetainIdentity(Hash("identity_hash"));
            case "blackhole_identity":
                return _blackholes.BlackholeIdentity(Hash("blackhole_identity"),
                    request.Arguments["until"] is { } until ? MsgPack.ToDouble(until) : null,
                    request.Arguments["reason"] as string);
            case "unblackhole_identity":
                return _blackholes.UnblackholeIdentity(Hash("unblackhole_identity"));
            case "manage":
                var name = (string)request.Arguments["name"]!;
                return request.Path switch
                {
                    "attach_interface" => await AttachInterfaceAsync(name, ReloadConfiguration(), cancellationToken: cancellationToken).ConfigureAwait(false),
                    "detach_interface" => await DetachInterfaceAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false),
                    "reload_interface" => await ReloadInterfaceAsync(name, ReloadConfiguration(), cancellationToken: cancellationToken).ConfigureAwait(false),
                    _ => throw new InvalidDataException("Unsupported interface management operation."),
                };
            case "drop" when request.Path == "path":
                return Router.ExpirePath(Hash());
            case "drop" when request.Path == "all_via":
                var destinations = new HostStateProvider(this).CaptureState().Paths
                    .Where(p => p.ReceivedFrom.AsSpan().SequenceEqual(Hash())).Select(p => p.DestinationHash).ToArray();
                return destinations.Count(destination => Router.ExpirePath(destination));
            case "drop" when request.Path == "announce_queues":
                _log(5, $"Discarded {Router.Engine.ClearPendingRebroadcasts()} pending announces.");
                return null;
            default:
                throw new InvalidDataException($"Unsupported management operation '{request.Operation}/{request.Path}'.");
        }
    }

    private RnsdConfig ReloadConfiguration()
    {
        var path = _configuration.SourcePath;
        return path is null ? _configuration : RnsdConfigParser.Parse(File.ReadAllText(path), _extensions) with { SourcePath = path };
    }

    private void RemoveBlackholedPaths()
    {
        foreach (var path in new HostStateProvider(this).CaptureState().Paths)
        {
            using var identity = Router.Engine.Known.Recall(path.DestinationHash, markUsed: false);
            if (identity is not null && _blackholes.IsBlackholed(identity.Hash)) Router.Engine.RemovePath(path.DestinationHash);
        }
        foreach (var link in Router.Links.Links)
            if (link.RemoteIdentity is { } remote && _blackholes.IsBlackholed(remote.Hash)) link.Teardown();
    }
}
