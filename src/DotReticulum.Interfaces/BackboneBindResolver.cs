using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DotReticulum.Interfaces;

internal static class BackboneBindResolver
{
    public static async ValueTask<IPEndPoint> ResolveAsync(IPEndPoint? literalEndpoint, string? host, int port,
        BackbonePacketInterfaceOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (options.Device is { } device)
        {
            var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(candidate => candidate.Name == device)
                ?? throw new IOException($"No kernel network interface named '{device}' is available for Backbone binding.");
            var properties = network.GetIPProperties();
            var address = SelectAddress(properties.UnicastAddresses.Select(unicast => unicast.Address).ToArray(), options.PreferIPv6);
            if (address.IsIPv6LinkLocal)
            {
                var index = properties.GetIPv6Properties()?.Index
                    ?? throw new IOException($"No IPv6 scope is available on Backbone device '{device}'.");
                address = new IPAddress(address.GetAddressBytes(), index);
            }
            token.ThrowIfCancellationRequested();
            return new(address, port);
        }
        if (literalEndpoint is not null) return new(literalEndpoint.Address, literalEndpoint.Port);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
        return new(SelectAddress(addresses, options.PreferIPv6), port);
    }

    internal static IPAddress SelectAddress(IReadOnlyList<IPAddress> addresses, bool preferIPv6)
    {
        var preferred = preferIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        return addresses.FirstOrDefault(address => address.AddressFamily == preferred) ??
            addresses.FirstOrDefault(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ??
            throw new IOException("No IPv4 or IPv6 address is available for Backbone binding.");
    }
}
