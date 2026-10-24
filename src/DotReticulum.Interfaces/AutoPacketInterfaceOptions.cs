using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DotReticulum.Interfaces;

/// <summary>Configuration for IPv6 automatic peer discovery, corresponding to upstream AutoInterface.</summary>
public sealed record AutoPacketInterfaceOptions
{
    public string GroupId { get; init; } = "reticulum";
    public string DiscoveryScope { get; init; } = "link";
    public string MulticastAddressType { get; init; } = "temporary";
    public int DiscoveryPort { get; init; } = 29716;
    public int DataPort { get; init; } = 42671;
    public IReadOnlyList<string> Devices { get; init; } = [];
    public IReadOnlyList<string> IgnoredDevices { get; init; } = [];
    public long ConfiguredBitrate { get; init; } = 10_000_000;
    public int MaximumPeers { get; init; } = 128;
    public int ReceiveQueueCapacity { get; init; } = 32;
    public Action<Exception>? Diagnostic { get; init; }

    internal AutoPacketInterfaceOptions ValidateAndCopy()
    {
        ArgumentNullException.ThrowIfNull(GroupId);
        ArgumentNullException.ThrowIfNull(Devices);
        ArgumentNullException.ThrowIfNull(IgnoredDevices);
        ArgumentOutOfRangeException.ThrowIfLessThan(DiscoveryPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DiscoveryPort, 65534);
        ArgumentOutOfRangeException.ThrowIfLessThan(DataPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DataPort, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(ConfiguredBitrate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumPeers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumPeers, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReceiveQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReceiveQueueCapacity, 4096);
        _ = Scope;
        return this with { Devices = Devices.ToArray(), IgnoredDevices = IgnoredDevices.ToArray() };
    }

    internal int Scope => DiscoveryScope?.ToLowerInvariant() switch
    {
        "link" => 2,
        "admin" => 4,
        "site" => 5,
        "organisation" => 8,
        "global" => 14,
        _ => throw new ArgumentException("AutoInterface discovery scope must be link, admin, site, organisation or global.", nameof(DiscoveryScope)),
    };

    /// <summary>The upstream group-derived IPv6 multicast destination, without an interface scope ID.</summary>
    public IPAddress GetMulticastAddress()
    {
        ArgumentNullException.ThrowIfNull(GroupId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(GroupId));
        var address = new byte[16];
        address[0] = 0xff;
        address[1] = (byte)((string.Equals(MulticastAddressType, "permanent", StringComparison.OrdinalIgnoreCase) ? 0 : 0x10) | Scope);
        hash.AsSpan(2, 12).CopyTo(address.AsSpan(4));
        return new IPAddress(address);
    }

    /// <summary>Builds the upstream discovery token; this group discriminator is not peer authentication.</summary>
    public byte[] GetDiscoveryToken(IPAddress sourceAddress)
    {
        ArgumentNullException.ThrowIfNull(GroupId);
        ArgumentNullException.ThrowIfNull(sourceAddress);
        if (sourceAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            throw new ArgumentException("AutoInterface requires an IPv6 source address.", nameof(sourceAddress));
        return SHA256.HashData(Encoding.UTF8.GetBytes(GroupId + new IPAddress(sourceAddress.GetAddressBytes())));
    }
}
