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
        foreach (var settings in publishable) ValidateAdvertisement(settings);
        foreach (var settings in publishable)
            RegisterAdvertisement(settings);
    }

    private void ValidateAdvertisement(RnsdInterfaceConfig settings)
    {
        if (!settings.Enabled || !settings.Discovery.Enabled) return;
        if (settings.Type is not (RnsdInterfaceType.TcpServer or RnsdInterfaceType.Backbone) || settings.Get("target_host") is not null)
            throw new NotSupportedException($"Interface '{settings.Name}': discovery publication requires a native listening TCP or Backbone bearer.");
        if (settings.Discovery.Encrypt && _networkIdentity is null)
            throw new InvalidOperationException($"Interface '{settings.Name}': encrypted discovery requires network_identity.");
        if (settings.Discovery.LocationCommand is not null && !_executionPolicy.AllowDiscoveryCommands)
            throw new UnauthorizedAccessException($"Interface '{settings.Name}': location_cmd requires explicit --allow-discovery-commands authorization.");
        if (settings.Discovery.AnnounceInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(settings), "Discovery advertisement interval must be positive.");
    }

    private void RegisterAdvertisement(RnsdInterfaceConfig settings)
    {
        if (!settings.Enabled || !settings.Discovery.Enabled) return;
        EnsureAnnouncer();
        _announcer!.Register(settings.Name, settings.Discovery.AnnounceInterval, token => ReadAdvertisementAsync(settings, token),
            settings.Discovery.LocationCommand);
    }

    private void EnsureAnnouncer()
    {
        if (_announcer is not null) return;
        var identity = _networkIdentity ?? Router.TransportIdentity;
        var destination = Destination.Create(DestinationType.Single, "rnstransport", ["discovery", "interface"], identity);
        var local = new LocalDestination(identity, destination);
        Router.RegisterLocalDestination(local);
        _announcer = new DiscoveryAnnouncer(async (payload, token) =>
        {
            await Router.AnnounceAsync(local, cancellationToken: token, appData: payload.ToArray()).ConfigureAwait(false);
        }, _networkIdentity, timeProvider: _timeProvider, diagnostic: message => _log(1, message),
            metadataResolver: (advertisement, location, token) => DiscoveryMetadata.ResolveAsync(advertisement, location,
                token, _executionPolicy.DiscoveryCommandTimeout, _ => _executionPolicy.AllowDiscoveryCommands));
    }

    internal ValueTask TickDiscoveryAdvertisementsAsync(CancellationToken cancellationToken = default) =>
        _announcer?.TickAsync(cancellationToken) ?? ValueTask.CompletedTask;

    private ValueTask<DiscoveryAdvertisement> ReadAdvertisementAsync(RnsdInterfaceConfig settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IPacketInterface? iface;
        lock (_gate) iface = _interfaces.FirstOrDefault(i => i.Name == settings.Name).Interface;
        var port = iface switch
        {
            TcpServerPacketInterface { LocalEndpoint: { } endpoint } => endpoint.Port,
            BackbonePacketInterface { LocalEndpoint: { } endpoint } => endpoint.Port,
            _ => (int?)null,
        };
        if (port is null)
            throw new InvalidOperationException($"Discoverable interface '{settings.Name}' is not online.");
        return ValueTask.FromResult(DiscoveryAdvertisementFactory.Create(settings, Router.TransportIdentity,
            Router.TransportEnabled, port) with
        {
            Version = typeof(RnsdHost).Assembly.GetName().Version?.ToString() ?? "unknown",
        });
    }

    private async ValueTask<DiscoveryConnection?> ConnectDiscoveredAsync(DiscoveryConnectRequest request, CancellationToken token)
    {
        var advertisement = request.Advertisement;
        if (advertisement.InterfaceType != "BackboneInterface")
            throw new NotSupportedException($"Discovery autoconnect for {advertisement.InterfaceType} has no native bearer.");
        var host = advertisement.ReachableOn ?? throw new InvalidDataException("Missing discovery endpoint.");
        var port = advertisement.Port ?? throw new InvalidDataException("Missing discovery port.");
        IPacketInterface iface = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()
            ? new BackboneClientPacketInterface(host, port, new BackbonePacketInterfaceOptions { NominalBitrate = request.ConfiguredBitrate })
            : new TcpClientPacketInterface(host, port, request.ConfiguredBitrate, reconnect: true);
        var accessCode = advertisement.NetworkName is null && advertisement.Passphrase is null ? null
            : InterfaceAccessCode.Derive(advertisement.NetworkName, advertisement.Passphrase, size: 16);
        var mode = request.Mode is null ? InterfaceMode.Full : ParseDiscoveryMode(request.Mode);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await AttachDiscoveredInterfaceAsync(request.Name, iface, mode, deadline.Token, accessCode,
                () => _configuration.Routing.CreateInterfaceOptions(Router.TransportEnabled,
                    request.Gravity, request.AnnouncesToInternal)).ConfigureAwait(false);
        }
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

    private async ValueTask AttachDiscoveredInterfaceAsync(string name, IPacketInterface iface, InterfaceMode mode, CancellationToken token,
        InterfaceAccessCode? accessCode = null, Func<InterfaceAnnounceOptions>? optionsFactory = null)
    {
        var acquired = false;
        try
        {
            await _mutation.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            lock (_gate)
                if (_interfaces.Any(i => i.Name == name)) throw new InvalidOperationException($"Interface '{name}' is already attached.");
            ConfigureMode(iface, mode, optionsFactory);
            await _manager.AttachAsync(iface, token, isolateFailure: true, accessCode: accessCode).ConfigureAwait(false);
            lock (_gate) _interfaces.Add((name, iface));
        }
        catch
        {
            RemoveModeHandler(iface);
            accessCode?.Dispose();
            await iface.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { if (acquired) _mutation.Release(); }
    }
}
