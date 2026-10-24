using DotReticulum.Applications.Rnsd;
using DotReticulum.Interfaces;

namespace DotReticulum.Applications.Tests;

public sealed class Parity6InterfaceConfigurationTests
{
    [Theory]
    [InlineData("AutoInterface", "group_id=isolated-fixture\ndevices=alpha,beta\nignored_devices=gamma", "AutoPacketInterface", 10_000_000, 16)]
    [InlineData("PipeInterface", "command=never-started-fixture argument", "PipePacketInterface", 1_000_000, 8)]
    [InlineData("KISSInterface", "port=NEVER_OPENED", "KissPacketInterface", 1200, 8)]
    [InlineData("AX25KISSInterface", "port=NEVER_OPENED\ncallsign=N0CALL\nssid=3", "Ax25KissPacketInterface", 1200, 8)]
    [InlineData("WeaveInterface", "port=NEVER_OPENED", "WeavePacketInterface", 250_000, 16)]
    [InlineData("BackboneInterface", "listen_ip=127.0.0.1\nlisten_port=0", "BackbonePacketInterface", 100_000_000, 16)]
    [InlineData("BackboneInterface", "target_host=localhost\ntarget_port=4242", "BackboneClientPacketInterface", 100_000_000, 16)]
    [InlineData("BackboneClientInterface", "remote=localhost\nport=4242", "BackboneClientPacketInterface", 100_000_000, 16)]
    public async Task NewNativeFactoriesPreserveDefaultsWithoutOpeningDevices(string type, string settings, string className, long bitrate, int ifacSize)
    {
        var cfg = Assert.Single(RnsdConfigParser.Parse($"[interfaces]\n[[Fixture]]\nenabled=yes\ntype={type}\n{settings}").Interfaces);
        await using var bearer = RnsdHost.CreateInterface(cfg);
        Assert.Equal(className, bearer.GetType().Name);
        Assert.Equal(bitrate, bearer.Bitrate);
        Assert.Equal(ifacSize, cfg.IfacSize);
    }

    [Theory]
    [InlineData("KISSInterface", "port=NEVER_OPENED")]
    [InlineData("AX25KISSInterface", "port=NEVER_OPENED\ncallsign=N0CALL\nssid=3")]
    [InlineData("AutoInterface", "devices=alpha,beta")]
    [InlineData("WeaveInterface", "port=NEVER_OPENED")]
    [InlineData("PipeInterface", "command=never-started-fixture argument")]
    public async Task GenericBitrateIsEffectiveNotMerelyAccepted(string type, string settings)
    {
        var cfg = Assert.Single(RnsdConfigParser.Parse($"[interfaces]\n[[Fixture]]\nenabled=yes\ntype={type}\nbitrate=7000\n{settings}").Interfaces);
        await using var bearer = RnsdHost.CreateInterface(cfg);
        Assert.Equal(7000, bearer.Bitrate);
    }

    [Fact]
    public void AutoListConfigurationRetainsSeparateDevices()
    {
        var cfg = Assert.Single(RnsdConfigParser.Parse("[interfaces]\n[[Auto]]\ntype=AutoInterface\nenabled=yes\ndevices=\"device, one\", device-two,\nignored_devices=,").Interfaces);
        Assert.Equal(["device, one", "device-two"], cfg.GetList("devices"));
        Assert.Empty(cfg.GetList("ignored_devices"));
    }

    [Fact]
    public async Task SerialFactoryPassesAllSourcePortSettingsWithoutOpeningThePort()
    {
        var cfg = Assert.Single(RnsdConfigParser.Parse("[interfaces]\n[[Serial]]\ntype=SerialInterface\nenabled=yes\nport=NEVER_OPENED\nspeed=19200\nparity=even\ndatabits=7\nstopbits=2\noutgoing=no").Interfaces);
        await using var bearer = Assert.IsType<SerialPacketInterface>(RnsdHost.CreateInterface(cfg));
        Assert.Equal(7, bearer.DataBits);
        Assert.Equal(System.IO.Ports.Parity.Even, bearer.Parity);
        Assert.Equal(System.IO.Ports.StopBits.Two, bearer.StopBits);
        Assert.Equal(19200, bearer.Bitrate);
        Assert.False(cfg.Outgoing);
    }

    [Fact]
    public async Task TcpKissFactoryDisablesTunnelSynthesis()
    {
        var cfg = Assert.Single(RnsdConfigParser.Parse("[interfaces]\n[[TCP]]\ntype=TCPClientInterface\nenabled=yes\ntarget_host=localhost\ntarget_port=4242\nkiss_framing=yes\ni2p_tunneled=yes").Interfaces);
        await using var bearer = Assert.IsType<TcpClientPacketInterface>(RnsdHost.CreateInterface(cfg));
        Assert.True(bearer.KissFraming);
        Assert.False(bearer.WantsTunnel);
    }

    [Fact]
    public async Task RNodeNestedSectionsPreserveParentEnableAndIndependentChildSettings()
    {
        var cfg = Assert.Single(RnsdConfigParser.Parse("""
            [interfaces]
            [[Radio]]
            type=RNodeMultiInterface
            interface_enabled=yes
            port=NEVER_OPENED
            outgoing=no
            network_name=parent-only
            [[[Disabled]]]
            interface_enabled=no
            [[[Active]]]
            interface_enabled=yes
            outgoing=yes
            vport=2
            frequency=868000000
            bandwidth=125000
            txpower=0
            spreadingfactor=7
            codingrate=5
            airtime_limit_short=0.75
            """).Interfaces);
        Assert.Equal(2, cfg.Subinterfaces.Count);
        Assert.Equal("yes", cfg.Subinterfaces["Active"]["outgoing"]);
        Assert.False(cfg.GetBool("enabled"));
        Assert.False(cfg.Outgoing);
        await using var bearer = Assert.IsType<RNodeMultiPacketInterface>(RnsdHost.CreateInterface(cfg));
        Assert.Equal(8, cfg.IfacSize);
    }

    [Theory]
    [InlineData("mode=ap")]
    [InlineData("enabled=yes")]
    [InlineData("ifac_size=64")]
    public void IneffectiveNestedRadioControlsAreDiagnosedRatherThanIgnored(string setting) =>
        Assert.Contains("unsupported", Assert.Throws<FormatException>(() => RnsdConfigParser.Parse(
            $"[interfaces]\n[[Radio]]\ntype=RNodeMultiInterface\nenabled=yes\nport=NEVER_OPENED\n[[[Child]]]\n{setting}")).Message);

    [Fact]
    public void NestedRadioSectionUnderAnotherBearerIsRejected() =>
        Assert.Throws<FormatException>(() => RnsdConfigParser.Parse("[interfaces]\n[[Auto]]\ntype=AutoInterface\nenabled=yes\n[[[NotARadio]]]\nvport=0"));

    [Fact]
    public void BackboneAliasesOverrideExplicitFieldsBeforeClientSelection()
    {
        var cfg = Assert.Single(RnsdConfigParser.Parse("""
            [interfaces]
            [[Backbone]]
            type=BackboneInterface
            enabled=yes
            listen_ip=old-listen
            listen_on=127.0.0.1
            listen_port=41000
            target_port=41001
            target_host=old-target
            remote=localhost
            port=41002
            """).Interfaces);
        Assert.Equal("localhost", cfg.Get("target_host"));
        Assert.Equal("127.0.0.1", cfg.Get("listen_ip"));
        Assert.Equal(41002, cfg.GetInt("listen_port", 0));
        Assert.Equal(41002, cfg.GetInt("target_port", 0));
    }

    [Fact]
    public void ExplicitBackboneClientNeverFallsBackToAListener() =>
        Assert.Contains("target_host", Assert.Throws<FormatException>(() => RnsdConfigParser.Parse("""
            [interfaces]
            [[Backbone]]
            type=BackboneClientInterface
            enabled=yes
            listen_ip=127.0.0.1
            port=4242
            """)).Message);

    [Fact]
    public async Task FractionalBackboneAndPipeDurationsAreAcceptedByTheirFactories()
    {
        var backbone = Assert.Single(RnsdConfigParser.Parse("""
            [interfaces]
            [[Backbone]]
            type=BackboneInterface
            enabled=yes
            listen_ip=127.0.0.1
            listen_port=0
            fast_flapping_threshold=1.5
            fast_flapping_block_time=0.5
            """).Interfaces);
        await using var listener = RnsdHost.CreateInterface(backbone);
        var pipe = Assert.Single(RnsdConfigParser.Parse("""
            [interfaces]
            [[Pipe]]
            type=PipeInterface
            enabled=yes
            command=never-started-fixture
            respawn_delay=0.25
            """).Interfaces);
        await using var child = RnsdHost.CreateInterface(pipe);
        Assert.Equal(1.5, backbone.GetDouble("fast_flapping_threshold", 0));
        Assert.Equal(0.5, backbone.GetDouble("fast_flapping_block_time", 0));
        Assert.Equal(0.25, pipe.GetDouble("respawn_delay", 0));
    }

    [Theory]
    [InlineData("listen_ip=localhost\nlisten_port=0", true, false)]
    [InlineData("forward_ip=localhost\nforward_port=4242", false, true)]
    [InlineData("listen_ip=localhost\nlisten_port=0\nforward_ip=localhost\nport=4242", true, true)]
    public async Task UdpConfigurationWiresIndependentDirectionsWithoutInventedDefaults(string settings, bool receives, bool forwards)
    {
        var config = RnsdConfigParser.Parse($"[interfaces]\n[[UDP]]\ntype=UDPInterface\nenabled=yes\n{settings}");
        await using var iface = Assert.IsType<UdpPacketInterface>(RnsdHost.CreateInterface(Assert.Single(config.Interfaces)));
        await iface.StartAsync(default);
        Assert.Equal(receives, iface.GetDiagnostics()["receives"]);
        Assert.Equal(forwards, iface.GetDiagnostics()["forwards"]);
        Assert.Equal(receives, iface.LocalEndpoint is not null);
    }

    [Fact]
    public void UdpDeviceConfigurationPreservesExplicitAddressesAndPorts()
    {
        var config = RnsdConfigParser.Parse("""
            [interfaces]
            [[UDP]]
            type=UDPInterface
            enabled=yes
            device=an-explicit-device
            port=41000
            listen_port=41001
            forward_port=41002
            """);
        var entry = Assert.Single(config.Interfaces);
        Assert.Equal("an-explicit-device", entry.Get("device"));
        Assert.Equal(41001, entry.GetInt("listen_port", 0));
        Assert.Equal(41002, entry.GetInt("forward_port", 0));
    }

    [Theory]
    [InlineData("forward_ip=localhost")]
    [InlineData("listen_port=4242")]
    [InlineData("device=missing")]
    [InlineData("forward_ip=localhost\nforward_port=65536")]
    [InlineData("listen_ip=localhost\nlisten_port=-1")]
    public void UdpInvalidOrIneffectiveConfigurationIsExplicitlyRejected(string settings) =>
        Assert.Throws<FormatException>(() => RnsdConfigParser.Parse($"[interfaces]\n[[UDP]]\ntype=UDPInterface\nenabled=yes\n{settings}"));
}
