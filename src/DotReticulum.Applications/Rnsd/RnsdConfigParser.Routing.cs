using System.Globalization;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnsd;

/// <summary>Interface.py ingress/recursive-egress defaults; durations are seconds and frequencies are events per second.</summary>
public sealed record RnsdIngressControlConfig
{
    public bool Enabled { get; init; } = true;
    public bool EgressEnabled { get; init; }
    public int MaximumHeldAnnounces { get; init; } = 256;
    public double BurstHoldSeconds { get; init; } = 15;
    public double BurstFrequencyNew { get; init; } = 3;
    public double BurstFrequency { get; init; } = 10;
    public double PathRequestBurstFrequencyNew { get; init; } = 3;
    public double PathRequestBurstFrequency { get; init; } = 8;
    public double NewTimeSeconds { get; init; } = 7200;
    public double BurstPenaltySeconds { get; init; } = 15;
    public double HeldReleaseIntervalSeconds { get; init; } = 5;
    public double EgressPathRequestFrequency { get; init; } = 5;
}

public sealed record RnsdInterfaceRoutingConfig
{
    public int Gravity { get; init; }
    public double AnnounceCap { get; init; } = .02;
    public int? AnnounceRateTargetSeconds { get; init; }
    public int? AnnounceRateGrace { get; init; }
    public int? AnnounceRatePenaltySeconds { get; init; }
    public bool AnnouncesFromInternal { get; init; } = true;
    public bool? AnnouncesToInternal { get; init; }
    public bool RecursivePathRequests { get; init; }
    public RnsdIngressControlConfig Ingress { get; init; } = new();

    public InterfaceAnnounceOptions ToAnnounceOptions() => new()
    {
        Gravity = Gravity,
        AnnounceCap = AnnounceCap,
        AnnouncesFromInternal = AnnouncesFromInternal,
        AnnouncesToInternal = AnnouncesToInternal == true,
        RateLimit = AnnounceRateTargetSeconds is { } target
            ? new AnnounceRateLimit(TimeSpan.FromSeconds(target), AnnounceRateGrace ?? 0, TimeSpan.FromSeconds(AnnounceRatePenaltySeconds ?? 0)) : null,
        HeldAnnounceCapacity = Ingress.MaximumHeldAnnounces,
        RecursivePathRequests = RecursivePathRequests,
        Ingress = new InterfaceIngressOptions
        {
            Enabled = Ingress.Enabled,
            EgressEnabled = Ingress.EgressEnabled,
            BurstHold = TimeSpan.FromSeconds(Ingress.BurstHoldSeconds),
            BurstFrequencyNew = Ingress.BurstFrequencyNew,
            BurstFrequency = Ingress.BurstFrequency,
            PathRequestBurstFrequencyNew = Ingress.PathRequestBurstFrequencyNew,
            PathRequestBurstFrequency = Ingress.PathRequestBurstFrequency,
            NewTime = TimeSpan.FromSeconds(Ingress.NewTimeSeconds),
            BurstPenalty = TimeSpan.FromSeconds(Ingress.BurstPenaltySeconds),
            HeldReleaseInterval = TimeSpan.FromSeconds(Ingress.HeldReleaseIntervalSeconds),
            EgressPathRequestFrequency = Ingress.EgressPathRequestFrequency,
        },
    };
}

public sealed record RnsdRoutingConfig
{
    public int DefaultGravity { get; init; }
    public int DefaultAnnounceRateTargetSeconds { get; init; } = 3600;
    public int DefaultAnnounceRateGrace { get; init; } = 5;
    public int DefaultAnnounceRatePenaltySeconds { get; init; }
    public int InboundDataCapacity { get; init; } = 1024;
    public int InboundAnnounceCapacity { get; init; } = 128;
    public int InboundPathRequestCapacity { get; init; } = 128;
    public int InboundIngressLimitedCapacity { get; init; } = 8;
    public RnsdIngressControlConfig IngressDefaults { get; init; } = new();

    public InterfaceAnnounceOptions CreateInterfaceOptions(bool transportEnabled, int? gravity = null, bool? announcesToInternal = null) =>
        new RnsdInterfaceRoutingConfig
        {
            Gravity = gravity ?? DefaultGravity,
            AnnouncesToInternal = announcesToInternal,
            AnnounceRateTargetSeconds = transportEnabled ? DefaultAnnounceRateTargetSeconds : null,
            AnnounceRateGrace = transportEnabled ? DefaultAnnounceRateGrace : null,
            AnnounceRatePenaltySeconds = transportEnabled ? DefaultAnnounceRatePenaltySeconds : null,
            Ingress = IngressDefaults,
        }.ToAnnounceOptions();
}

public static partial class RnsdConfigParser
{
    private const string IngressKeys = "egress_control ic_max_held_announces ic_burst_hold ic_burst_freq_new ic_burst_freq ic_pr_burst_freq_new ic_pr_burst_freq ec_pr_freq ic_new_time ic_burst_penalty ic_held_release_interval";
    private const string RoutingInterfaceKeys = $"gravity announce_cap announce_rate_target announce_rate_grace announce_rate_penalty recursive_prs announces_from_internal announces_to_internal ingress_control {IngressKeys}";
    private const string RoutingGlobalKeys = $"default_gravity default_ar_target default_ar_grace default_ar_penalty qlen_in_data qlen_in_announce qlen_in_pr qlen_in_il {IngressKeys}";

    private static RnsdRoutingConfig ParseRouting(IReadOnlyDictionary<string, string> values)
    {
        var defaults = new RnsdRoutingConfig();
        int PositiveOrDefault(string key, int fallback) => RoutingInteger(values, key) is > 0 and var value ? value : fallback;
        int NonnegativeOrDefault(string key, int fallback) => RoutingInteger(values, key) is >= 0 and var value ? value : fallback;
        return defaults with
        {
            DefaultGravity = RoutingInteger(values, "default_gravity") ?? defaults.DefaultGravity,
            DefaultAnnounceRateTargetSeconds = PositiveOrDefault("default_ar_target", defaults.DefaultAnnounceRateTargetSeconds),
            // Upstream's _default_ar_grace uses `configured or DEFAULT_AR_GRACE`, including an explicit zero.
            DefaultAnnounceRateGrace = PositiveOrDefault("default_ar_grace", defaults.DefaultAnnounceRateGrace),
            DefaultAnnounceRatePenaltySeconds = NonnegativeOrDefault("default_ar_penalty", defaults.DefaultAnnounceRatePenaltySeconds),
            InboundDataCapacity = PositiveOrDefault("qlen_in_data", defaults.InboundDataCapacity),
            InboundAnnounceCapacity = PositiveOrDefault("qlen_in_announce", defaults.InboundAnnounceCapacity),
            InboundPathRequestCapacity = PositiveOrDefault("qlen_in_pr", defaults.InboundPathRequestCapacity),
            InboundIngressLimitedCapacity = PositiveOrDefault("qlen_in_il", defaults.InboundIngressLimitedCapacity),
            IngressDefaults = ParseIngress(values, defaults.IngressDefaults, global: true),
        };
    }

    internal static RnsdInterfaceRoutingConfig ResolveInterfaceRouting(RnsdInterfaceConfig config, RnsdRoutingConfig defaults, bool transport) =>
        ParseInterfaceRouting(config, defaults, transport);

    private static RnsdInterfaceRoutingConfig ParseInterfaceRouting(RnsdInterfaceConfig config, RnsdRoutingConfig defaults, bool transport)
    {
        var values = config.Options;
        var explicitTarget = RoutingInteger(values, "announce_rate_target");
        var explicitGrace = RoutingInteger(values, "announce_rate_grace");
        var explicitPenalty = RoutingInteger(values, "announce_rate_penalty");
        int? target = explicitTarget is > 0 ? explicitTarget : null;
        int? grace = explicitGrace is >= 0 ? explicitGrace : null;
        int? penalty = explicitPenalty is >= 0 ? explicitPenalty : null;
        if (target is not null) { grace ??= 0; penalty ??= 0; }
        if (transport)
        {
            target ??= defaults.DefaultAnnounceRateTargetSeconds;
            grace ??= defaults.DefaultAnnounceRateGrace;
            penalty ??= defaults.DefaultAnnounceRatePenaltySeconds;
        }
        var cap = RoutingDouble(values, "announce_cap");
        return new RnsdInterfaceRoutingConfig
        {
            Gravity = RoutingInteger(values, "gravity") ?? defaults.DefaultGravity,
            AnnounceCap = cap is > 0 and <= 100 ? cap.Value / 100 : .02,
            AnnounceRateTargetSeconds = target,
            AnnounceRateGrace = grace,
            AnnounceRatePenaltySeconds = penalty,
            AnnouncesFromInternal = RoutingBoolean(values, "announces_from_internal") ?? true,
            AnnouncesToInternal = RoutingBoolean(values, "announces_to_internal"),
            RecursivePathRequests = RoutingBoolean(values, "recursive_prs") ?? false,
            Ingress = ParseIngress(values, defaults.IngressDefaults, global: false),
        };
    }

    private static RnsdIngressControlConfig ParseIngress(IReadOnlyDictionary<string, string> values, RnsdIngressControlConfig defaults, bool global)
    {
        double Number(string key, double fallback)
        {
            var value = RoutingDouble(values, key);
            if (value is null) return fallback;
            if (global) return value > 0 ? value.Value : fallback;
            if (value < 0) throw new FormatException($"'{key}' must be nonnegative.");
            return value.Value;
        }
        var held = RoutingInteger(values, "ic_max_held_announces");
        if (!global && held < 0) throw new FormatException("'ic_max_held_announces' must be nonnegative.");
        return defaults with
        {
            Enabled = RoutingBoolean(values, "ingress_control") ?? defaults.Enabled,
            EgressEnabled = RoutingBoolean(values, "egress_control") ?? defaults.EgressEnabled,
            MaximumHeldAnnounces = global ? held is > 0 ? held.Value : defaults.MaximumHeldAnnounces : held ?? defaults.MaximumHeldAnnounces,
            BurstHoldSeconds = Number("ic_burst_hold", defaults.BurstHoldSeconds),
            BurstFrequencyNew = Number("ic_burst_freq_new", defaults.BurstFrequencyNew),
            BurstFrequency = Number("ic_burst_freq", defaults.BurstFrequency),
            PathRequestBurstFrequencyNew = Number("ic_pr_burst_freq_new", defaults.PathRequestBurstFrequencyNew),
            PathRequestBurstFrequency = Number("ic_pr_burst_freq", defaults.PathRequestBurstFrequency),
            NewTimeSeconds = Number("ic_new_time", defaults.NewTimeSeconds),
            BurstPenaltySeconds = Number("ic_burst_penalty", defaults.BurstPenaltySeconds),
            HeldReleaseIntervalSeconds = Number("ic_held_release_interval", defaults.HeldReleaseIntervalSeconds),
            EgressPathRequestFrequency = Number("ec_pr_freq", defaults.EgressPathRequestFrequency),
        };
    }

    private static int? RoutingInteger(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var text)) return null;
        if (!int.TryParse(Scalar(text), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"'{key}' must be an integer.");
        return value;
    }

    private static double? RoutingDouble(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var text)) return null;
        if (!double.TryParse(Scalar(text), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value >= TimeSpan.MaxValue.TotalSeconds)
            throw new FormatException($"'{key}' must be a finite number within the supported duration range.");
        return value;
    }

    private static bool? RoutingBoolean(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) ? ParseBool(text, key) : null;
}
