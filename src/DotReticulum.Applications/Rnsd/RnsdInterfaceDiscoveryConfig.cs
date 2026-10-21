using System.Globalization;

namespace DotReticulum.Applications.Rnsd;

public sealed record RnsdInterfaceDiscoveryConfig
{
    public bool Enabled { get; init; }
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromHours(6);
    public int StampValue { get; init; } = 16;
    public string? Name { get; init; }
    public bool Encrypt { get; init; }
    public string? ReachableOn { get; init; }
    public bool PublishIfac { get; init; }
    public string? LocationCommand { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? Height { get; init; }
    public int? Frequency { get; init; }
    public int? Bandwidth { get; init; }
    public int? Modulation { get; init; }
    public byte[]? LxmfAddress { get; init; }
    public bool IgnoreConfigWarnings { get; init; }

    internal const string AcceptedKeys =
        "discoverable announce_interval discovery_stamp_value discovery_name discovery_encrypt reachable_on publish_ifac location_cmd latitude longitude height discovery_frequency discovery_bandwidth discovery_modulation discovery_lxmf_address ignore_config_warnings";

    internal static RnsdInterfaceDiscoveryConfig Parse(RnsdInterfaceConfig configuration)
    {
        bool Bool(string key) => configuration.Get(key) is { } text && RnsdConfigParser.ParseBool(text, key);
        double? Number(string key)
        {
            if (configuration.Get(key) is not { } text) return null;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new FormatException($"Interface '{configuration.Name}': '{key}' must be finite numeric data.");
            return value;
        }
        int? Integer(string key) => configuration.Get(key) is null ? null : configuration.GetInt(key, 0, int.MinValue);
        var intervalMinutes = Integer("announce_interval") ?? 360;
        var stampValue = Integer("discovery_stamp_value") ?? 16;
        if (stampValue <= 0) stampValue = 16;
        if (stampValue > 256) throw new FormatException($"Interface '{configuration.Name}': discovery_stamp_value exceeds the native maximum 256.");
        byte[]? address = null;
        if (configuration.Get("discovery_lxmf_address") is { } addressText)
        {
            if (addressText.Length != 32)
                throw new FormatException($"Interface '{configuration.Name}': discovery_lxmf_address must be 32 hexadecimal characters.");
            try { address = Convert.FromHexString(addressText); }
            catch (FormatException ex) { throw new FormatException($"Interface '{configuration.Name}': discovery_lxmf_address must be hexadecimal.", ex); }
        }
        return new RnsdInterfaceDiscoveryConfig
        {
            Enabled = Bool("discoverable"),
            AnnounceInterval = TimeSpan.FromMinutes(Math.Max(5, intervalMinutes)),
            StampValue = stampValue,
            Name = configuration.Get("discovery_name"),
            Encrypt = Bool("discovery_encrypt"),
            ReachableOn = configuration.Get("reachable_on"),
            PublishIfac = Bool("publish_ifac"),
            LocationCommand = configuration.Get("location_cmd"),
            Latitude = Number("latitude"),
            Longitude = Number("longitude"),
            Height = Number("height"),
            Frequency = Integer("discovery_frequency"),
            Bandwidth = Integer("discovery_bandwidth"),
            Modulation = Integer("discovery_modulation"),
            LxmfAddress = address,
            IgnoreConfigWarnings = Bool("ignore_config_warnings"),
        };
    }
}
