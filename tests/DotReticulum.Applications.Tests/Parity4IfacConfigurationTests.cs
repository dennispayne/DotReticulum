using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Interfaces;

namespace DotReticulum.Applications.Tests;

public sealed class Parity4IfacConfigurationTests
{
    [Theory]
    [InlineData("networkname", "passphrase")]
    [InlineData("network_name", "passphrase")]
    [InlineData("networkname", "pass_phrase")]
    [InlineData("network_name", "pass_phrase")]
    public async Task SourceAliasesDeriveIdenticalAccessCodesAndConfigureBearer(string nameKey, string passKey)
    {
        var config = RnsdConfigParser.Parse($"""
            [interfaces]
              [[private TCP]]
                type = TCPClientInterface
                enabled = yes
                target_host = localhost
                target_port = 4242
                {nameKey} = "test network"
                {passKey} = "synthetic passphrase"
                ifac_size = 128
                bitrate = 64000
                max_reconnect_tries = 3
                connect_timeout = 2
            """).Interfaces.Single();
        using var parsed = config.CreateAccessCode();
        using var expected = InterfaceAccessCode.Derive("test network", "synthetic passphrase");
        Assert.NotNull(parsed);
        Assert.Equal(16, parsed.Size);
        Assert.Equal(expected.ExportPublicKey(), parsed.ExportPublicKey());
        Assert.Equal(expected.Signature.ToArray(), parsed.Signature.ToArray());
        Assert.Equal(3, config.MaxReconnectAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), config.ConnectTimeout);
        await using var iface = RnsdHost.CreateInterface(config);
        Assert.IsType<TcpClientPacketInterface>(iface);
        Assert.Equal(64000, iface.Bitrate);
        Assert.Equal(1024, iface.HardwareMtu);
        Assert.True(iface.SupportsMtuDiscovery);
    }

    [Fact]
    public void LaterNonemptyAliasesOverrideButNoneDoesNotEraseEarlierValues()
    {
        var config = ParseExtra("""
            networkname = original
            network_name = replacement
            passphrase = synthetic
            pass_phrase = None
            """);
        Assert.Equal("replacement", config.IfacNetworkName);
        Assert.Equal("synthetic", config.IfacPassphrase);
        using var code = config.CreateAccessCode();
        using var expected = InterfaceAccessCode.Derive("replacement", "synthetic");
        Assert.Equal(expected.ExportPublicKey(), code!.ExportPublicKey());
    }

    [Fact]
    public void EmptyAndAmbiguousNoneValuesDisableIfac()
    {
        var config = ParseExtra("""
            networkname = ""
            network_name = None
            passphrase = None
            pass_phrase = ""
            """);
        Assert.Null(config.CreateAccessCode());
    }

    [Theory]
    [InlineData(-1, 16)]
    [InlineData(0, 16)]
    [InlineData(7, 16)]
    [InlineData(8, 1)]
    [InlineData(15, 1)]
    [InlineData(128, 16)]
    [InlineData(512, 64)]
    public void IfacSizeIsBitsWithUpstreamFloorAndMinimumFallback(int configuredBits, int bytes)
    {
        Assert.Equal(bytes, ParseExtra($"ifac_size = {configuredBits}").IfacSize);
    }

    [Theory]
    [InlineData("ifac_size = 520")]
    [InlineData("ifac_size = invalid")]
    [InlineData("bitrate = NaN")]
    [InlineData("fixed_mtu = 499")]
    [InlineData("fixed_mtu = 524289")]
    [InlineData("connect_timeout = 0")]
    [InlineData("max_reconnect_tries = -1")]
    public void MalformedAndUnboundedSettingsAreDiagnosed(string extra) =>
        Assert.Throws<FormatException>(() => ParseExtra(extra));

    [Fact]
    public async Task FixedMtuOverridesAutomaticSelectionAndLowConfiguredBitrateFallsBack()
    {
        var config = ParseExtra("""
            fixed_mtu = 4096
            bitrate = 4
            """);
        Assert.Null(config.ConfiguredBitrate);
        await using var iface = RnsdHost.CreateInterface(config);
        Assert.Equal(10_000_000, iface.Bitrate);
        Assert.Equal(4096, iface.HardwareMtu);
        var serial = RnsdConfigParser.Parse("""
            [interfaces]
              [[serial]]
                type = SerialInterface
                enabled = yes
                port = no-such-port
                speed = 9600
            """).Interfaces.Single();
        Assert.Equal(8, serial.IfacSize);
        await using var serialBearer = RnsdHost.CreateInterface(serial);
        Assert.Equal(9600, serialBearer.Bitrate);
        Assert.Equal(564, serialBearer.HardwareMtu);
        Assert.False(serialBearer.SupportsMtuDiscovery);
    }

    private static RnsdInterfaceConfig ParseExtra(string extra) => RnsdConfigParser.Parse($"""
        [interfaces]
          [[private TCP]]
            type = TCPClientInterface
            enabled = yes
            target_host = localhost
            target_port = 4242
        {extra}
        """).Interfaces.Single();
}
