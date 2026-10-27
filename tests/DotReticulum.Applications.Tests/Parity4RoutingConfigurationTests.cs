using System.Globalization;
using System.Text.Json;
using DotReticulum.Applications.Rnsd;

namespace DotReticulum.Applications.Tests;

// Executed reference decisions: RNS1.5.7 a29bfb51c468945c96a1ee22cae0114e993e2391,
// Reticulum.py881-971. Reproduce with GenerateParity4RoutingConfiguration.py.
[Trait("Category", "Unit")]
public sealed class Parity4RoutingConfigurationTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Parity4RoutingConfiguration-vectors.json"))).RootElement;

    public static IEnumerable<object[]> Cases() =>
        Vectors.GetProperty("cases").EnumerateArray().Select((_, index) => new object[] { index });

    [Theory]
    [MemberData(nameof(Cases))]
    public void InterfaceControlsMatchExecutedSource(int index)
    {
        var vector = Vectors.GetProperty("cases")[index];
        var expected = vector.GetProperty("expected");
        var config = Parse(vector.GetProperty("settings").GetString()!, vector.GetProperty("transport").GetBoolean());
        var routing = Assert.Single(config.Interfaces).Routing;
        Assert.Equal(expected.GetProperty("gravity").GetInt32(), routing.Gravity);
        Assert.Equal(expected.GetProperty("announce_cap").GetDouble(), routing.AnnounceCap);
        Assert.Equal(NullableInt(expected, "announce_rate_target"), routing.AnnounceRateTargetSeconds);
        Assert.Equal(NullableInt(expected, "announce_rate_grace"), routing.AnnounceRateGrace);
        Assert.Equal(NullableInt(expected, "announce_rate_penalty"), routing.AnnounceRatePenaltySeconds);
        Assert.Equal(expected.GetProperty("announces_from_internal").GetBoolean(), routing.AnnouncesFromInternal);
        Assert.Equal(NullableBool(expected, "announces_to_internal"), routing.AnnouncesToInternal);
        Assert.Equal(expected.GetProperty("recursive_prs").GetBoolean(), routing.RecursivePathRequests);
        Assert.Equal(expected.GetProperty("ingress_control").GetBoolean(), routing.Ingress.Enabled);
        Assert.Equal(NullableBool(expected, "egress_control") ?? false, routing.Ingress.EgressEnabled);
        Assert.Equal(NullableInt(expected, "ic_max_held_announces") ?? 256, routing.Ingress.MaximumHeldAnnounces);
        Assert.Equal(NullableDouble(expected, "ic_burst_hold") ?? 15, routing.Ingress.BurstHoldSeconds);
        Assert.Equal(NullableDouble(expected, "ic_burst_freq_new") ?? 3, routing.Ingress.BurstFrequencyNew);
        Assert.Equal(NullableDouble(expected, "ic_burst_freq") ?? 10, routing.Ingress.BurstFrequency);
        Assert.Equal(NullableDouble(expected, "ic_pr_burst_freq_new") ?? 3, routing.Ingress.PathRequestBurstFrequencyNew);
        Assert.Equal(NullableDouble(expected, "ic_pr_burst_freq") ?? 8, routing.Ingress.PathRequestBurstFrequency);
        Assert.Equal(NullableDouble(expected, "ic_new_time") ?? 7200, routing.Ingress.NewTimeSeconds);
        Assert.Equal(NullableDouble(expected, "ic_burst_penalty") ?? 15, routing.Ingress.BurstPenaltySeconds);
        Assert.Equal(NullableDouble(expected, "ic_held_release_interval") ?? 5, routing.Ingress.HeldReleaseIntervalSeconds);
        Assert.Equal(NullableDouble(expected, "ec_pr_freq") ?? 5, routing.Ingress.EgressPathRequestFrequency);
        var options = routing.ToAnnounceOptions();
        Assert.Equal(routing.Gravity, options.Gravity);
        Assert.Equal(routing.AnnounceCap, options.AnnounceCap);
        Assert.Equal(routing.AnnouncesFromInternal, options.AnnouncesFromInternal);
        Assert.Equal(routing.AnnouncesToInternal == true, options.AnnouncesToInternal);
        Assert.Equal(routing.Ingress.MaximumHeldAnnounces, options.HeldAnnounceCapacity);
        Assert.Equal(routing.RecursivePathRequests, options.RecursivePathRequests);
        Assert.Equal(routing.Ingress.Enabled, options.Ingress.Enabled);
        Assert.Equal(routing.Ingress.EgressEnabled, options.Ingress.EgressEnabled);
        Assert.Equal(TimeSpan.FromSeconds(routing.Ingress.BurstHoldSeconds), options.Ingress.BurstHold);
        Assert.Equal(routing.Ingress.BurstFrequencyNew, options.Ingress.BurstFrequencyNew);
        Assert.Equal(routing.Ingress.BurstFrequency, options.Ingress.BurstFrequency);
        Assert.Equal(routing.Ingress.PathRequestBurstFrequencyNew, options.Ingress.PathRequestBurstFrequencyNew);
        Assert.Equal(routing.Ingress.PathRequestBurstFrequency, options.Ingress.PathRequestBurstFrequency);
        Assert.Equal(TimeSpan.FromSeconds(routing.Ingress.NewTimeSeconds), options.Ingress.NewTime);
        Assert.Equal(TimeSpan.FromSeconds(routing.Ingress.BurstPenaltySeconds), options.Ingress.BurstPenalty);
        Assert.Equal(TimeSpan.FromSeconds(routing.Ingress.HeldReleaseIntervalSeconds), options.Ingress.HeldReleaseInterval);
        Assert.Equal(routing.Ingress.EgressPathRequestFrequency, options.Ingress.EgressPathRequestFrequency);
        Assert.Null(options.IngressLimitAnnounces);
        Assert.Null(options.IngressLimitPathRequests);
        Assert.Null(options.EgressLimitPathRequests);
        if (routing.AnnounceRateTargetSeconds is { } target)
        {
            Assert.NotNull(options.RateLimit);
            Assert.Equal(TimeSpan.FromSeconds(target), options.RateLimit.Target);
            Assert.Equal(routing.AnnounceRateGrace ?? 0, options.RateLimit.Grace);
            Assert.Equal(TimeSpan.FromSeconds(routing.AnnounceRatePenaltySeconds ?? 0), options.RateLimit.Penalty);
        }
        else Assert.Null(options.RateLimit);
    }

    [Fact]
    public void GlobalDefaultsMergeBeforePerInterfaceOverridesAndQueueBoundsRemainIndependent()
    {
        var config = RnsdConfigParser.Parse("""
            [reticulum]
              enable_transport = yes
              default_gravity = -8
              default_ar_target = 20
              default_ar_grace = 7
              default_ar_penalty = 30
              qlen_in_data = 100
              qlen_in_announce = 11
              qlen_in_pr = 12
              qlen_in_il = 2
              egress_control = yes
              ic_max_held_announces = 10
              ic_burst_freq = 25
              ic_new_time = 50
            [interfaces]
              [[defaults]]
                type = TCPClientInterface
                enabled = yes
                target_host = localhost
                target_port = 4242
              [[overrides]]
                type = TCPClientInterface
                enabled = yes
                target_host = localhost
                target_port = 4243
                gravity = 9
                announce_rate_target = 15
                ingress_control = no
                egress_control = no
                ic_max_held_announces = 0
                ic_burst_freq = 0
            """);
        Assert.Equal(100, config.Routing.InboundDataCapacity);
        Assert.Equal(11, config.Routing.InboundAnnounceCapacity);
        Assert.Equal(12, config.Routing.InboundPathRequestCapacity);
        Assert.Equal(2, config.Routing.InboundIngressLimitedCapacity);
        var defaults = config.Interfaces[0].Routing;
        Assert.Equal(-8, defaults.Gravity);
        Assert.Equal(20, defaults.AnnounceRateTargetSeconds);
        Assert.Equal(7, defaults.AnnounceRateGrace);
        Assert.Equal(30, defaults.AnnounceRatePenaltySeconds);
        Assert.True(defaults.Ingress.EgressEnabled);
        Assert.Equal(25, defaults.Ingress.BurstFrequency);
        var overrides = config.Interfaces[1].Routing;
        Assert.Equal(9, overrides.Gravity);
        Assert.Equal(15, overrides.AnnounceRateTargetSeconds);
        Assert.Equal(0, overrides.AnnounceRateGrace);
        Assert.Equal(0, overrides.AnnounceRatePenaltySeconds);
        Assert.False(overrides.Ingress.Enabled);
        Assert.False(overrides.Ingress.EgressEnabled);
        Assert.Equal(0, overrides.Ingress.MaximumHeldAnnounces);
        Assert.Equal(0, overrides.Ingress.BurstFrequency);
        Assert.Equal(50, overrides.Ingress.NewTimeSeconds);
    }

    [Fact]
    public void SourceGlobalFalsyDefaultsDifferFromExplicitPerInterfaceZero()
    {
        var config = Parse("announce_rate_grace=0\nic_burst_hold=0\nic_held_release_interval=0", true,
            "default_ar_target=0\ndefault_ar_grace=0\nic_max_held_announces=0\nic_burst_hold=0\nqlen_in_data=0");
        Assert.Equal(3600, config.Routing.DefaultAnnounceRateTargetSeconds);
        Assert.Equal(5, config.Routing.DefaultAnnounceRateGrace);
        Assert.Equal(256, config.Routing.IngressDefaults.MaximumHeldAnnounces);
        Assert.Equal(15, config.Routing.IngressDefaults.BurstHoldSeconds);
        Assert.Equal(1024, config.Routing.InboundDataCapacity);
        var configured = Assert.Single(config.Interfaces).Routing;
        Assert.Equal(0, configured.AnnounceRateGrace);
        Assert.Equal(0, configured.Ingress.BurstHoldSeconds);
        Assert.Equal(0, configured.Ingress.HeldReleaseIntervalSeconds);
    }

    [Fact]
    public void DiscoveredOptionsFactoryRetainsOperationalGlobalControlsWhileOverridingGravityAndInternalFlag()
    {
        var config = Parse("", true, "default_ar_target=10\nic_burst_hold=2\negress_control=yes");
        var options = config.Routing.CreateInterfaceOptions(transportEnabled: true, gravity: -2, announcesToInternal: true);
        Assert.Equal(-2, options.Gravity);
        Assert.True(options.AnnouncesToInternal);
        Assert.NotNull(options.RateLimit);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RateLimit.Target);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Ingress.BurstHold);
        Assert.True(options.Ingress.EgressEnabled);
        Assert.True(options.Ingress.Enabled);
        Assert.Null(options.IngressLimitAnnounces);
        Assert.Null(config.Routing.CreateInterfaceOptions(false).RateLimit);
    }

    [Theory]
    [InlineData("gravity=NaN")]
    [InlineData("announce_cap=NaN")]
    [InlineData("announce_cap=Infinity")]
    [InlineData("announce_rate_target=1.5")]
    [InlineData("announce_rate_grace=invalid")]
    [InlineData("announces_from_internal=maybe")]
    [InlineData("announces_to_internal=maybe")]
    [InlineData("recursive_prs=maybe")]
    [InlineData("ingress_control=maybe")]
    [InlineData("egress_control=maybe")]
    [InlineData("ic_max_held_announces=-1")]
    [InlineData("ic_burst_hold=-1")]
    [InlineData("ic_burst_freq=NaN")]
    [InlineData("ic_new_time=1e100")]
    public void MalformedAndUnboundedInterfaceValuesAreDiagnosed(string settings) =>
        Assert.Throws<FormatException>(() => Parse(settings));

    [Theory]
    [InlineData("qlen_in_data=1.2")]
    [InlineData("default_gravity=Infinity")]
    [InlineData("default_ar_target=invalid")]
    [InlineData("egress_control=maybe")]
    [InlineData("ic_new_time=NaN")]
    public void MalformedGlobalControlsAreDiagnosed(string settings) =>
        Assert.Throws<FormatException>(() => Parse("", globals: settings));

    [Fact]
    public void NontransportDoesNotInheritTransportRateTargetAndIfacConfigurationIsPreserved()
    {
        var config = Parse("network_name=synthetic\npass_phrase=synthetic-secret\nifac_size=64\nannounce_cap=25", globals: "default_ar_target=50");
        var iface = Assert.Single(config.Interfaces);
        Assert.Null(iface.Routing.AnnounceRateTargetSeconds);
        Assert.Null(iface.Routing.ToAnnounceOptions().RateLimit);
        Assert.Equal(.25, iface.Routing.AnnounceCap);
        Assert.Equal("synthetic", iface.IfacNetworkName);
        Assert.Equal("synthetic-secret", iface.IfacPassphrase);
        Assert.Equal(8, iface.IfacSize);
        using var accessCode = iface.CreateAccessCode();
        Assert.NotNull(accessCode);
    }

    [Fact]
    public void FloatingSettingsAreInvariantAndDisabledInterfacesDoNotValidateInactiveRouting()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(.00125, Assert.Single(Parse("announce_cap=.125").Interfaces).Routing.AnnounceCap);
            Assert.Equal(2.5, Assert.Single(Parse("ic_burst_hold=2.5").Interfaces).Routing.Ingress.BurstHoldSeconds);
        }
        finally { CultureInfo.CurrentCulture = previous; }
        var disabled = RnsdConfigParser.Parse("""
            [interfaces]
              [[inactive]]
                type=TCPClientInterface
                enabled=no
                gravity=invalid
                ic_burst_hold=-1
            """);
        Assert.False(Assert.Single(disabled.Interfaces).Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitAttachOfDisabledEntryResolvesGlobalAndConfiguredOperationalRouting(bool overridden)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "parity4-routing-disabled-" + Guid.NewGuid().ToString("N"));
        var settings = overridden ? """
            gravity=22
            announce_cap=25
            announce_rate_target=8
            announces_to_internal=yes
            ingress_control=no
            ic_burst_freq_new=2.5
            ic_max_held_announces=3
            """ : "";
        var config = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance=no
              enable_transport=yes
              default_gravity=11
              default_ar_target=40
              default_ar_grace=6
              default_ar_penalty=7
              ic_burst_freq=9
              ic_max_held_announces=14
              egress_control=yes
            [interfaces]
              [[disabled]]
                type=TCPServerInterface
                enabled=no
                listen_ip=127.0.0.1
                listen_port=0
            {settings}
            """);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var host = RnsdHost.Create(config, Path.Combine(root, "identity"), (_, _) => { });
            await host.StartAsync(stop.Token);
            Assert.Empty(host.Interfaces);
            Assert.True(await host.AttachInterfaceAsync("disabled", cancellationToken: stop.Token));
            var attached = Assert.Single(host.Interfaces).Interface;
            var options = host.Router.GetInterfaceAnnounceOptions(attached);
            Assert.Equal(overridden ? 22 : 11, options.Gravity);
            Assert.Equal(overridden ? .25 : .02, options.AnnounceCap);
            Assert.Equal(overridden, options.AnnouncesToInternal);
            Assert.NotNull(options.RateLimit);
            Assert.Equal(TimeSpan.FromSeconds(overridden ? 8 : 40), options.RateLimit.Target);
            Assert.Equal(overridden ? 0 : 6, options.RateLimit.Grace);
            Assert.Equal(TimeSpan.FromSeconds(overridden ? 0 : 7), options.RateLimit.Penalty);
            Assert.Equal(!overridden, options.Ingress.Enabled);
            Assert.True(options.Ingress.EgressEnabled);
            Assert.Equal(9, options.Ingress.BurstFrequency);
            Assert.Equal(overridden ? 2.5 : 3, options.Ingress.BurstFrequencyNew);
            Assert.Equal(overridden ? 3 : 14, options.HeldAnnounceCapacity);
            Assert.True(await host.ReloadInterfaceAsync("disabled", cancellationToken: stop.Token));
            var replacement = Assert.Single(host.Interfaces).Interface;
            Assert.NotSame(attached, replacement);
            Assert.Equal(options, host.Router.GetInterfaceAnnounceOptions(replacement));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static int? NullableInt(JsonElement value, string key) => value.GetProperty(key).ValueKind == JsonValueKind.Null
        ? null : value.GetProperty(key).GetInt32();
    private static double? NullableDouble(JsonElement value, string key) => value.GetProperty(key).ValueKind == JsonValueKind.Null
        ? null : value.GetProperty(key).GetDouble();
    private static bool? NullableBool(JsonElement value, string key) => value.GetProperty(key).ValueKind == JsonValueKind.Null
        ? null : value.GetProperty(key).GetBoolean();

    private static RnsdConfig Parse(string settings, bool transport = false, string globals = "") => RnsdConfigParser.Parse($"""
        [reticulum]
          enable_transport={(transport ? "yes" : "no")}
        {globals}
        [interfaces]
          [[fixture]]
            type=TCPClientInterface
            enabled=yes
            target_host=localhost
            target_port=4242
        {settings}
        """);
}
