using System.Text.Json;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

/// <summary>
/// Configuration/log evidence: GenerateRnsdConfiguration.py executes hosted RNS
/// a29bfb51c468945c96a1ee22cae0114e993e2391. Per-source SHA-256 values are in
/// Fixtures/rnsd-configuration.json and checked by the generator.
/// </summary>
public sealed class RnsdConfigurationParityTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Environment.CurrentDirectory, "TestResults", "rnsd-parity-" + Guid.NewGuid().ToString("N"));
    public RnsdConfigurationParityTests() => Directory.CreateDirectory(_workspace);
    public void Dispose() => Directory.Delete(_workspace, recursive: true);
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rnsd-configuration.json")));

    [Fact]
    public void EnableFlagsAndModesMatchExecutedReference()
    {
        using var fixture = Fixture();
        foreach (var entry in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var config = RnsdConfigParser.Parse("[interfaces]\n[[Fixture]]\ntype=TCPServerInterface\nlisten_port=4242\n" + entry.GetProperty("settings").GetString());
            var actual = Assert.Single(config.Interfaces);
            Assert.Equal(entry.GetProperty("enabled").GetBoolean(), actual.Enabled);
            if (actual.Enabled) Assert.Equal(entry.GetProperty("mode").GetInt32(), (int)actual.Mode);
        }
    }

    [Fact]
    public void QuotedCommentsAndListsMatchConfigObj()
    {
        using var fixture = Fixture();
        var quoted = fixture.RootElement.GetProperty("quoted");
        var config = RnsdConfigParser.Parse("""
            [interfaces] # top
            [[Fixture]] # nested
            type=TCPClientInterface
            enabled=yes
            target_host="a # b, c" # trailing
            target_port='4242'
            """);
        Assert.Equal(quoted.GetProperty("name").GetString(), config.Interfaces[0].Get("target_host"));
        Assert.Equal(quoted.GetProperty("sources").EnumerateArray().Select(s => s.GetString()), RnsdConfigParser.ParseList("\"aa\", 'bb,cc', dd,"));
    }

    [Fact]
    public void TypedRuntimeOptionsApplyDefaultsListsDeduplicationAndClamps()
    {
        const string hash = "0123456789abcdef0123456789abcdef";
        var config = RnsdConfigParser.Parse($"""
            [reticulum]
            share_instance = no
            shared_instance_port = 40001
            instance_control_port = 40002
            shared_instance_type = tcp
            rpc_key = "01020304"
            network_identity = "identities/network"
            enable_remote_management=yes
            enable_interface_management=no
            remote_management_allowed={hash}, '{hash}'
            discover_interfaces=yes
            required_discovery_value=0
            interface_discovery_sources={hash},
            autoconnect_discovered_interfaces=3
            autoconnect_unverified_implementations=yes
            autoconnect_interface_mode=ptp
            autoconnect_interface_gravity=-2
            autoconnect_announces_to_internal=no
            publish_blackhole=yes
            blackhole_sources={hash}, {hash}
            blackhole_update_interval=0.5
            [logging]
            loglevel=100
            logtimestamps=no
            """);
        Assert.False(config.SharedInstance.Enabled);
        Assert.Equal(40001, config.SharedInstance.InstancePort);
        Assert.Equal(40002, config.SharedInstance.ControlPort);
        Assert.Null(config.SharedInstance.SocketPath);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, config.SharedInstance.RpcKey);
        Assert.Equal("identities/network", config.NetworkIdentityPath);
        Assert.True(config.EnableRemoteManagement);
        Assert.False(config.EnableInterfaceManagement);
        Assert.Single(config.RemoteManagementAllowed);
        Assert.True(config.Discovery.Enabled);
        Assert.Equal(16, config.Discovery.RequiredStampValue);
        Assert.Single(config.Discovery.AllowedSources);
        Assert.Equal(3, config.Discovery.MaxAutoConnected);
        Assert.True(config.Discovery.AllowUnverifiedImplementations);
        Assert.Equal("ptp", config.Discovery.InterfaceMode);
        Assert.Equal(-2, config.Discovery.InterfaceGravity);
        Assert.False(config.Discovery.AnnouncesToInternal);
        Assert.True(config.Blackholes.Publish);
        Assert.Single(config.Blackholes.Sources);
        Assert.Equal(TimeSpan.FromMinutes(2), config.Blackholes.UpdateInterval);
        Assert.Equal(8, config.LogLevel);
        Assert.False(config.LogTimestamps);
    }

    [Fact]
    public void DefaultRuntimeSettingsMatchUpstream()
    {
        var config = RnsdConfigParser.Parse("");
        Assert.False(config.EnableTransport);
        Assert.False(config.StaticTransportIdentity);
        Assert.True(config.SharedInstance.Enabled);
        Assert.Equal(37428, config.SharedInstance.InstancePort);
        Assert.Equal(37429, config.SharedInstance.ControlPort);
        Assert.False(config.EnableRemoteManagement);
        Assert.True(config.EnableInterfaceManagement);
        Assert.False(config.Discovery.Enabled);
        Assert.False(config.Blackholes.Publish);
        Assert.Equal(TimeSpan.FromHours(1), config.Blackholes.UpdateInterval);
        Assert.True(config.LogTimestamps);
        Assert.Equal(3, config.LogLevel);
        Assert.Equal(4, RnsdConfigParser.Parse(RnsdConfigurationFiles.DefaultConfig).LogLevel);
    }

    [Fact]
    public void DiscoveryInterfaceMetadataIsTypedClampedAndImpliesStaticIdentityAndGatewayMode()
    {
        var config = RnsdConfigParser.Parse("""
            [reticulum]
            static_transport_identity=no
            [interfaces]
            [[Published]]
            type=TCPServerInterface
            enabled=yes
            listen_port=4242
            discoverable=yes
            announce_interval=1
            discovery_stamp_value=0
            discovery_name="Published # name"
            discovery_encrypt=yes
            reachable_on=example.org
            publish_ifac=no
            location_cmd="explicit-authorization-required --location"
            latitude=1.25
            longitude=-2.5
            height=3.75
            discovery_frequency=1000
            discovery_bandwidth=2000
            discovery_modulation=3
            discovery_lxmf_address=0123456789abcdef0123456789abcdef
            """);
        var cfg = Assert.Single(config.Interfaces);
        var metadata = cfg.Discovery;
        Assert.True(config.StaticTransportIdentity);
        Assert.Equal(InterfaceMode.Gateway, cfg.Mode);
        Assert.True(metadata.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(5), metadata.AnnounceInterval);
        Assert.Equal(16, metadata.StampValue);
        Assert.Equal("Published # name", metadata.Name);
        Assert.True(metadata.Encrypt);
        Assert.Equal("example.org", metadata.ReachableOn);
        Assert.False(metadata.PublishIfac);
        Assert.Equal("explicit-authorization-required --location", metadata.LocationCommand);
        Assert.Equal(1.25, metadata.Latitude);
        Assert.Equal(-2.5, metadata.Longitude);
        Assert.Equal(3.75, metadata.Height);
        Assert.Equal(1000, metadata.Frequency);
        Assert.Equal(2000, metadata.Bandwidth);
        Assert.Equal(3, metadata.Modulation);
        Assert.Equal(Convert.FromHexString("0123456789abcdef0123456789abcdef"), metadata.LxmfAddress);
    }

    [Fact]
    public void DiscoveryModeWarningOverrideAndExplicitStaticIdentityArePreserved()
    {
        var config = RnsdConfigParser.Parse("""
            [reticulum]
            static_transport_identity=yes
            [interfaces]
            [[Published]]
            type=TCPServerInterface
            enabled=yes
            port=4242
            discoverable=yes
            mode=ptp
            ignore_config_warnings=yes
            """);
        Assert.True(config.StaticTransportIdentity);
        Assert.Equal(InterfaceMode.PointToPoint, config.Interfaces[0].Mode);
        Assert.Equal(TimeSpan.FromHours(6), config.Interfaces[0].Discovery.AnnounceInterval);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    public void BootstrapOnlyHasTypedBooleanConfiguration(string value, bool expected)
    {
        var cfg = Assert.Single(RnsdConfigParser.Parse($"[interfaces]\n[[Bootstrap]]\ntype=TCPServerInterface\nenabled=yes\nport=4242\nbootstrap_only={value}").Interfaces);
        Assert.Equal(expected, cfg.BootstrapOnly);
    }

    [Theory]
    [InlineData("latitude=NaN")]
    [InlineData("height=Infinity")]
    [InlineData("discovery_lxmf_address=nothex")]
    [InlineData("discovery_lxmf_address=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("discovery_stamp_value=257")]
    [InlineData("discovery_encrypt=maybe")]
    [InlineData("bootstrap_only=maybe")]
    public void DiscoveryMetadataRejectsMalformedValues(string options) =>
        Assert.Throws<FormatException>(() => RnsdConfigParser.Parse("[interfaces]\n[[Published]]\ntype=TCPServerInterface\nenabled=yes\nport=4242\ndiscoverable=yes\n" + options));

    [Theory]
    [InlineData("[reticulum]\nqlen_in_unknown=1")]
    [InlineData("[reticulum]\npanic_on_interface_error=yes")]
    [InlineData("[logging]\ncompact_log_fmt=yes")]
    [InlineData("[other]\na=b")]
    [InlineData("[interfaces]\n[[X]]\ntype=TCPServerInterface\nenabled=yes\nlisten_port=4242\nkiss_framing=yes")]
    public void IneffectiveControlsFailWithUnsupportedDiagnostic(string text) =>
        Assert.Contains("unsupported", Assert.Throws<FormatException>(() => RnsdConfigParser.Parse(text)).Message);

    [Theory]
    [InlineData("[reticulum]\nrpc_key=nothex")]
    [InlineData("[reticulum]\nblackhole_sources=aa")]
    [InlineData("[reticulum]\ninterface_discovery_sources=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("[reticulum]\nshared_instance_port=0")]
    [InlineData("[reticulum]\nblackhole_update_interval=NaN")]
    [InlineData("[reticulum]\nblackhole_update_interval=1e300")]
    [InlineData("[reticulum]\nautoconnect_interface_mode=imaginary")]
    [InlineData("[reticulum]\n[reticulum]")]
    [InlineData("[interfaces]\n[[X]]\ntype=TCPClientInterface\nenabled=yes\ntarget_host=\"unterminated")]
    [InlineData("[interfaces]\n[[X]]\ntype=TCPServerInterface\nenabled=yes\nlisten_port=65536")]
    [InlineData("[reticulum]\nenable_transport=yes\nenable_transport=no")]
    public void InvalidSettingsAreNotSilentlyAccepted(string text) => Assert.Throws<FormatException>(() => RnsdConfigParser.Parse(text));

    [Fact]
    public void TcpServerPortOverridesListenPortWhileUdpUsesFallbackOnly()
    {
        var config = RnsdConfigParser.Parse("""
            [interfaces]
            [[TCP]]
            type=TCPServerInterface
            enabled=yes
            port=41001
            listen_port=41002
            [[UDP]]
            type=UDPInterface
            enabled=yes
            listen_ip=127.0.0.1
            forward_ip=127.0.0.1
            port=42001
            listen_port=42002
            """);
        Assert.Equal(41001, config.Interfaces[0].GetInt("listen_port", 0));
        Assert.Equal(42002, config.Interfaces[1].GetInt("listen_port", 0));
        Assert.Equal(42001, config.Interfaces[1].GetInt("forward_port", 0));
    }

    [Fact]
    public void ListenPortZeroRemainsAvailableForEphemeralBinding()
    {
        var cfg = Assert.Single(RnsdConfigParser.Parse("[interfaces]\n[[X]]\ntype=TCPServerInterface\nenabled=yes\nlisten_port=0").Interfaces);
        Assert.Equal(0, cfg.GetInt("listen_port", 42));
    }

    [Theory]
    [InlineData("TCPClientInterface", "target_host=localhost\nport=4242")]
    [InlineData("TCPServerInterface", "listen_ip=127.0.0.1")]
    [InlineData("UDPInterface", "port=4242")]
    public void NoInventedNetworkPortsOrAddresses(string type, string options) =>
        Assert.Throws<FormatException>(() => RnsdConfigParser.Parse($"[interfaces]\n[[X]]\ntype={type}\nenabled=yes\n{options}"));

    [Fact]
    public void ExtensionRequiresExplicitRegistrationAndDeclaredOptions()
    {
        const string text = "[interfaces]\n[[X]]\ntype=ExampleNativeInterface\nenabled=yes\nendpoint=\"quoted # endpoint\"";
        Assert.Throws<FormatException>(() => RnsdConfigParser.Parse(text));
        var registry = new InterfaceFactoryRegistry();
        registry.Register("ExampleNativeInterface", ["endpoint", "peers"], _ => throw new InvalidOperationException("Parser must not instantiate extensions."));
        var cfg = Assert.Single(RnsdConfigParser.Parse(text + "\npeers='first,peer', second,", registry).Interfaces);
        Assert.Equal(RnsdInterfaceType.Extension, cfg.Type);
        Assert.Equal("ExampleNativeInterface", cfg.TypeName);
        Assert.Equal("quoted # endpoint", cfg.Get("endpoint"));
        Assert.Equal(["first,peer", "second"], cfg.GetList("peers"));
        Assert.Throws<FormatException>(() => RnsdConfigParser.Parse(text + "\nsilent_ignored_option=yes", registry));
    }

    [Fact]
    public void ScalarSettingsRejectSingleItemListAndAclAcceptsConfigObjEmptyList()
    {
        Assert.Throws<FormatException>(() => RnsdConfigParser.Parse("[reticulum]\nenable_transport=yes,"));
        Assert.Empty(RnsdConfigParser.Parse("[reticulum]\nblackhole_sources=,").Blackholes.Sources);
        Assert.Throws<FormatException>(() => RnsdConfigParser.ParseList("first,,last"));
    }

    [Fact]
    public void SearchOrderAndMissingDirectorySemanticsMatchUpstream()
    {
        var system = Path.Combine(_workspace, "system");
        var home = Path.Combine(_workspace, "home");
        var user = Path.Combine(home, ".config", "reticulum");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "config"), "");
        Assert.Equal(user, RnsdConfigurationFiles.Resolve(null, home, system).Dir);
        Directory.CreateDirectory(system);
        File.WriteAllText(Path.Combine(system, "config"), "");
        Assert.Equal(system, RnsdConfigurationFiles.Resolve(null, home, system).Dir);
        File.Delete(Path.Combine(system, "config"));
        File.Delete(Path.Combine(user, "config"));
        Assert.Equal(Path.Combine(home, ".reticulum"), RnsdConfigurationFiles.Resolve(null, home, system).Dir);
        var missing = Path.Combine(_workspace, "not-created");
        Assert.Equal((missing, Path.Combine(missing, "config")), RnsdConfigurationFiles.Resolve(missing));
    }

    [Fact]
    public void DefaultCreationIsExclusiveAndPreservesExistingFiles()
    {
        var dir = Path.Combine(_workspace, "new");
        var file = Path.Combine(dir, "config");
        Assert.True(RnsdConfigurationFiles.EnsureCreated(dir, file));
        var text = File.ReadAllText(file);
        Assert.Empty(RnsdConfigParser.Parse(text).Interfaces);
        Assert.False(RnsdConfigurationFiles.EnsureCreated(dir, file));
        Assert.Equal(text, File.ReadAllText(file));
    }

    [Fact]
    public async Task ConcurrentDefaultCreationPublishesOneWholeConfiguration()
    {
        var dir = Path.Combine(_workspace, "concurrent-default");
        var file = Path.Combine(dir, "config");
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            var created = RnsdConfigurationFiles.EnsureCreated(dir, file);
            Assert.Equal(4, RnsdConfigParser.Parse(File.ReadAllText(file)).LogLevel);
            return created;
        })));
        Assert.Single(results, created => created);
        Assert.Single(Directory.GetFiles(dir));
    }

    [Fact]
    public void SharedConfigurationLoaderCreatesAndLoadsBothDirectoryAndExistingFilePaths()
    {
        var dir = Path.Combine(_workspace, "loader");
        var loaded = RnsdApp.LoadConfiguration(dir);
        Assert.Equal(dir, loaded.Directory);
        Assert.Equal(Path.Combine(dir, "config"), loaded.File);
        Assert.Equal(loaded.File, loaded.Config.SourcePath);
        Assert.Equal(4, loaded.Config.LogLevel);
        var file = RnsdApp.LoadConfiguration(loaded.File);
        Assert.Equal(loaded.Directory, file.Directory);
        Assert.Equal(loaded.File, file.File);
        Assert.Equal(file.File, file.Config.SourcePath);
        Assert.Equal(4, file.Config.LogLevel);
        var custom = Path.Combine(dir, "custom.ini");
        File.WriteAllText(custom, "[logging]\nloglevel=5");
        var explicitFile = RnsdApp.LoadConfiguration(custom);
        Assert.Equal(custom, explicitFile.Config.SourcePath);
    }

    [Fact]
    public void AllNineLogLevelsMatchPinnedPythonOutput()
    {
        using var fixture = Fixture();
        using var output = new StringWriter();
        var logger = new RnsdLogger(8, false, output, TextWriter.Null);
        for (var level = 0; level <= 8; level++) logger.Log(level, "fixture");
        Assert.Equal(fixture.RootElement.GetProperty("logLines").EnumerateArray().Select(s => s.GetString()), output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(RnsdLogger.MaximumFileSize, fixture.RootElement.GetProperty("logMaximumFileSize").GetInt64());
    }

    [Theory]
    [InlineData(4, 2, false, 6)]
    [InlineData(4, 2, true, 4)]
    [InlineData(100, -5, false, 8)]
    [InlineData(-100, 5, false, 0)]
    [InlineData(7, 1, false, 8)]
    public void VerbosityIsAppliedBeforeClampingAndServiceIgnoresIt(int configured, int verbosity, bool service, int expected)
    {
        var config = RnsdConfigParser.Parse($"[logging]\nloglevel={configured}");
        Assert.Equal(expected, RnsdLogger.LevelFor(config, verbosity, service));
        Assert.Equal(3, RnsdLogger.LevelFor(RnsdConfigParser.Parse(""), verbosity, service));
    }

    [Fact]
    public void LoggerFiltersAndFormatsLocalTimestamps()
    {
        using var output = new StringWriter();
        var logger = new RnsdLogger(3, true, output, TextWriter.Null, clock: new FixedClock());
        logger.Log(4, "hidden");
        logger.Log(3, "shown");
        Assert.Equal("[2026-01-02 03:04:05] [Notice]   shown" + Environment.NewLine, output.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => logger.Log(9, "invalid"));
    }

    [Fact]
    public void ServiceLogRotatesAfterFiveMiBAndReplacesOnlyOneBackup()
    {
        var file = Path.Combine(_workspace, "logfile");
        File.WriteAllText(file + ".1", "old backup");
        using (var stream = File.Create(file)) stream.SetLength(RnsdLogger.MaximumFileSize);
        var logger = new RnsdLogger(8, false, TextWriter.Null, TextWriter.Null, file);
        logger.Log(7, "rotation boundary");
        Assert.False(File.Exists(file));
        Assert.True(new FileInfo(file + ".1").Length > RnsdLogger.MaximumFileSize);
        logger.Log(8, "next message");
        Assert.Equal("[Extra]    next message" + Environment.NewLine, File.ReadAllText(file));
        Assert.False(File.Exists(file + ".2"));
    }

    [Fact]
    public void LogWriteFailureReportsDiagnosticAndFallsBackPermanently()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var logger = new RnsdLogger(8, false, output, error, _workspace);
        logger.Log(1, "first");
        logger.Log(2, "second");
        Assert.Contains("first", output.ToString());
        Assert.Contains("second", output.ToString());
        Assert.Contains("Dumping future log events to console!", error.ToString());
        Assert.Single(error.ToString().Split(Environment.NewLine), line => line.Contains("Exception occurred"));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
