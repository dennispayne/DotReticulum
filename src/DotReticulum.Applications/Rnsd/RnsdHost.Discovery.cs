using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnsd;

public sealed partial class RnsdHost
{
    private void ConfigureAdvertisements()
    {
        var publishable = _configuration.Interfaces.Where(i => i.Enabled && i.Discovery.Enabled).ToArray();
        if (publishable.Length == 0) return;
        foreach (var settings in publishable)
        {
            if (settings.Type != RnsdInterfaceType.TcpServer)
                throw new NotSupportedException($"Interface '{settings.Name}': discovery publication is only supported for the native TCP server bearer.");
            if (settings.Discovery.Encrypt && _networkIdentity is null)
                throw new InvalidOperationException($"Interface '{settings.Name}': encrypted discovery requires network_identity.");
            if (settings.Discovery.LocationCommand is not null && !_executionPolicy.AllowDiscoveryCommands)
                throw new UnauthorizedAccessException($"Interface '{settings.Name}': location_cmd requires explicit --allow-discovery-commands authorization.");
        }
        var identity = _networkIdentity ?? Router.TransportIdentity;
        var destination = Destination.Create(DestinationType.Single, "rnstransport", ["discovery", "interface"], identity);
        Router.RegisterLocalDestination(new LocalDestination(identity, destination));
        _announcer = new DiscoveryAnnouncer(async (payload, token) =>
        {
            var local = new LocalDestination(identity, destination) { AppData = payload.ToArray() };
            Router.RegisterLocalDestination(local);
            await Router.AnnounceAsync(local, cancellationToken: token).ConfigureAwait(false);
        }, _networkIdentity, diagnostic: message => _log(1, message),
            metadataResolver: (advertisement, location, token) => DiscoveryMetadata.ResolveAsync(advertisement, location,
                token, _executionPolicy.DiscoveryCommandTimeout, _ => _executionPolicy.AllowDiscoveryCommands));
        foreach (var settings in publishable)
            _announcer.Register(settings.Name, settings.Discovery.AnnounceInterval, token => ReadAdvertisementAsync(settings, token),
                settings.Discovery.LocationCommand);
    }

    private ValueTask<DiscoveryAdvertisement> ReadAdvertisementAsync(RnsdInterfaceConfig settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IPacketInterface? iface;
        lock (_gate) iface = _interfaces.FirstOrDefault(i => i.Name == settings.Name).Interface;
        if (iface is not TcpServerPacketInterface server || server.LocalEndpoint is null)
            throw new InvalidOperationException($"Discoverable interface '{settings.Name}' is not online.");
        return ValueTask.FromResult(DiscoveryAdvertisementFactory.Create(settings, Router.TransportIdentity,
            Router.TransportEnabled, server.LocalEndpoint.Port) with
        {
            Version = typeof(RnsdHost).Assembly.GetName().Version?.ToString() ?? "unknown",
        });
    }

    private async ValueTask<DiscoveryConnection?> ConnectDiscoveredAsync(DiscoveryConnectRequest request, CancellationToken token)
    {
        var advertisement = request.Advertisement;
        if (advertisement.InterfaceType != "BackboneInterface")
            throw new NotSupportedException($"Discovery autoconnect for {advertisement.InterfaceType} has no native bearer.");
        if (advertisement.NetworkName is not null || advertisement.Passphrase is not null)
            throw new NotSupportedException("Discovered IFAC-protected interfaces require the Parity 4 authentication implementation; refusing an unauthenticated connection.");
        if (request.Gravity != 0 || request.AnnouncesToInternal is false)
            throw new NotSupportedException("The requested discovered-interface gravity/internal-announcement controls are not implemented.");
        var iface = new TcpClientPacketInterface(advertisement.ReachableOn ?? throw new InvalidDataException("Missing discovery endpoint."),
            advertisement.Port ?? throw new InvalidDataException("Missing discovery port."), request.ConfiguredBitrate);
        var mode = request.Mode is null ? InterfaceMode.Full : ParseDiscoveryMode(request.Mode);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { await AttachDiscoveredInterfaceAsync(request.Name, iface, mode, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException($"Discovery connection to {advertisement.ReachableOn}:{advertisement.Port} exceeded the reference five-second initial connection timeout."); }
        return CreateDiscoveryLease(request.Name, iface);
    }

    private static InterfaceMode ParseDiscoveryMode(string mode) => mode switch
    {
        "full" => InterfaceMode.Full,
        "pointtopoint" or "point_to_point" or "ptp" => InterfaceMode.PointToPoint,
        "access_point" or "accesspoint" or "ap" => InterfaceMode.AccessPoint,
        "roaming" => InterfaceMode.Roaming,
        "boundary" => InterfaceMode.Boundary,
        "gateway" or "gw" => InterfaceMode.Gateway,
        "internal" => InterfaceMode.Internal,
        _ => throw new FormatException("Invalid discovery interface mode."),
    };

    private DiscoveryConnection CreateDiscoveryLease(string name, IPacketInterface iface) => new(name,
        () => (iface as IPacketInterfaceDiagnostics)?.GetDiagnostics().GetValueOrDefault("status") is true,
        async () => { await DetachInterfaceAsync(name, internalForced: true).ConfigureAwait(false); },
        () => _manager.Contains(iface));

    private async ValueTask<IReadOnlyList<DiscoveryConnection>> AttachBootstrapAsync(CancellationToken token)
    {
        var result = new List<DiscoveryConnection>();
        foreach (var settings in _configuration.Interfaces.Where(i => i.Enabled && i.BootstrapOnly))
        {
            IPacketInterface? iface;
            lock (_gate) iface = _interfaces.FirstOrDefault(i => i.Name == settings.Name).Interface;
            if (iface is null)
            {
                await AttachInterfaceAsync(settings.Name, internalForced: true, cancellationToken: token).ConfigureAwait(false);
                lock (_gate) iface = _interfaces.Single(i => i.Name == settings.Name).Interface;
            }
            result.Add(CreateDiscoveryLease(settings.Name, iface));
        }
        return result;
    }

    private async ValueTask AttachDiscoveredInterfaceAsync(string name, IPacketInterface iface, InterfaceMode mode, CancellationToken token)
    {
        await _mutation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_gate)
                if (_interfaces.Any(i => i.Name == name)) throw new InvalidOperationException($"Interface '{name}' is already attached.");
            ConfigureMode(iface, mode);
            await _manager.AttachAsync(iface, token, isolateFailure: true).ConfigureAwait(false);
            lock (_gate) _interfaces.Add((name, iface));
        }
        catch
        {
            RemoveModeHandler(iface);
            await iface.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { _mutation.Release(); }
    }
}
