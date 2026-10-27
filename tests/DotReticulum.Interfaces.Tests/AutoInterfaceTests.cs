using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces.Tests;

// Reference: a29bfb51c468945c96a1ee22cae0114e993e2391; GenerateParity6AutoVectors.py
// executes upstream constructor, discovery handler and peer duplicate suppression.
[Trait("Category", "Integration")]
public sealed class AutoInterfaceTests
{
    private static readonly AutoPacketDevice Device = new("ethernet-test", 7, IPAddress.Parse("fe80::1"));
    private static readonly IPAddress Remote = IPAddress.Parse("fe80::2");
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-auto.json")));

    [Fact]
    public void ConstructorAndTokenVectorsMatchPinnedReference()
    {
        using var fixture = Vectors();
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", fixture.RootElement.GetProperty("revision").GetString());
        Assert.Equal("c92fe1da13d7633b4bd443660041c8471fd8762fc9f3535cf92926f9f323c9df",
            fixture.RootElement.GetProperty("auto_source_sha256").GetString());
        Assert.Equal("88efeadbe2e48e374910fa9b7ea747076cdf6ee6163bc95a098596096672aedf",
            fixture.RootElement.GetProperty("auto_source_canonical_lf_sha256").GetString());
        Assert.Equal("88efeadbe2e48e374910fa9b7ea747076cdf6ee6163bc95a098596096672aedf",
            fixture.RootElement.GetProperty("auto_git_blob_sha256").GetString());
        foreach (var vector in fixture.RootElement.GetProperty("constructors").EnumerateArray())
        {
            var options = new AutoPacketInterfaceOptions
            {
                GroupId = vector.GetProperty("group").GetString()!,
                DiscoveryScope = vector.GetProperty("scope").GetString()!,
                MulticastAddressType = vector.GetProperty("kind").GetString()!,
            };
            Assert.Equal(IPAddress.Parse(vector.GetProperty("multicast").GetString()!), options.GetMulticastAddress());
            Assert.Equal(vector.GetProperty("token").GetString(),
                Convert.ToHexString(options.GetDiscoveryToken(IPAddress.Parse(vector.GetProperty("source").GetString()!))).ToLowerInvariant());
        }
        Assert.Equal(1196, fixture.RootElement.GetProperty("constants").GetProperty("mtu").GetInt32());
        var defaults = new AutoPacketInterfaceOptions();
        Assert.Equal(29716, defaults.DiscoveryPort);
        Assert.Equal(42671, defaults.DataPort);
        Assert.Equal(10_000_000, defaults.ConfiguredBitrate);
    }

    [Fact]
    public async Task DiscoveryPrefixVectorsMatchPinnedHandler()
    {
        using var fixture = Vectors();
        foreach (var vector in fixture.RootElement.GetProperty("discovery").EnumerateArray())
        {
            var network = new Network();
            await using var auto = Create(network);
            await auto.StartAsync(default);
            auto.ProcessDiscovery(Device, IPAddress.Parse("fe80::1234:abcd"), Convert.FromHexString(vector.GetProperty("data").GetString()!));
            Assert.Equal(vector.GetProperty("accepted").GetBoolean() ? 1 : 0, auto.PeerCount);
        }
    }

    [Fact]
    public async Task DuplicateTimeBoundaryMatchesPinnedPeer()
    {
        using var fixture = Vectors();
        var clock = new Clock();
        await using var auto = Create(new Network(), clock: clock);
        IPacketInterface? child = null;
        auto.ChildConnected += peer => child = peer;
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        foreach (var vector in fixture.RootElement.GetProperty("duplicates").EnumerateArray())
        {
            clock.Now = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(vector.GetProperty("time").GetDouble());
            var before = (long)((IPacketInterfaceDiagnostics)child!).GetDiagnostics()["rxb"]!;
            auto.ProcessData(Remote, Convert.FromHexString(vector.GetProperty("frame").GetString()!));
            var after = (long)((IPacketInterfaceDiagnostics)child!).GetDiagnostics()["rxb"]!;
            Assert.Equal(vector.GetProperty("accepted").GetBoolean(), after > before);
        }
    }

    [Fact]
    public void ScopedAndBsdAddressesMatchReferenceDescopeHelper()
    {
        using var fixture = Vectors();
        foreach (var vector in fixture.RootElement.GetProperty("descope").EnumerateArray())
        {
            var source = vector.GetProperty("source").GetString()!.Split('%')[0];
            Assert.Equal(IPAddress.Parse(vector.GetProperty("result").GetString()!),
                AutoPacketNetwork.NormalizeLinkLocal(IPAddress.Parse(source)));
        }
    }

    [Fact]
    public async Task PeerJobTimeoutAndReverseBoundariesMatchReferenceExecution()
    {
        using var fixture = Vectors();
        foreach (var vector in fixture.RootElement.GetProperty("peer_jobs").EnumerateArray())
        {
            var clock = new Clock();
            var network = new Network();
            await using var auto = Create(network, clock: clock);
            await auto.StartAsync(default);
            Discover(auto, Device, Remote);
            clock.Now += TimeSpan.FromSeconds(vector.GetProperty("elapsed").GetDouble());
            await auto.RunMaintenanceAsync();
            Assert.Equal(vector.GetProperty("expired").GetBoolean(), auto.PeerCount == 0);
            Assert.Equal(vector.GetProperty("reverse").GetBoolean(),
                network.Opened.Single().Value.Sent.Any(send => send.Port == 29717));
        }
    }

    [Theory]
    [InlineData("lo0", false, false, false, false)]
    [InlineData("lo0", true, true, false, false)]
    [InlineData("awdl0", false, true, false, false)]
    [InlineData("awdl0", true, true, false, true)]
    [InlineData("en5", false, true, false, false)]
    [InlineData("llw0", false, true, false, false)]
    [InlineData("tun0", false, false, true, false)]
    [InlineData("rmnet7", true, false, true, true)]
    [InlineData("ethernet-test", false, false, false, true)]
    public void DeviceSelectionHonoursPlatformExceptions(string name, bool allowed, bool darwin, bool android, bool expected)
    {
        var options = new AutoPacketInterfaceOptions { Devices = allowed ? [name] : [] };
        Assert.Equal(expected, AutoPacketNetwork.IsAllowed(name, options, darwin, android));
        Assert.False(AutoPacketNetwork.IsAllowed(name, options with { IgnoredDevices = [name] }, darwin, android));
    }

    [Fact]
    public void ConfigurationBoundsAndScopeFailExplicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutoPacketInterface(new() { DiscoveryPort = 65535 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutoPacketInterface(new() { DiscoveryPort = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutoPacketInterface(new() { DataPort = 65536 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutoPacketInterface(new() { ConfiguredBitrate = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutoPacketInterface(new() { MaximumPeers = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutoPacketInterface(new() { ReceiveQueueCapacity = 0 }));
        Assert.Throws<ArgumentException>(() => new AutoPacketInterface(new() { DiscoveryScope = "unknown" }));
        Assert.Throws<ArgumentException>(() => new AutoPacketInterfaceOptions().GetDiscoveryToken(IPAddress.Loopback));
        Assert.Equal(IPAddress.Parse("fe80::abcd"), AutoPacketNetwork.Unscope(IPAddress.Parse("fe80::abcd%7")));
    }

    [Fact]
    public void DeviceSelectionAcceptsNativeNameOrStableWindowsIdentifier()
    {
        var device = Device with { Id = "{00000000-0000-0000-0000-000000000007}" };
        var options = new AutoPacketInterfaceOptions { Devices = [device.Id!] };
        Assert.True(AutoPacketNetwork.IsAllowed(device, options, false, false));
        Assert.False(AutoPacketNetwork.IsAllowed(device, options with { IgnoredDevices = [Device.Name] }, false, false));
        Assert.False(AutoPacketNetwork.IsAllowed(device, options with { IgnoredDevices = [device.Id!] }, false, false));
    }

    [Fact]
    public async Task DevicesAreCopiedAndExplicitWhitelistIsApplied()
    {
        var devices = new List<string> { Device.Name };
        var network = new Network { Devices = [Device, new("other", 8, IPAddress.Parse("fe80::8"))] };
        await using var auto = Create(network, options: new() { Devices = devices });
        devices.Clear();
        await auto.StartAsync(default);
        Assert.Single(network.Opened);
        Assert.Equal(Device.Name, network.Opened.Single().Key.Name);
    }

    [Fact]
    public async Task OwnEchoAndUnknownDataDoNotCreatePeersWithoutSubscribers()
    {
        var clock = new Clock();
        await using var auto = Create(new Network(), clock: clock);
        await auto.StartAsync(default);
        Discover(auto, Device, Device.Address);
        auto.ProcessData(Remote, new byte[] { 1, 2, 3 });
        Assert.Equal(0, auto.PeerCount);
        Discover(auto, Device, Remote);
        auto.ProcessData(Remote, new byte[] { 1, 2, 3 });
        Assert.Equal(1, auto.PeerCount);
        Assert.Equal(3L, auto.GetDiagnostics()["rxb"]);
        clock.Now += TimeSpan.FromSeconds(22);
        await auto.RunMaintenanceAsync();
        Assert.Equal(1, auto.PeerCount);
        clock.Now += TimeSpan.FromTicks(1);
        await auto.RunMaintenanceAsync();
        Assert.Equal(0, auto.PeerCount);
    }

    [Fact]
    public async Task UnselectedLocalAddressEchoDoesNotCreateASelfPeer()
    {
        var otherLocal = IPAddress.Parse("fe80::9");
        var device = Device with { LocalAddresses = [otherLocal, Device.Address] };
        await using var auto = Create(new Network { Devices = [device] });
        await auto.StartAsync(default);
        Discover(auto, device, otherLocal);
        Assert.Equal(0, auto.PeerCount);
        Assert.Equal(0, auto.GetDiagnostics()["multicast_echo_devices"]);
    }

    [Fact]
    public async Task ObserversAreIsolatedAndEachPeerHasIndependentIngress()
    {
        var errors = new ConcurrentQueue<Exception>();
        var network = new Network();
        await using var auto = Create(network, options: new() { Diagnostic = errors.Enqueue });
        var peers = new List<IPacketInterface>();
        auto.ChildConnected += _ => throw new InvalidOperationException("observer failure");
        auto.ChildConnected += peers.Add;
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        var second = IPAddress.Parse("fe80::3");
        Discover(auto, Device, second);
        Assert.Equal(2, peers.Count);
        Assert.NotSame(peers[0], peers[1]);
        await using var a = peers[0].ReceiveAsync(default).GetAsyncEnumerator();
        await using var b = peers[1].ReceiveAsync(default).GetAsyncEnumerator();
        auto.ProcessData(Remote, new byte[] { 1, 0x7e, 0x7d });
        auto.ProcessData(second, new byte[] { 2, 0x7e, 0x7d });
        Assert.Equal(new byte[] { 1, 0x7e, 0x7d }, (await TestSupport.Next(a)).ToArray());
        Assert.Equal(new byte[] { 2, 0x7e, 0x7d }, (await TestSupport.Next(b)).ToArray());
        await peers[1].SendAsync(new byte[] { 3, 0x7e, 0x7d }, default);
        Assert.Contains(network.Opened.Single().Value.Sent, send =>
            send.Address.Equals(second) && send.Port == 42671 && send.Data.SequenceEqual(new byte[] { 3, 0x7e, 0x7d }));
        Assert.Contains(errors, error => error.InnerException?.Message == "observer failure");
        await peers[0].DisposeAsync();
        Assert.Equal(1, auto.PeerCount);
        Assert.False(await a.MoveNextAsync());
        await peers[1].SendAsync(new byte[] { 9 }, default);
    }

    [Fact]
    public async Task PeerAndQueueLimitsAreBoundedAndFramesAreOwned()
    {
        var errors = new ConcurrentQueue<Exception>();
        await using var auto = Create(new Network(), options: new() { MaximumPeers = 1, ReceiveQueueCapacity = 1, Diagnostic = errors.Enqueue });
        IPacketInterface? child = null;
        auto.ChildConnected += peer => child = peer;
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        Discover(auto, Device, IPAddress.Parse("fe80::3"));
        Assert.Equal(1, auto.PeerCount);
        byte[] original = [1, 2, 3];
        auto.ProcessData(Remote, original);
        original[0] = 99;
        auto.ProcessData(Remote, new byte[] { 4, 5, 6 });
        await using var reader = child!.ReceiveAsync(default).GetAsyncEnumerator();
        Assert.Equal(new byte[] { 1, 2, 3 }, (await TestSupport.Next(reader)).ToArray());
        Assert.Contains(errors, error => error.Message.Contains("queue full", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("peer limit", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => child!.ReceiveAsync(default));
    }

    [Fact]
    public async Task FramingBoundsPermitOnlyFlaggedIfacOverhead()
    {
        var network = new Network();
        await using var auto = Create(network);
        IPacketInterface? child = null;
        auto.ChildConnected += peer => child = peer;
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        byte[] normal = new byte[1196];
        byte[] protectedFrame = new byte[1260];
        protectedFrame[0] = 0x80;
        await child!.SendAsync(normal, default);
        await child!.SendAsync(protectedFrame, default);
        await Assert.ThrowsAsync<ArgumentException>(() => child!.SendAsync(ReadOnlyMemory<byte>.Empty, default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => child!.SendAsync(new byte[1197], default).AsTask());
        var oversized = new byte[1261];
        oversized[0] = 0x80;
        await Assert.ThrowsAsync<ArgumentException>(() => child!.SendAsync(oversized, default).AsTask());
        auto.ProcessData(Remote, normal);
        auto.ProcessData(Remote, protectedFrame);
        auto.ProcessData(Remote, new byte[1197]);
        auto.ProcessData(Remote, ReadOnlyMemory<byte>.Empty);
        auto.ProcessData(Remote, oversized);
        Assert.Equal(2456L, auto.GetDiagnostics()["rxb"]);
        Assert.Equal(3L, auto.GetDiagnostics()["dropped_frames"]);
    }

    [Fact]
    public async Task DuplicateCacheIsSharedAndEvictedAtFortyEightEntries()
    {
        await using var auto = Create(new Network());
        var peers = new List<IPacketInterface>();
        auto.ChildConnected += peers.Add;
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        var second = IPAddress.Parse("fe80::3");
        Discover(auto, Device, second);
        auto.ProcessData(Remote, new byte[] { 0 });
        auto.ProcessData(second, new byte[] { 0 });
        Assert.Equal(0L, ((IPacketInterfaceDiagnostics)peers[1]).GetDiagnostics()["rxb"]);
        for (var i = 1; i <= 48; i++) auto.ProcessData(Remote, new byte[] { (byte)i });
        auto.ProcessData(second, new byte[] { 0 });
        Assert.Equal(1L, ((IPacketInterfaceDiagnostics)peers[1]).GetDiagnostics()["rxb"]);
    }

    [Fact]
    public async Task AcceptedDataRefreshesExpiryButDuplicatesDoNot()
    {
        var clock = new Clock();
        await using var auto = Create(new Network(), clock: clock);
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        clock.Now += TimeSpan.FromSeconds(21);
        auto.ProcessData(Remote, new byte[] { 1 });
        clock.Now += TimeSpan.FromSeconds(.5);
        auto.ProcessData(Remote, new byte[] { 1 });
        clock.Now += TimeSpan.FromSeconds(21.6);
        await auto.RunMaintenanceAsync();
        Assert.Equal(0, auto.PeerCount);
    }

    [Fact]
    public async Task PeerExpiryCompletesOnlyItsStreamAndRediscoveryCreatesAFreshChild()
    {
        var clock = new Clock();
        await using var auto = Create(new Network(), clock: clock);
        var peers = new List<IPacketInterface>();
        auto.ChildConnected += peers.Add;
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        await using var oldReader = peers[0].ReceiveAsync(default).GetAsyncEnumerator();
        var pending = oldReader.MoveNextAsync().AsTask();
        clock.Now += TimeSpan.FromSeconds(22) + TimeSpan.FromTicks(1);
        await auto.RunMaintenanceAsync();
        Assert.False(await pending.WaitAsync(TestSupport.Patience));
        Assert.True(auto.IsOnline);
        Discover(auto, Device, Remote);
        Assert.Equal(2, peers.Count);
        Assert.NotSame(peers[0], peers[1]);
        await peers[0].DisposeAsync();
        Assert.Equal(1, auto.PeerCount);
        await using var currentReader = peers[1].ReceiveAsync(default).GetAsyncEnumerator();
        auto.ProcessData(Remote, new byte[] { 3, 4, 5 });
        Assert.Equal(new byte[] { 3, 4, 5 }, (await TestSupport.Next(currentReader)).ToArray());
    }

    [Fact]
    public async Task ReversePeeringAndEchoCarrierUseExactBoundaries()
    {
        var clock = new Clock();
        var network = new Network();
        await using var auto = Create(network, clock: clock);
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        var socket = network.Opened.Single().Value;
        clock.Now += TimeSpan.FromSeconds(5.2);
        await auto.RunMaintenanceAsync();
        Assert.DoesNotContain(socket.Sent, send => send.Port == 29717);
        clock.Now += TimeSpan.FromTicks(1);
        await auto.RunMaintenanceAsync();
        Assert.Contains(socket.Sent, send => send.Port == 29717 && send.Address.Equals(Remote));
        clock.Now = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(6.5);
        await auto.RunMaintenanceAsync();
        Assert.Equal(0, auto.GetDiagnostics()["carrier_lost_devices"]);
        clock.Now += TimeSpan.FromTicks(1);
        await auto.RunMaintenanceAsync();
        Assert.Equal(1, auto.GetDiagnostics()["carrier_lost_devices"]);
        Discover(auto, Device, Device.Address);
        await auto.RunMaintenanceAsync();
        Assert.Equal(0, auto.GetDiagnostics()["carrier_lost_devices"]);
    }

    [Fact]
    public async Task DiscoveryAndDataPumpsWorkWithoutOptionalObservers()
    {
        var network = new Network();
        await using var auto = Create(network);
        await auto.StartAsync(default);
        var socket = network.Opened.Single().Value;
        await socket.Multicast.Writer.WriteAsync(new(Remote, new AutoPacketInterfaceOptions().GetDiscoveryToken(Remote)));
        await TestSupport.WaitFor(() => auto.PeerCount == 1);
        await socket.Data.Writer.WriteAsync(new(Remote, new byte[] { 1, 2, 3 }));
        await TestSupport.WaitFor(() => Equals(auto.GetDiagnostics()["rxb"], 3L));
    }

    [Fact]
    public async Task AddressChangesReplaceAndJoinOwnedListenersWithoutDroppingPeerIdentity()
    {
        var network = new Network();
        await using var auto = Create(network);
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        var previous = network.Opened.Single().Value;
        var updated = Device with { Address = IPAddress.Parse("fe80::9") };
        network.Devices = [updated];
        await auto.RunMaintenanceAsync();
        Assert.True(previous.Disposed);
        Assert.Equal(2, network.Opened.Count);
        Assert.Equal(1, auto.PeerCount);
        Discover(auto, updated, updated.Address);
        Assert.Equal(1, auto.PeerCount);
    }

    [Fact]
    public async Task MissingDeviceDisposesItsSessionAndPeersAndReappearanceOpensFreshSocket()
    {
        var network = new Network();
        await using var auto = Create(network);
        var peers = new List<IPacketInterface>();
        auto.ChildConnected += peers.Add;
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        var originalSocket = network.Opened[Device];
        await using var reader = peers[0].ReceiveAsync(default).GetAsyncEnumerator();
        var pendingReceive = reader.MoveNextAsync().AsTask();

        network.Devices = [];
        await auto.RunMaintenanceAsync();

        Assert.True(originalSocket.Disposed);
        Assert.Equal(0, auto.GetDiagnostics()["devices"]);
        Assert.Equal(0, auto.PeerCount);
        Assert.False(await pendingReceive.WaitAsync(TestSupport.Patience));
        Assert.True(auto.IsOnline);

        network.Devices = [Device];
        await auto.RunMaintenanceAsync();

        var restoredSocket = network.Opened[Device];
        Assert.NotSame(originalSocket, restoredSocket);
        Assert.False(restoredSocket.Disposed);
        Assert.Equal(1, auto.GetDiagnostics()["devices"]);
        Discover(auto, Device, Remote);
        Assert.Equal(2, peers.Count);
        Assert.NotSame(peers[0], peers[1]);
        await peers[1].SendAsync(new byte[] { 7 }, default);
        Assert.Contains(restoredSocket.Sent, sent =>
            sent.Address.Equals(Remote) && sent.Data.SequenceEqual(new byte[] { 7 }));
    }

    [Fact]
    public async Task InitiallyFailedDeviceSetupAndFailedReappearanceAreRetried()
    {
        var network = new Network { FailOpen = true };
        await using var auto = Create(network);
        await auto.StartAsync(default);
        Assert.Equal(0, auto.GetDiagnostics()["devices"]);

        network.FailOpen = false;
        await auto.RunMaintenanceAsync();
        var firstSocket = network.Opened[Device];
        Assert.Equal(1, auto.GetDiagnostics()["devices"]);

        network.Devices = [];
        await auto.RunMaintenanceAsync();
        Assert.True(firstSocket.Disposed);
        network.Devices = [Device];
        network.FailOpen = true;
        await auto.RunMaintenanceAsync();
        Assert.Equal(0, auto.GetDiagnostics()["devices"]);

        network.FailOpen = false;
        await auto.RunMaintenanceAsync();
        Assert.NotSame(firstSocket, network.Opened[Device]);
        Assert.False(network.Opened[Device].Disposed);
        Assert.Equal(1, auto.GetDiagnostics()["devices"]);
    }

    [Fact]
    public async Task DeviceStartFailureIsIsolatedAndEmptyInterfaceIsDiagnosed()
    {
        var errors = new ConcurrentQueue<Exception>();
        var network = new Network { FailOpen = true };
        await using var auto = Create(network, options: new() { Diagnostic = errors.Enqueue });
        await auto.StartAsync(default);
        Assert.True(auto.IsOnline);
        Assert.Equal(0, auto.GetDiagnostics()["devices"]);
        Assert.Contains(errors, error => error.Message.Contains("could not adopt", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("no connectivity", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => auto.StartAsync(default).AsTask());
    }

    [Fact]
    public async Task FailedAddressReplacementIsRetriedAtNextJob()
    {
        var network = new Network();
        await using var auto = Create(network);
        await auto.StartAsync(default);
        network.Devices = [Device with { Address = IPAddress.Parse("fe80::9") }];
        network.FailOpen = true;
        await auto.RunMaintenanceAsync();
        network.FailOpen = false;
        await auto.RunMaintenanceAsync();
        Assert.Equal(2, network.Opened.Count);
    }

    [Fact]
    public async Task AFailedReceivePumpIsDiagnosedAndReopenedWithoutStoppingOtherDevices()
    {
        var other = new AutoPacketDevice("other", 8, IPAddress.Parse("fe80::8"));
        var network = new Network { Devices = [Device, other] };
        var errors = new ConcurrentQueue<Exception>();
        await using var auto = Create(network, options: new() { Diagnostic = errors.Enqueue });
        await auto.StartAsync(default);
        var old = network.Opened[Device];
        var healthy = network.Opened[other];
        old.Data.Writer.TryComplete(new IOException("broken listener"));
        await TestSupport.WaitFor(() => errors.Any(error => error.Message.Contains("receive failed", StringComparison.Ordinal)));
        await auto.RunMaintenanceAsync();
        Assert.True(old.Disposed);
        Assert.NotSame(old, network.Opened[Device]);
        Assert.Same(healthy, network.Opened[other]);
        Assert.False(healthy.Disposed);
        Discover(auto, other, Remote);
        Assert.Equal(1, auto.PeerCount);
    }

    [Fact]
    public async Task ConcurrentTransmitsAreBoundedAndChildDisposeCancelsTheOwnedSend()
    {
        var network = new Network();
        await using var auto = Create(network);
        IPacketInterface? child = null;
        auto.ChildConnected += peer => child = peer;
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        var socket = network.Opened.Single().Value;
        socket.BlockUnicast = true;
        var send = child!.SendAsync(new byte[] { 1 }, default).AsTask();
        await socket.SendEntered.Task.WaitAsync(TestSupport.Patience);
        await Assert.ThrowsAsync<IOException>(() => child!.SendAsync(new byte[] { 2 }, default).AsTask());
        await child!.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Equal(0, auto.PeerCount);
    }

    [Fact]
    public async Task CallerSendCancellationDoesNotDetachPeerOrBlockTheNextDatagram()
    {
        var network = new Network();
        await using var auto = Create(network);
        IPacketInterface? child = null;
        auto.ChildConnected += peer => child = peer;
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        var socket = network.Opened.Single().Value;
        socket.BlockUnicast = true;
        using var stop = new CancellationTokenSource();
        var send = child!.SendAsync(new byte[] { 1 }, stop.Token).AsTask();
        await socket.SendEntered.Task.WaitAsync(TestSupport.Patience);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        socket.BlockUnicast = false;
        await child!.SendAsync(new byte[] { 2 }, default);
        Assert.Equal(1, auto.PeerCount);
        Assert.Contains(socket.Sent, sent => sent.Address.Equals(Remote) && sent.Data.SequenceEqual(new byte[] { 2 }));
    }

    [Fact]
    public async Task DiscoveryRejectsWrongGroupAndInvalidAddressWithoutChangingPeers()
    {
        await using var auto = Create(new Network());
        await auto.StartAsync(default);
        auto.ProcessDiscovery(Device, Remote, new AutoPacketInterfaceOptions { GroupId = "other" }.GetDiscoveryToken(Remote));
        auto.ProcessDiscovery(Device, IPAddress.Loopback, new byte[32]);
        var global = IPAddress.Parse("2001:db8::1");
        auto.ProcessDiscovery(Device, global, new AutoPacketInterfaceOptions().GetDiscoveryToken(global));
        Assert.Equal(0, auto.PeerCount);
        Assert.Equal(3L, auto.GetDiagnostics()["dropped_frames"]);
    }

    [Fact]
    public async Task CancellationAndRepeatedDisposeEndParentAndChildrenPromptly()
    {
        var network = new Network();
        var auto = Create(network);
        IPacketInterface? child = null;
        auto.ChildConnected += peer => child = peer;
        await auto.StartAsync(default);
        Discover(auto, Device, Remote);
        await using var parentReader = auto.ReceiveAsync(default).GetAsyncEnumerator();
        await using var childReader = child!.ReceiveAsync(default).GetAsyncEnumerator();
        var parentMove = parentReader.MoveNextAsync().AsTask();
        var childMove = childReader.MoveNextAsync().AsTask();
        await Task.WhenAll(auto.DisposeAsync().AsTask(), auto.DisposeAsync().AsTask()).WaitAsync(TestSupport.Patience);
        Assert.False(await parentMove.WaitAsync(TestSupport.Patience));
        Assert.False(await childMove.WaitAsync(TestSupport.Patience));
        Assert.All(network.Opened.Values, socket => Assert.True(socket.Disposed));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => auto.StartAsync(default).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => child!.SendAsync(new byte[] { 1 }, default).AsTask());
    }

    [Fact]
    public async Task CancelledStartupRollsBackAllOpenedSockets()
    {
        var network = new Network();
        await using var auto = new AutoPacketInterface(new(), network, TimeProvider.System, TimeSpan.FromHours(1));
        using var stop = new CancellationTokenSource();
        var start = auto.StartAsync(stop.Token).AsTask();
        await TestSupport.WaitFor(() => network.Opened.Count == 1);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(network.Opened.Single().Value.Disposed);
    }

    [Fact]
    public async Task DisposeDuringStartupCancelsWarmupAndJoinsStart()
    {
        var network = new Network();
        var auto = new AutoPacketInterface(new(), network, TimeProvider.System, TimeSpan.FromHours(1));
        var start = auto.StartAsync(default).AsTask();
        await TestSupport.WaitFor(() => network.Opened.Count == 1);
        await auto.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(network.Opened.Single().Value.Disposed);
        Assert.False(auto.IsOnline);
    }

    [Fact]
    public async Task PreCancelledStartAndReceiveCancellationAreExplicit()
    {
        await using var auto = Create(new Network());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auto.StartAsync(stop.Token).AsTask());
        await auto.StartAsync(default);
        await using var reader = auto.ReceiveAsync(stop.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task DiagnosticObserverFailureDoesNotDisableProcessing()
    {
        await using var auto = Create(new Network(), options: new() { Diagnostic = _ => throw new InvalidOperationException("diagnostic") });
        await auto.StartAsync(default);
        auto.ProcessDiscovery(Device, Remote, new byte[] { 1 });
        Discover(auto, Device, Remote);
        Assert.Equal(1, auto.PeerCount);
    }

    private static AutoPacketInterface Create(Network network, Clock? clock = null, AutoPacketInterfaceOptions? options = null) =>
        new(options ?? new() { Diagnostic = _ => { } }, network, clock ?? new(), TimeSpan.Zero);

    private static void Discover(AutoPacketInterface auto, AutoPacketDevice device, IPAddress address) =>
        auto.ProcessDiscovery(device, address, new AutoPacketInterfaceOptions().GetDiscoveryToken(address));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Network : IAutoPacketNetwork
    {
        public IReadOnlyList<AutoPacketDevice> Devices { get; set; } = [Device];
        public bool FailOpen { get; set; }
        public ConcurrentDictionary<AutoPacketDevice, SocketStub> Opened { get; } = new();
        public IReadOnlyList<AutoPacketDevice> GetDevices() => Devices;
        public IAutoPacketDevice Open(AutoPacketDevice device, AutoPacketInterfaceOptions options)
        {
            if (FailOpen) throw new SocketException((int)SocketError.AddressNotAvailable);
            var socket = new SocketStub();
            Opened[device] = socket;
            return socket;
        }
    }

    private sealed class SocketStub : IAutoPacketDevice
    {
        public Channel<AutoPacketDatagram> Multicast { get; } = Channel.CreateBounded<AutoPacketDatagram>(32);
        public Channel<AutoPacketDatagram> Unicast { get; } = Channel.CreateBounded<AutoPacketDatagram>(32);
        public Channel<AutoPacketDatagram> Data { get; } = Channel.CreateBounded<AutoPacketDatagram>(32);
        public ConcurrentQueue<(byte[] Data, IPAddress Address, int Port)> Sent { get; } = new();
        public bool BlockUnicast { get; set; }
        public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public ValueTask<AutoPacketDatagram> ReceiveAsync(bool discovery, bool multicast, CancellationToken cancellationToken) =>
            (discovery ? multicast ? Multicast : Unicast : Data).Reader.ReadAsync(cancellationToken);
        public async ValueTask SendAsync(ReadOnlyMemory<byte> data, IPAddress target, int port, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (BlockUnicast && !target.IsIPv6Multicast)
            {
                SendEntered.TrySetResult();
                await _releaseSend.Task.WaitAsync(cancellationToken);
            }
            Sent.Enqueue((data.ToArray(), target, port));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
