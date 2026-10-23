using System.Globalization;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnsd;

public enum RnsdInterfaceType
{
    TcpServer,
    TcpClient,
    Udp,
    Serial,
    Extension,
}

/// <param name="Name">The <c>[[Name]]</c> section title.</param>
/// <param name="Options">Every <c>key = value</c> of the section (keys lower-cased).</param>
public sealed record RnsdInterfaceConfig(string Name, RnsdInterfaceType Type, bool Enabled, InterfaceMode Mode, IReadOnlyDictionary<string, string> Options)
{
    public string TypeName { get; init; } = Type.ToString();
    public IReadOnlyDictionary<string, string> RawOptions { get; init; } = Options;
    public RnsdInterfaceDiscoveryConfig Discovery { get; init; } = new();
    public RnsdInterfaceRoutingConfig Routing { get; init; } = new();
    public bool BootstrapOnly { get; init; }
    public long? ConfiguredBitrate
    {
        get
        {
            if (Get("bitrate") is not { } text) return null;
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"Interface '{Name}': 'bitrate' must be an integer.");
            return value >= 5 ? value : null;
        }
    }
    public long Bitrate => ConfiguredBitrate ?? (Type == RnsdInterfaceType.Serial ? GetInt("speed", 9600, 1) : 10_000_000);
    public int? FixedMtu => GetInt("fixed_mtu", 0, 0, InterfaceAccessCode.MaximumPacketSize) is var value && value != 0
        ? value >= Packet.Mtu ? value : throw new FormatException($"Interface '{Name}': 'fixed_mtu' must be at least {Packet.Mtu}.")
        : null;
    public int? MaxReconnectAttempts => Get("max_reconnect_tries") is null ? null : GetInt("max_reconnect_tries", 0);
    public TimeSpan ConnectTimeout => TimeSpan.FromSeconds(GetInt("connect_timeout", 5, 1));
    public string? IfacNetworkName => IfacAlias("networkname", "network_name");
    public string? IfacPassphrase => IfacAlias("passphrase", "pass_phrase");
    public int IfacSize
    {
        get
        {
            var bits = GetInt("ifac_size", 0, int.MinValue, 512);
            return bits >= 8 ? bits / 8 : Type == RnsdInterfaceType.Serial ? 8 : 16;
        }
    }

    private string? IfacAlias(string first, string second)
    {
        static bool Present(string? value) => value is not (null or "" or "None");
        var primary = Get(first);
        var alias = Get(second);
        return Present(alias) ? alias : Present(primary) ? primary : null;
    }

    public InterfaceAccessCode? CreateAccessCode() => IfacNetworkName is null && IfacPassphrase is null
        ? null : InterfaceAccessCode.Derive(IfacNetworkName, IfacPassphrase, IfacSize);
    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : null;
    public IReadOnlyList<string> GetList(string key) =>
        RawOptions.TryGetValue(key, out var value) ? RnsdConfigParser.ParseList(value) : [];

    public int GetInt(string key, int fallback, int min = 0, int max = int.MaxValue)
    {
        if (Get(key) is not { } text) return fallback;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new FormatException($"Interface '{Name}': '{key}' must be an integer between {min} and {max}, got '{text}'.");
        return value;
    }
}

public sealed record RnsdConfig(bool EnableTransport, int LogLevel, IReadOnlyList<RnsdInterfaceConfig> Interfaces)
{
    public bool LogTimestamps { get; init; } = true;
    public bool HasConfiguredLogLevel { get; init; }
    public int ConfiguredLogLevel { get; init; } = LogLevel;
    public bool StaticTransportIdentity { get; init; }
    public string? SourcePath { get; init; }
    public SharedInstanceOptions SharedInstance { get; init; } = new();
    public DiscoveryOptions Discovery { get; init; } = new();
    public BlackholeOptions Blackholes { get; init; } = new();
    public RnsdRoutingConfig Routing { get; init; } = new();
    public string? NetworkIdentityPath { get; init; }
    public bool EnableRemoteManagement { get; init; }
    public bool EnableInterfaceManagement { get; init; } = true;
    public IReadOnlyList<byte[]> RemoteManagementAllowed { get; init; } = [];
    public IReadOnlyDictionary<string, string> ReticulumOptions { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> LoggingOptions { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Reads supported ConfigObj sections, scalar/list quoting and comments. Ineffective controls are rejected rather than silently ignored.
/// </summary>
public static partial class RnsdConfigParser
{
    private static readonly Dictionary<string, RnsdInterfaceType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TCPServerInterface"] = RnsdInterfaceType.TcpServer,
        ["TCPClientInterface"] = RnsdInterfaceType.TcpClient,
        ["UDPInterface"] = RnsdInterfaceType.Udp,
        ["SerialInterface"] = RnsdInterfaceType.Serial,
    };

    public static RnsdConfig Parse(string text, InterfaceFactoryRegistry? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reticulum = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var logging = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sections = new List<(string Name, Dictionary<string, string> Options)>();
        var topSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        Dictionary<string, string>? current = null;
        var lineNumber = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line[0] == '[')
            {
                var sub = line.StartsWith("[[", StringComparison.Ordinal);
                var open = sub ? 2 : 1;
                var close = line.IndexOf(sub ? "]]" : "]", StringComparison.Ordinal);
                if (close < open) throw new FormatException($"Line {lineNumber}: malformed section header '{line}'.");
                var name = line[open..close].Trim();
                if (name.Length == 0) throw new FormatException($"Line {lineNumber}: empty section name.");
                if (line[(close + open)..].Trim() is { Length: > 0 } suffix && !suffix.StartsWith('#'))
                    throw new FormatException($"Line {lineNumber}: unexpected text after section.");
                if (sub)
                {
                    if (!string.Equals(section, "interfaces", StringComparison.OrdinalIgnoreCase))
                        throw new FormatException($"Line {lineNumber}: subsection '[[{name}]]' is only valid inside [interfaces].");
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (sections.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        throw new FormatException($"Line {lineNumber}: duplicate interface '{name}'.");
                    sections.Add((name, current));
                }
                else
                {
                    section = name.ToLowerInvariant();
                    if (!topSections.Add(section)) throw new FormatException($"Line {lineNumber}: duplicate section '[{name}]'.");
                    current = section switch
                    {
                        "reticulum" => reticulum,
                        "logging" => logging,
                        "interfaces" => null,
                        _ => throw new FormatException($"Line {lineNumber}: unsupported section '[{name}]'."),
                    };
                }

                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) throw new FormatException($"Line {lineNumber}: expected 'key = value', got '{line}'.");
            if (current is null) throw new FormatException($"Line {lineNumber}: setting outside of any section.");
            var key = line[..eq].Trim().ToLowerInvariant();
            if (current.ContainsKey(key)) throw new FormatException($"Line {lineNumber}: duplicate setting '{key}'.");
            current[key] = StripComment(line[(eq + 1)..].Trim());
        }

        ValidateKeys(reticulum, "reticulum",
            $"enable_transport static_transport_identity share_instance instance_name shared_instance_type shared_instance_port instance_control_port rpc_key network_identity enable_remote_management enable_interface_management remote_management_allowed discover_interfaces required_discovery_value interface_discovery_sources autoconnect_discovered_interfaces autoconnect_unverified_implementations autoconnect_interface_mode autoconnect_interface_gravity autoconnect_announces_to_internal publish_blackhole blackhole_sources blackhole_update_interval {RoutingGlobalKeys}");
        ValidateKeys(logging, "logging", "loglevel logtimestamps");
        var enableTransport = reticulum.TryGetValue("enable_transport", out var et) && ParseBool(et, "enable_transport");
        var routing = ParseRouting(reticulum);
        var logLevel = 3;
        if (logging.TryGetValue("loglevel", out var ll))
        {
            if (!int.TryParse(Scalar(ll), NumberStyles.Integer, CultureInfo.InvariantCulture, out logLevel))
                throw new FormatException($"[logging] loglevel must be an integer, got '{ll}'.");
        }

        var interfaces = new List<RnsdInterfaceConfig>();
        foreach (var (name, options) in sections)
        {
            var enabled = (options.TryGetValue("interface_enabled", out var ie) && ParseBool(ie, "interface_enabled"))
                | (options.TryGetValue("enabled", out var en) && ParseBool(en, "enabled"));

            if (!options.TryGetValue("type", out var typeName))
            {
                if (!enabled) continue;
                throw new FormatException($"Interface '{name}' has no 'type'.");
            }
            typeName = Scalar(typeName);
            if (!Types.TryGetValue(typeName, out var type))
            {
                if (!enabled) continue;
                if (extensions?.Contains(typeName) != true)
                    throw new FormatException($"Interface '{name}': unsupported type '{typeName}'; no native factory registered (supported: {string.Join(", ", Types.Keys)}).");
                type = RnsdInterfaceType.Extension;
            }

            var mode = InterfaceMode.Full;
            var modeText = options.GetValueOrDefault("interface_mode") ?? options.GetValueOrDefault("mode");
            if (modeText is not null) mode = ParseMode(modeText, name);
            var scalars = options.ToDictionary(p => p.Key,
                p => type == RnsdInterfaceType.Extension && IsList(p.Value) ? p.Value : Scalar(p.Value), StringComparer.OrdinalIgnoreCase);
            if (enabled)
            {
                var typeKeys = type switch
                {
                    RnsdInterfaceType.TcpServer => "listen_ip listen_port port fixed_mtu",
                    RnsdInterfaceType.TcpClient => "target_host target_port fixed_mtu connect_timeout max_reconnect_tries",
                    RnsdInterfaceType.Udp => "listen_ip listen_port forward_ip forward_port port",
                    RnsdInterfaceType.Serial => "port speed",
                    _ => "",
                };
                if (type != RnsdInterfaceType.Extension)
                    ValidateKeys(options, $"interface '{name}'", $"type enabled interface_enabled mode interface_mode bootstrap_only bitrate networkname network_name passphrase pass_phrase ifac_size {typeKeys} {RnsdInterfaceDiscoveryConfig.AcceptedKeys} {RoutingInterfaceKeys}");
                if (type == RnsdInterfaceType.TcpServer)
                {
                    if (scalars.TryGetValue("port", out var port)) scalars["listen_port"] = port;
                    Require(scalars, name, "listen_port");
                }
                if (type == RnsdInterfaceType.TcpClient) { Require(scalars, name, "target_host"); Require(scalars, name, "target_port"); }
                if (type == RnsdInterfaceType.Udp)
                {
                    if (scalars.TryGetValue("port", out var port))
                    {
                        scalars.TryAdd("listen_port", port);
                        scalars.TryAdd("forward_port", port);
                    }
                    foreach (var key in new[] { "listen_ip", "listen_port", "forward_ip", "forward_port" }) Require(scalars, name, key);
                }
                if (type == RnsdInterfaceType.Serial) Require(scalars, name, "port");
                var cfg = new RnsdInterfaceConfig(name, type, enabled, mode, scalars);
                _ = cfg.ConfiguredBitrate;
                _ = cfg.IfacSize;
                if (type is RnsdInterfaceType.TcpClient or RnsdInterfaceType.TcpServer) _ = cfg.FixedMtu;
                if (type == RnsdInterfaceType.TcpClient) { _ = cfg.ConnectTimeout; _ = cfg.MaxReconnectAttempts; }
                foreach (var key in new[] { "listen_port", "target_port", "forward_port" })
                    if (scalars.ContainsKey(key)) _ = cfg.GetInt(key, 0, key == "listen_port" ? 0 : 1, 65535);
            }
            var configured = new RnsdInterfaceConfig(name, type, enabled, mode, scalars) { TypeName = typeName, RawOptions = options };
            if (enabled) configured = configured with { Routing = ParseInterfaceRouting(configured, routing, enableTransport) };
            var discovery = RnsdInterfaceDiscoveryConfig.Parse(configured);
            if (discovery.Enabled && !discovery.IgnoreConfigWarnings && mode is not (InterfaceMode.Gateway or InterfaceMode.AccessPoint or InterfaceMode.Internal))
                mode = InterfaceMode.Gateway;
            configured = configured with
            {
                Discovery = discovery,
                Mode = mode,
                BootstrapOnly = options.TryGetValue("bootstrap_only", out var bootstrap) && ParseBool(bootstrap, "bootstrap_only"),
            };
            if (enabled && type == RnsdInterfaceType.Extension) extensions!.Validate(configured);
            interfaces.Add(configured);
        }

        bool Bool(string key, bool fallback = false) => reticulum.TryGetValue(key, out var value) ? ParseBool(value, key) : fallback;
        int Integer(string key, int fallback, int min = 0, int max = int.MaxValue)
        {
            if (!reticulum.TryGetValue(key, out var value)) return fallback;
            if (!int.TryParse(Scalar(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
                throw new FormatException($"[reticulum] '{key}' must be an integer between {min} and {max}.");
            return number;
        }
        string? Value(string key) => reticulum.TryGetValue(key, out var value) ? Scalar(value) : null;
        var sharedType = Value("shared_instance_type")?.ToLowerInvariant();
        if (sharedType is not null && sharedType is not ("tcp" or "unix"))
            throw new FormatException("shared_instance_type must be tcp or unix.");
        var instanceName = Value("instance_name") ?? "default";
        if (instanceName.Length == 0 || instanceName.IndexOfAny(['\0', '/', '\\']) >= 0)
            throw new FormatException("instance_name must be a nonempty socket name without path separators.");
        var interval = 60d;
        if (reticulum.TryGetValue("blackhole_update_interval", out var intervalText)
            && (!double.TryParse(Scalar(intervalText), NumberStyles.Float, CultureInfo.InvariantCulture, out interval)
                || !double.IsFinite(interval) || interval >= TimeSpan.MaxValue.TotalMinutes))
            throw new FormatException("blackhole_update_interval must be a finite number of minutes.");
        var autoMode = Value("autoconnect_interface_mode");
        if (autoMode is not null) _ = ParseMode(autoMode, "autoconnect_interface_mode");
        return new RnsdConfig(enableTransport, Math.Clamp(logLevel, 0, 8), interfaces)
        {
            HasConfiguredLogLevel = logging.ContainsKey("loglevel"),
            ConfiguredLogLevel = logLevel,
            StaticTransportIdentity = Bool("static_transport_identity") || interfaces.Any(i => i.Discovery.Enabled),
            LogTimestamps = !logging.TryGetValue("logtimestamps", out var timestamps) || ParseBool(timestamps, "logtimestamps"),
            ReticulumOptions = reticulum,
            Routing = routing,
            LoggingOptions = logging,
            NetworkIdentityPath = Value("network_identity"),
            EnableRemoteManagement = Bool("enable_remote_management"),
            EnableInterfaceManagement = Bool("enable_interface_management", true),
            RemoteManagementAllowed = Hashes(reticulum, "remote_management_allowed"),
            SharedInstance = new SharedInstanceOptions
            {
                Enabled = Bool("share_instance", true),
                InstancePort = Integer("shared_instance_port", 37428, 1, 65535),
                ControlPort = Integer("instance_control_port", 37429, 1, 65535),
                SocketPath = sharedType == "tcp" || !OperatingSystem.IsLinux() ? null : instanceName,
                RpcKey = Value("rpc_key") is { } rpc ? Hex(rpc, "rpc_key") : null,
            },
            Discovery = new DiscoveryOptions
            {
                Enabled = Bool("discover_interfaces"),
                RequiredStampValue = Integer("required_discovery_value", 16, int.MinValue, 256) is > 0 and var required ? required : 16,
                AllowedSources = Hashes(reticulum, "interface_discovery_sources"),
                MaxAutoConnected = Math.Max(0, Integer("autoconnect_discovered_interfaces", 0, int.MinValue, 1024)),
                AllowUnverifiedImplementations = Bool("autoconnect_unverified_implementations"),
                InterfaceMode = autoMode?.ToLowerInvariant(),
                InterfaceGravity = Integer("autoconnect_interface_gravity", 0, int.MinValue),
                AnnouncesToInternal = reticulum.ContainsKey("autoconnect_announces_to_internal") ? Bool("autoconnect_announces_to_internal") : null,
            },
            Blackholes = new BlackholeOptions
            {
                Publish = Bool("publish_blackhole"),
                Sources = Hashes(reticulum, "blackhole_sources"),
                UpdateInterval = TimeSpan.FromMinutes(Math.Max(2, interval)),
            },
        };
    }

    public static bool ParseBool(string text, string key) => Scalar(text).ToLowerInvariant() switch
    {
        "yes" or "true" or "1" or "on" => true,
        "no" or "false" or "0" or "off" => false,
        _ => throw new FormatException($"'{key}' must be yes or no, got '{text}'."),
    };

    private static InterfaceMode ParseMode(string text, string name) => Scalar(text).ToLowerInvariant() switch
    {
        "full" => InterfaceMode.Full,
        "pointtopoint" or "point_to_point" or "ptp" => InterfaceMode.PointToPoint,
        "access_point" or "accesspoint" or "ap" => InterfaceMode.AccessPoint,
        "roaming" => InterfaceMode.Roaming,
        "boundary" => InterfaceMode.Boundary,
        "gateway" or "gw" => InterfaceMode.Gateway,
        "internal" => InterfaceMode.Internal,
        _ => throw new FormatException($"Interface '{name}': unknown mode '{text}'."),
    };

    private static void Require(Dictionary<string, string> options, string name, string key)
    {
        if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new FormatException($"Interface '{name}': '{key}' is required; no implicit port/address default.");
    }

    private static void ValidateKeys(Dictionary<string, string> values, string section, string supported)
    {
        var keys = supported.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in values.Keys)
            if (!keys.Contains(key)) throw new FormatException($"[{section}] '{key}' is unsupported by this native runtime; it would have no effect.");
    }

    private static byte[] Hex(string value, string key)
    {
        try { return Convert.FromHexString(value); }
        catch (FormatException ex) { throw new FormatException($"'{key}' must contain hexadecimal bytes.", ex); }
    }

    private static IReadOnlyList<byte[]> Hashes(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? ParseList(value).Distinct(StringComparer.OrdinalIgnoreCase).Select(hash =>
        {
            if (hash.Length != 32) throw new FormatException($"'{key}' requires 32 hexadecimal characters per identity.");
            return Hex(hash, key);
        }).ToArray() : [];

    private static string StripComment(string value)
    {
        char quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '#') return value[..i].TrimEnd();
        }
        if (quote != '\0') throw new FormatException("Unterminated quoted setting.");
        return value;
    }

    public static IReadOnlyList<string> ParseList(string value)
    {
        if (value.Trim() == ",") return [];
        var result = new List<string>();
        char quote = '\0';
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == ',')
            {
                var item = value[start..i].Trim();
                if (item.Length == 0) throw new FormatException("Empty item in configuration list.");
                result.Add(Unquote(item));
                start = i + 1;
            }
        }
        if (quote != '\0') throw new FormatException("Unterminated quoted list.");
        if (start < value.Length && value[start..].Trim() is { Length: > 0 } last) result.Add(Unquote(last));
        return result;
    }

    private static string Scalar(string value)
    {
        if (IsList(value)) throw new FormatException("Expected a scalar setting, not a list.");
        var parts = ParseList(value);
        if (parts.Count > 1) throw new FormatException("Expected a scalar setting, not a list.");
        return parts.Count == 0 ? "" : parts[0];
    }

    private static bool IsList(string value)
    {
        char quote = '\0';
        foreach (var c in value)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == ',') return true;
        }
        return false;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'' ? value[1..^1] : value;
}
