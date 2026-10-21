using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;

namespace DotReticulum.Applications.Runtime;

/// <summary>Maps only supported native listening bearers; a type label never implies an unimplemented interface.</summary>
public static class DiscoveryAdvertisementFactory
{
    public static DiscoveryAdvertisement Create(RnsdInterfaceConfig configuration, Identity transportIdentity,
        bool transportEnabled, int? boundPort = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(transportIdentity);
        if (!configuration.Enabled || !configuration.Discovery.Enabled)
            throw new InvalidOperationException($"Interface '{configuration.Name}' is not enabled for discovery advertisements.");
        if (configuration.Type != RnsdInterfaceType.TcpServer)
            throw new NotSupportedException($"Discovery advertising for {configuration.TypeName} is unsupported; only a native TCPServerInterface can be advertised.");
        var discovery = configuration.Discovery;
        var port = boundPort ?? configuration.GetInt("listen_port", 0, 0, 65535);
        if (port is < 1 or > 65535) throw new InvalidOperationException("Discovery advertising requires the actual bound TCP listening port.");
        if (string.IsNullOrWhiteSpace(discovery.ReachableOn))
            throw new InvalidOperationException($"Interface '{configuration.Name}' has no reachable_on discovery address.");
        return new()
        {
            InterfaceType = "TCPServerInterface",
            Transport = transportEnabled,
            TransportIdentity = transportIdentity.Hash.ToArray(),
            Name = discovery.Name,
            ReachableOn = discovery.ReachableOn,
            Port = port,
            Latitude = discovery.Latitude,
            Longitude = discovery.Longitude,
            Height = discovery.Height,
            NetworkName = configuration.Get("network_name"),
            Passphrase = configuration.Get("passphrase"),
            PublishIfac = discovery.PublishIfac,
            OperatorAddress = discovery.LxmfAddress?.ToArray(),
            Encrypt = discovery.Encrypt,
            StampValue = discovery.StampValue,
        };
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
