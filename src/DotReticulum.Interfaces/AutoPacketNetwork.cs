using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DotReticulum.Interfaces;

internal sealed record AutoPacketDevice(string Name, int Index, IPAddress Address, string? Id = null,
    IReadOnlyList<IPAddress>? LocalAddresses = null);
internal readonly record struct AutoPacketDatagram(IPAddress Source, ReadOnlyMemory<byte> Data);

internal interface IAutoPacketDevice : IAsyncDisposable
{
    ValueTask<AutoPacketDatagram> ReceiveAsync(bool discovery, bool multicast, CancellationToken cancellationToken);
    ValueTask SendAsync(ReadOnlyMemory<byte> data, IPAddress target, int port, CancellationToken cancellationToken);
}

internal interface IAutoPacketNetwork
{
    IReadOnlyList<AutoPacketDevice> GetDevices();
    IAutoPacketDevice Open(AutoPacketDevice device, AutoPacketInterfaceOptions options);
}

internal sealed class AutoPacketNetwork : IAutoPacketNetwork
{
    public IReadOnlyList<AutoPacketDevice> GetDevices()
    {
        var result = new List<AutoPacketDevice>();
        foreach (var device in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (!device.Supports(NetworkInterfaceComponent.IPv6)) continue;
                var properties = device.GetIPProperties();
                var index = properties.GetIPv6Properties()?.Index;
                if (index is null or <= 0) continue;
                var addresses = properties.UnicastAddresses.Where(a => a.Address.IsIPv6LinkLocal)
                    .Select(a => NormalizeLinkLocal(a.Address)).ToArray();
                if (addresses.Length > 0) result.Add(new(device.Name, index.Value, addresses[^1], device.Id, addresses));
            }
            catch (Exception error) when (error is NetworkInformationException or NotSupportedException)
            {
                System.Diagnostics.Trace.TraceError("AutoInterface cannot inspect device {0}: {1}", device.Name, error);
            }
        }
        return result;
    }

    internal static IPAddress Unscope(IPAddress address) => new(address.GetAddressBytes());

    internal static IPAddress NormalizeLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        // BSD embeds an interface scope in the second word of fe80:<scope>:: addresses.
        if (bytes.Length == 16 && bytes[0] == 0xfe && bytes[1] == 0x80 && bytes.AsSpan(4, 4).SequenceEqual(new byte[4]))
            bytes.AsSpan(2, 2).Clear();
        return new IPAddress(bytes);
    }

    internal static bool IsAllowed(string name, AutoPacketInterfaceOptions options, bool darwin, bool android)
    {
        var explicitlyAllowed = options.Devices.Contains(name, StringComparer.Ordinal);
        if (name == "lo0" || options.IgnoredDevices.Contains(name, StringComparer.Ordinal)) return false;
        if (darwin && new[] { "awdl0", "llw0", "en5" }.Contains(name) && !explicitlyAllowed) return false;
        if (android && (name is "dummy0" or "lo" or "tun0" ||
            new[] { "rmnet0", "rmnet1", "rmnet2", "rmnet3", "rmnet4", "rmnet5", "rmnet6", "rmnet7" }.Contains(name)) && !explicitlyAllowed) return false;
        return options.Devices.Count == 0 || explicitlyAllowed;
    }

    internal static bool IsAllowed(AutoPacketDevice device, AutoPacketInterfaceOptions options, bool darwin, bool android)
    {
        if (device.Id is null) return IsAllowed(device.Name, options, darwin, android);
        if (options.IgnoredDevices.Contains(device.Id, StringComparer.Ordinal)) return false;
        if (options.Devices.Contains(device.Id, StringComparer.Ordinal))
            options = options with { Devices = [.. options.Devices, device.Name] };
        return IsAllowed(device.Name, options, darwin, android);
    }

    public IAutoPacketDevice Open(AutoPacketDevice device, AutoPacketInterfaceOptions options) => new Device(device, options);

    private sealed class Device : IAutoPacketDevice
    {
        private readonly Socket _multicast;
        private readonly Socket _unicast;
        private readonly Socket _data;
        private readonly int _index;

        public Device(AutoPacketDevice device, AutoPacketInterfaceOptions options)
        {
            _index = device.Index;
            Socket? multicast = null, unicast = null, data = null;
            try
            {
                _multicast = multicast = CreateSocket(reuse: true);
                _unicast = unicast = CreateSocket(reuse: true);
                _data = data = CreateSocket(reuse: false);
                var local = new IPAddress(device.Address.GetAddressBytes(), device.Index);
                var group = options.GetMulticastAddress();
                _multicast.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, device.Index);
                _multicast.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(group, device.Index));
                _multicast.MulticastLoopback = true;
                _multicast.Bind(new IPEndPoint(OperatingSystem.IsWindows() ? IPAddress.IPv6Any :
                    new IPAddress(group.GetAddressBytes(), options.Scope == 2 ? device.Index : 0), options.DiscoveryPort));
                _unicast.Bind(new IPEndPoint(OperatingSystem.IsWindows() ? IPAddress.IPv6Any : local, options.DiscoveryPort + 1));
                _data.Bind(new IPEndPoint(local, options.DataPort));
                _data.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, device.Index);
                _data.MulticastLoopback = true;
            }
            catch
            {
                multicast?.Dispose();
                unicast?.Dispose();
                data?.Dispose();
                throw;
            }
        }

        private static Socket CreateSocket(bool reuse)
        {
            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.DualMode = false;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, reuse);
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }

        public async ValueTask<AutoPacketDatagram> ReceiveAsync(bool discovery, bool multicast, CancellationToken cancellationToken)
        {
            var socket = discovery ? multicast ? _multicast : _unicast : _data;
            // One extra byte distinguishes oversized frames on platforms that truncate a UDP receive.
            var buffer = new byte[discovery ? 1024 : AutoPacketInterface.MaximumFrameSize + 1];
            var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                new IPEndPoint(IPAddress.IPv6Any, 0), cancellationToken).ConfigureAwait(false);
            return new(Unscope(((IPEndPoint)result.RemoteEndPoint).Address), buffer.AsMemory(0, result.ReceivedBytes));
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> data, IPAddress target, int port, CancellationToken cancellationToken)
        {
            var address = new IPAddress(target.GetAddressBytes(), _index);
            var sent = await _data.SendToAsync(data, SocketFlags.None, new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            if (sent != data.Length) throw new IOException($"Incomplete AutoInterface datagram: {sent} of {data.Length} bytes.");
        }

        public ValueTask DisposeAsync()
        {
            _multicast.Dispose();
            _unicast.Dispose();
            _data.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
