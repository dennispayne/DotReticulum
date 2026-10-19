using System.Globalization;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnsd;

public enum RnsdInterfaceType
{
    TcpServer,
    TcpClient,
    Udp,
    Serial,
}

/// <param name="Name">The <c>[[Name]]</c> section title.</param>
/// <param name="Options">Every <c>key = value</c> of the section (keys lower-cased).</param>
public sealed record RnsdInterfaceConfig(string Name, RnsdInterfaceType Type, bool Enabled, InterfaceMode Mode, IReadOnlyDictionary<string, string> Options)
{
    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : null;

    public int GetInt(string key, int fallback, int min = 0, int max = int.MaxValue)
    {
        if (Get(key) is not { } text) return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new FormatException($"Interface '{Name}': '{key}' must be an integer between {min} and {max}, got '{text}'.");
        return value;
    }
}

public sealed record RnsdConfig(bool EnableTransport, int LogLevel, IReadOnlyList<RnsdInterfaceConfig> Interfaces);

/// <summary>
/// Reads the small subset of Reticulum's ConfigObj ini format that <c>rnsd</c> understands (see <c>docs/rnsd.md</c>): <c>[reticulum]</c>
/// (<c>enable_transport</c>), <c>[logging]</c> (<c>loglevel</c>) and <c>[interfaces]</c> with <c>[[Name]]</c> subsections. Other known upstream
/// keys are ignored; unsupported interface types are an error when enabled.
/// </summary>
public static class RnsdConfigParser
{
    private static readonly Dictionary<string, RnsdInterfaceType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TCPServerInterface"] = RnsdInterfaceType.TcpServer,
        ["TCPClientInterface"] = RnsdInterfaceType.TcpClient,
        ["UDPInterface"] = RnsdInterfaceType.Udp,
        ["SerialInterface"] = RnsdInterfaceType.Serial,
    };

    public static RnsdConfig Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reticulum = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var logging = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sections = new List<(string Name, Dictionary<string, string> Options)>();
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
                if (sub)
                {
                    if (!string.Equals(section, "interfaces", StringComparison.OrdinalIgnoreCase))
                        throw new FormatException($"Line {lineNumber}: subsection '[[{name}]]' is only valid inside [interfaces].");
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections.Add((name, current));
                }
                else
                {
                    section = name.ToLowerInvariant();
                    current = section switch { "reticulum" => reticulum, "logging" => logging, _ => new Dictionary<string, string>() };
                }

                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) throw new FormatException($"Line {lineNumber}: expected 'key = value', got '{line}'.");
            if (current is null) throw new FormatException($"Line {lineNumber}: setting outside of any section.");
            current[line[..eq].Trim().ToLowerInvariant()] = Unquote(StripComment(line[(eq + 1)..].Trim()));
        }

        var enableTransport = reticulum.TryGetValue("enable_transport", out var et) && ParseBool(et, "enable_transport");
        var logLevel = 4;
        if (logging.TryGetValue("loglevel", out var ll))
        {
            if (!int.TryParse(ll, NumberStyles.None, CultureInfo.InvariantCulture, out logLevel) || logLevel > 7)
                throw new FormatException($"[logging] loglevel must be 0-7, got '{ll}'.");
        }

        var interfaces = new List<RnsdInterfaceConfig>();
        foreach (var (name, options) in sections)
        {
            var enabled = true;
            if (options.TryGetValue("interface_enabled", out var ie)) enabled = ParseBool(ie, "interface_enabled");
            if (options.TryGetValue("enabled", out var en)) enabled = ParseBool(en, "enabled");

            if (!options.TryGetValue("type", out var typeName)) throw new FormatException($"Interface '{name}' has no 'type'.");
            if (!Types.TryGetValue(typeName, out var type))
            {
                if (!enabled) continue;
                throw new FormatException($"Interface '{name}': unsupported type '{typeName}' (supported: {string.Join(", ", Types.Keys)}).");
            }

            var mode = InterfaceMode.Full;
            var modeText = options.GetValueOrDefault("interface_mode") ?? options.GetValueOrDefault("mode");
            if (modeText is not null) mode = ParseMode(modeText, name);
            interfaces.Add(new RnsdInterfaceConfig(name, type, enabled, mode, options));
        }

        return new RnsdConfig(enableTransport, logLevel, interfaces);
    }

    public static bool ParseBool(string text, string key) => text.ToLowerInvariant() switch
    {
        "yes" or "true" or "1" or "on" => true,
        "no" or "false" or "0" or "off" => false,
        _ => throw new FormatException($"'{key}' must be yes or no, got '{text}'."),
    };

    private static InterfaceMode ParseMode(string text, string name) => text.ToLowerInvariant() switch
    {
        "full" => InterfaceMode.Full,
        "point_to_point" or "ptp" => InterfaceMode.PointToPoint,
        "access_point" or "accesspoint" or "ap" => InterfaceMode.AccessPoint,
        "roaming" => InterfaceMode.Roaming,
        "boundary" => InterfaceMode.Boundary,
        "gateway" or "gw" => InterfaceMode.Gateway,
        "internal" => InterfaceMode.Internal,
        _ => throw new FormatException($"Interface '{name}': unknown mode '{text}'."),
    };

    private static string StripComment(string value)
    {
        if (value.Length > 0 && value[0] is '"' or '\'') return value;
        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 ? value[..hash].TrimEnd() : value;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'' ? value[1..^1] : value;
}
