using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using DotReticulum.Core;
using static DotReticulum.Interfaces.Tests.TestSupport;

namespace DotReticulum.Interfaces.Tests;

// Independent constructor observations from RNS a29bfb51c468945c96a1ee22cae0114e993e2391.
// GenerateParity6UdpVectors.py executes the actual reference with ephemeral listeners.
[Trait("Category", "Integration")]
public sealed class Parity6UdpTests
{
    [Fact]
    public async Task DirectionAndPortPrecedenceMatchActualReferenceConstructor()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-udp-vectors.json")));
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", document.RootElement.GetProperty("revision").GetString());
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var settings = item.GetProperty("settings");
            string? Text(string key) => settings.TryGetProperty(key, out var value) ? value.GetString() : null;
            int? Number(string key) => settings.TryGetProperty(key, out var value) ? value.GetInt32() : null;
            await using var iface = new UdpPacketInterface(new UdpPacketInterfaceOptions
            {
                ListenAddress = Text("listen_ip"), ListenPort = Number("listen_port"),
                ForwardAddress = Text("forward_ip"), ForwardPort = Number("forward_port"), Port = Number("port"),
            });
            if (!item.GetProperty("receives").GetBoolean() && !item.GetProperty("forwards").GetBoolean())
            {
                await Assert.ThrowsAsync<ArgumentException>(async () => await iface.StartAsync(default));
                continue;
            }
            await iface.StartAsync(default);
            var diagnostics = iface.GetDiagnostics();
            Assert.Equal(item.GetProperty("receives").GetBoolean(), diagnostics["receives"]);
            Assert.Equal(item.GetProperty("forwards").GetBoolean(), diagnostics["forwards"]);
            Assert.Equal(item.GetProperty("hardwareMtu").GetInt32(), iface.HardwareMtu);
            Assert.Equal(item.GetProperty("bitrate").GetInt64(), iface.Bitrate);
            if (item.GetProperty("forwards").GetBoolean())
                Assert.Equal($"{item.GetProperty("forwardIp").GetString()}:{item.GetProperty("forwardPort").GetInt32()}",
                    diagnostics["forward_address"]);
            if (item.GetProperty("receives").GetBoolean())
                Assert.Equal(item.GetProperty("bindIp").GetString(), iface.LocalEndpoint!.Address.ToString());
        }
    }

    [Fact]
    public async Task ForwardOnlyAndReceiveOnlyExchangeWithoutBindingTheForwarder()
    {
        await using var receiver = new UdpPacketInterface(new UdpPacketInterfaceOptions
        {
            ListenAddress = "localhost", ListenPort = 0,
        });
        await receiver.StartAsync(default);
        Assert.Equal(AddressFamily.InterNetwork, receiver.LocalEndpoint!.AddressFamily);
        await using var sender = new UdpPacketInterface(new UdpPacketInterfaceOptions
        {
            ForwardAddress = "localhost", ForwardPort = receiver.LocalEndpoint.Port,
        });
        await sender.StartAsync(default);
        Assert.Null(sender.LocalEndpoint);
        await using var incoming = receiver.ReceiveAsync(default).GetAsyncEnumerator();
        await sender.SendAsync(MakePacket(37), default);
        Assert.Equal(37, TagOf(await Next(incoming)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await receiver.SendAsync(MakePacket(1), default));

        await using var offlineReceive = sender.ReceiveAsync(default).GetAsyncEnumerator();
        var pending = offlineReceive.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        await sender.DisposeAsync();
        Assert.False(await pending.WaitAsync(Patience));
    }

    [Fact]
    public async Task ForwardingUsesAnIndependentEphemeralSourcePort()
    {
        using var sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var iface = new UdpPacketInterface(new IPEndPoint(IPAddress.Loopback, 0),
            (IPEndPoint)sink.Client.LocalEndPoint!);
        await iface.StartAsync(default);
        await iface.SendAsync(MakePacket(1), default);
        var datagram = await sink.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(Patience);
        Assert.NotEqual(iface.LocalEndpoint!.Port, datagram.RemoteEndPoint.Port);
        Assert.Equal(MakePacket(1), datagram.Buffer);
    }

    [Theory]
    [InlineData(1065, false)]
    [InlineData(1129, true)]
    [InlineData(1193, true)]
    public async Task OversizedDatagramsAreDroppedWithoutDisablingReception(int length, bool accessCodeFlag)
    {
        await using var iface = new UdpPacketInterface(new UdpPacketInterfaceOptions
        {
            ListenAddress = "127.0.0.1", ListenPort = 0,
        });
        await iface.StartAsync(default);
        await using var incoming = iface.ReceiveAsync(default).GetAsyncEnumerator();
        using var socket = new UdpClient();
        var oversized = new byte[length];
        if (accessCodeFlag) oversized[0] = 0x80;
        await socket.SendAsync(oversized, iface.LocalEndpoint!);
        await socket.SendAsync(MakePacket(9), iface.LocalEndpoint!);
        Assert.Equal(9, TagOf(await Next(incoming)));
        Assert.Equal(1L, iface.GetDiagnostics()["oversized_datagrams"]);
    }

    [Fact]
    public async Task MissingDeviceAndWrongAddressFamilyAreExplicitFailures()
    {
        Assert.Throws<IOException>(() => UdpPacketInterface.GetDeviceBroadcastAddress("missing-" + Guid.NewGuid()));
        await using var iface = new UdpPacketInterface(new UdpPacketInterfaceOptions
        {
            ListenAddress = "::1", ListenPort = 0,
        });
        await Assert.ThrowsAsync<ArgumentException>(async () => await iface.StartAsync(default));
        Assert.False((bool)iface.GetDiagnostics()["status"]!);
    }

    [Fact]
    public void DeviceBroadcastResolutionUsesTheSelectedIpv4Mask()
    {
        var candidate = NetworkInterface.GetAllNetworkInterfaces()
            .Select(n => (Adapter: n, Address: n.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)))
            .First(pair => pair.Address is not null);
        var address = candidate.Address!.Address.GetAddressBytes();
        var mask = candidate.Address.IPv4Mask.GetAddressBytes();
        var broadcast = UdpPacketInterface.GetDeviceBroadcastAddress(candidate.Adapter.Name).GetAddressBytes();
        for (var i = 0; i < address.Length; i++) Assert.Equal((byte)(address[i] | ~mask[i]), broadcast[i]);
        Assert.Equal(new IPAddress(broadcast), UdpPacketInterface.GetDeviceBroadcastAddress(candidate.Adapter.Id));
    }

    [Fact]
    public async Task ForwardOnlyReceiveCancellationAndConcurrentDisposalAreBounded()
    {
        var iface = new UdpPacketInterface(new UdpPacketInterfaceOptions
        {
            ForwardAddress = "127.0.0.1", ForwardPort = 9,
        });
        await iface.StartAsync(default);
        using var cancelled = new CancellationTokenSource();
        await using var incoming = iface.ReceiveAsync(cancelled.Token).GetAsyncEnumerator();
        var pending = incoming.MoveNextAsync().AsTask();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => iface.DisposeAsync().AsTask())).WaitAsync(Patience);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await iface.SendAsync(MakePacket(1), default));
    }
}
