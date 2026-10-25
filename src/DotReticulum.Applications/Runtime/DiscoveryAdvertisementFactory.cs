using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;

namespace DotReticulum.Applications.Runtime;

/// <summary>Maps native bearer settings onto the pinned discovery advertisement fields.</summary>
public static class DiscoveryAdvertisementFactory
{
    public static DiscoveryAdvertisement Create(RnsdInterfaceConfig configuration, Identity transportIdentity,
        bool transportEnabled, int? boundPort = null, string? i2pAddress = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(transportIdentity);
        if (!configuration.Enabled || !configuration.Discovery.Enabled)
            throw new InvalidOperationException($"Interface '{configuration.Name}' is not enabled for discovery advertisements.");
        ValidateBearer(configuration);
        var discovery = configuration.Discovery;
        var tcp = configuration.Type is RnsdInterfaceType.TcpServer or RnsdInterfaceType.Backbone;
        var port = tcp ? boundPort ?? configuration.GetInt("listen_port", 0, 0, 65535) : 0;
        if (tcp && port is < 1 or > 65535) throw new InvalidOperationException("Discovery advertising requires the actual bound TCP listening port.");
        if (tcp && string.IsNullOrWhiteSpace(discovery.ReachableOn))
            throw new InvalidOperationException($"Interface '{configuration.Name}' has no reachable_on discovery address.");
        if (configuration.Type == RnsdInterfaceType.I2p && string.IsNullOrWhiteSpace(i2pAddress))
            throw new InvalidOperationException($"Interface '{configuration.Name}' has no online connectable I2P address.");
        return new()
        {
            InterfaceType = configuration.Type switch
            {
                RnsdInterfaceType.Backbone => "BackboneInterface",
                RnsdInterfaceType.TcpServer => "TCPServerInterface",
                RnsdInterfaceType.I2p => "I2PInterface",
                RnsdInterfaceType.RNode => "RNodeInterface",
                RnsdInterfaceType.Weave => "WeaveInterface",
                _ => "KISSInterface",
            },
            Transport = transportEnabled,
            TransportIdentity = transportIdentity.Hash.ToArray(),
            Name = discovery.Name,
            ReachableOn = configuration.Type == RnsdInterfaceType.I2p ? i2pAddress! : discovery.ReachableOn ?? "",
            Port = port,
            Frequency = configuration.Type == RnsdInterfaceType.RNode ? configuration.GetInt("frequency", 0) : discovery.Frequency,
            Bandwidth = configuration.Type == RnsdInterfaceType.RNode ? configuration.GetInt("bandwidth", 0) : discovery.Bandwidth,
            SpreadingFactor = configuration.Type == RnsdInterfaceType.RNode ? configuration.GetInt("spreadingfactor", 0) : null,
            CodingRate = configuration.Type == RnsdInterfaceType.RNode ? configuration.GetInt("codingrate", 0) : null,
            Modulation = discovery.Modulation,
            Latitude = discovery.Latitude,
            Longitude = discovery.Longitude,
            Height = discovery.Height,
            NetworkName = configuration.IfacNetworkName,
            Passphrase = configuration.IfacPassphrase,
            PublishIfac = discovery.PublishIfac,
            OperatorAddress = discovery.LxmfAddress?.ToArray(),
            Encrypt = discovery.Encrypt,
            StampValue = discovery.StampValue,
        };
    }

    internal static void ValidateBearer(RnsdInterfaceConfig configuration)
    {
        var supported = configuration.Type switch
        {
            RnsdInterfaceType.TcpServer => true,
            RnsdInterfaceType.Backbone => configuration.Get("target_host") is null,
            RnsdInterfaceType.I2p => configuration.GetBool("connectable"),
            RnsdInterfaceType.RNode or RnsdInterfaceType.Weave or RnsdInterfaceType.Kiss => true,
            RnsdInterfaceType.TcpClient => configuration.GetBool("kiss_framing"),
            _ => false,
        };
        if (!supported)
            throw new NotSupportedException($"Discovery advertising for '{configuration.Name}' requires a listening TCP/Backbone, connectable I2P, RNode, Weave, KISS or KISS-framed TCP bearer.");
        if (configuration.Type is RnsdInterfaceType.Kiss or RnsdInterfaceType.TcpClient && configuration.Discovery.Modulation is not null)
            throw new NotSupportedException("Pinned upstream discovery cannot sanitize a numeric KISS discovery_modulation; omit this setting.");
    }
    public static void Register(DiscoveryAnnouncer announcer, RnsdInterfaceConfig configuration,
        Identity transportIdentity, bool transportEnabled, Func<int>? boundPort = null)
    {
        // Validate unsupported bearers/configuration immediately, not six hours into a runtime.
        _ = Create(configuration, transportIdentity, transportEnabled, boundPort?.Invoke());
        announcer.Register(configuration.Name, configuration.Discovery.AnnounceInterval,
            _ => ValueTask.FromResult(Create(configuration, transportIdentity, transportEnabled, boundPort?.Invoke())),
            configuration.Discovery.LocationCommand);
    }
}
