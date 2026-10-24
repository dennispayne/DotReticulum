using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DotReticulum.Interfaces;

/// <summary>Server address selection matching the pinned TCPServerInterface family preference.</summary>
public static class TcpAddressResolver
{
    public static async ValueTask<IPEndPoint> ResolveListenEndpointAsync(string? listenHost, int port,
        string? device = null, bool preferIpv6 = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        cancellationToken.ThrowIfCancellationRequested();
        IPAddress[] addresses;
        if (!string.IsNullOrWhiteSpace(device))
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(
                candidate => candidate.Name == device || candidate.Id == device)
                ?? throw new ArgumentException($"TCP network device '{device}' was not found.", nameof(device));
            addresses = nic.GetIPProperties().UnicastAddresses.Select(entry => entry.Address).ToArray();
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(listenHost);
            addresses = IPAddress.TryParse(listenHost, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(listenHost, cancellationToken).ConfigureAwait(false);
        }
        addresses = addresses.Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).ToArray();
        if (addresses.Length == 0) throw new SocketException((int)SocketError.AddressFamilyNotSupported);
        var family = preferIpv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        return new IPEndPoint(addresses.FirstOrDefault(address => address.AddressFamily == family) ?? addresses[0], port);
    }
}
