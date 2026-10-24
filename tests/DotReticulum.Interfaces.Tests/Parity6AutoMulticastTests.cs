using System.Net;
using System.Net.Sockets;

namespace DotReticulum.Interfaces.Tests;

/// <summary>Actual OS IPv6 multicast, distinct from injected-network vectors and from live Python interop.</summary>
public sealed class Parity6AutoMulticastTests
{
    [AutoMulticastFact]
    public async Task RealIpv6MulticastReceivesOwnDiscoveryEcho()
    {
        var device = new AutoPacketNetwork().GetDevices().First(d =>
            AutoPacketNetwork.IsAllowed(d, new(), OperatingSystem.IsMacOS(), OperatingSystem.IsAndroid()));
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var discoveryPort = FreePortPair();
        var dataPort = FreePort();
        while (dataPort == discoveryPort || dataPort == discoveryPort + 1) dataPort = FreePort();
        await using var auto = new AutoPacketInterface(new()
        {
            GroupId = "dotreticulum-auto-multicast-test-" + Guid.NewGuid().ToString("N"),
            DiscoveryPort = discoveryPort,
            DataPort = dataPort,
            Devices = [device.Name],
            Diagnostic = errors.Enqueue,
        });
        await auto.StartAsync(default);
        Assert.Equal(1, auto.GetDiagnostics()["devices"]);
        try { await TestSupport.WaitFor(() => Equals(auto.GetDiagnostics()["multicast_echo_devices"], 1)); }
        catch (Exception error) { throw new InvalidOperationException("IPv6 multicast echo failed: " + string.Join("; ", errors.Select(e => e.Message)), error); }
        Assert.Equal(0, auto.PeerCount);
    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static int FreePortPair()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var port = FreePort();
            if (port == 65535) continue;
            try
            {
                using var first = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                using var second = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                first.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                second.Bind(new IPEndPoint(IPAddress.IPv6Any, port + 1));
                return port;
            }
            catch (SocketException) { }
        }
        throw new IOException("Could not reserve a free AutoInterface discovery port pair.");
    }
}

public sealed class AutoMulticastFactAttribute : FactAttribute
{
    public AutoMulticastFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_AUTO_MULTICAST") != "1")
        {
            Skip = "Actual IPv6 multicast is opt-in: set DOTRETICULUM_AUTO_MULTICAST=1; vectors and injected-network tests do not establish this capability.";
            return;
        }
        if (!Socket.OSSupportsIPv6) { Skip = "The OS does not support IPv6 sockets."; return; }
        try
        {
            var options = new AutoPacketInterfaceOptions();
            var device = new AutoPacketNetwork().GetDevices().FirstOrDefault(d =>
                AutoPacketNetwork.IsAllowed(d, options, OperatingSystem.IsMacOS(), OperatingSystem.IsAndroid()));
            if (device is null) { Skip = "No allowed device has an IPv6 link-local address and interface index."; return; }
            using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, device.Index);
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                new IPv6MulticastOption(options.GetMulticastAddress(), device.Index));
            socket.Bind(new IPEndPoint(new IPAddress(device.Address.GetAddressBytes(), device.Index), 0));
            socket.SendTo(options.GetDiscoveryToken(device.Address),
                new IPEndPoint(new IPAddress(options.GetMulticastAddress().GetAddressBytes(), device.Index), options.DiscoveryPort));
        }
        catch (Exception error) when (error is SocketException or PlatformNotSupportedException or System.Net.NetworkInformation.NetworkInformationException)
        {
            Skip = $"OS/device IPv6 multicast capability unavailable: {error.GetType().Name}: {error.Message}";
        }
    }
}
