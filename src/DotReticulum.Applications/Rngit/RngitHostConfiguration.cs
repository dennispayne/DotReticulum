using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

/// <summary>
/// Bounded flat/list ConfigObj subset for the pinned rngit server. Identity aliases
/// here identify peers, not repository destinations from client_config. Nested
/// sections, interpolation and multiline values are explicitly unsupported.
/// </summary>
public sealed class RngitHostConfiguration
{
    private const int MaxBytes = 1024 * 1024;
    private const string DefaultConfig = """
        [rngit]
        announce_interval = 360

        [repositories]
        internal = /path/to/directory/with/git/repositories
        public = /another/path/to/directory/with/git/repositories
        showcase = /another/path/to/directory/with/git/repositories

        [aliases]

        [access]
        public = r:all, w:9710b86ba12c42d1d8f30f74fe509286
        internal = rw:9710b86ba12c42d1d8f30f74fe509286

        [pages]

        [logging]
        loglevel = 4

        """;
    private static readonly FrozenSet<string> ReservedAliases =
        new[] { "n", "none", "nobody", "a", "all", "everyone" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly Action<string>? diagnostic;

    private RngitHostConfiguration(string directory, Action<string>? diagnostic)
    {
        DirectoryPath = directory;
        this.diagnostic = diagnostic;
    }

    public string DirectoryPath { get; }
    public string ConfigPath => Path.Combine(DirectoryPath, "config");
    public string IdentityPath => Path.Combine(DirectoryPath, "repositories_identity");
    public string StatisticsPath => Path.Combine(DirectoryPath, "stats");
    public string LogPath => Path.Combine(DirectoryPath, "server_log");
    public string NodeName { get; private init; } = "Anonymous Git Node";
    public TimeSpan AnnounceInterval { get; private init; }
    public TimeSpan MirrorInterval { get; private init; } = TimeSpan.FromDays(1);
    public bool RecordStats { get; private init; }
    public bool ServeNomadnet { get; private init; }
    public bool UnicodeIcons { get; private init; }
    public bool MediaConversion { get; private init; } = true;
    public int LogLevel { get; private init; } = 4;
    public IReadOnlyDictionary<string, string> Groups { get; private init; } = FrozenDictionary<string, string>.Empty;
    public IReadOnlyDictionary<string, string> IdentityAliases { get; private init; } = FrozenDictionary<string, string>.Empty;
    public IReadOnlySet<string> BlockedIdentities { get; private init; } = FrozenSet<string>.Empty;
    public IReadOnlySet<string> IgnoredIdentities { get; private init; } = FrozenSet<string>.Empty;
    public IReadOnlySet<string> PushIgnoredIdentities { get; private init; } = FrozenSet<string>.Empty;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Access { get; private init; } =
        FrozenDictionary<string, IReadOnlyList<string>>.Empty;

    public static RngitHostConfiguration Load(string? directory = null, Action<string>? diagnostic = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = ResolveDirectory(directory, home, Directory.Exists, File.Exists);
        Directory.CreateDirectory(path);
        var configPath = Path.Combine(path, "config");
        if (!File.Exists(configPath))
            PublishNew(configPath, staging => File.WriteAllText(staging, DefaultConfig, new UTF8Encoding(false)));
        using var stream = File.OpenRead(configPath);
        if (stream.Length > MaxBytes) throw new FormatException("rngit config exceeds 1048576 bytes.");
        var bytes = new byte[MaxBytes + 1];
        var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (length > MaxBytes) throw new FormatException("rngit config exceeds 1048576 bytes.");
        var sections = Parse(new UTF8Encoding(false, true).GetString(bytes, 0, length).TrimStart('\uFEFF'));
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in Section("aliases"))
        {
            var value = Scalar(values);
            if (ReservedAliases.Contains(key) || !CliSupport.TryParseHash(value, out var hash))
            {
                diagnostic?.Invoke($"Invalid identity alias '{key}', ignoring.");
                continue;
            }
            aliases.Add(key, Convert.ToHexStringLower(hash));
        }
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in Section("repositories"))
        {
            var value = Scalar(values);
            if (value == "~") value = home;
            else if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                value = Path.Combine(home, value[2..]);
            string groupPath;
            try { groupPath = Path.GetFullPath(value); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                diagnostic?.Invoke($"Repository group '{key}' has an invalid path, skipping: {exception.Message}");
                continue;
            }
            if (!Directory.Exists(groupPath))
            {
                diagnostic?.Invoke($"Repository group '{key}' path '{groupPath}' does not exist, skipping.");
                continue;
            }
            groups.Add(key, groupPath);
        }
        var access = Section("access").ToFrozenDictionary(pair => pair.Key,
            pair => (IReadOnlyList<string>)new ReadOnlyCollection<string>(pair.Value.Values), StringComparer.Ordinal);
        var announce = Integer("rngit", "announce_interval", 0);
        var mirror = Integer("rngit", "mirror_interval", 24);
        return new RngitHostConfiguration(path, diagnostic)
        {
            NodeName = Value("rngit", "node_name", "Anonymous Git Node"),
            AnnounceInterval = Interval(announce, minutes: true),
            MirrorInterval = Interval(Math.Max(0, mirror), minutes: false),
            RecordStats = Boolean("rngit", "record_stats", false),
            ServeNomadnet = Boolean("pages", "serve_nomadnet", false),
            UnicodeIcons = Boolean("pages", "unicode_icons", false),
            MediaConversion = Boolean("pages", "media_conversion", true),
            LogLevel = Math.Clamp(Integer("logging", "loglevel", 4), -1, 8),
            Groups = groups.ToFrozenDictionary(StringComparer.Ordinal),
            IdentityAliases = aliases.ToFrozenDictionary(StringComparer.Ordinal),
            Access = access,
            BlockedIdentities = Identities("blocked_identities"),
            IgnoredIdentities = Identities("stats_ignore_identities"),
            PushIgnoredIdentities = Identities("stats_push_ignore_identities"),
        };

        Dictionary<string, ConfigValue> Section(string name) => sections.GetValueOrDefault(name) ?? [];
        string Value(string section, string key, string fallback) =>
            Section(section).TryGetValue(key, out var values) ? Scalar(values) : fallback;
        int Integer(string section, string key, int fallback) =>
            int.TryParse(Value(section, key, fallback.ToString(CultureInfo.InvariantCulture)),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number : throw new FormatException($"rngit config [{section}] {key}: expected a signed 32-bit integer.");
        bool Boolean(string section, string key, bool fallback) => Value(section, key, fallback ? "true" : "false").ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new FormatException($"rngit config [{section}] {key}: expected a boolean."),
        };
        FrozenSet<string> Identities(string key)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (Section("rngit").TryGetValue(key, out var values))
                foreach (var raw in values.Values)
                {
                    // Reserved target words and literal hashes take precedence
                    // over aliases, including aliases with hash-shaped names.
                    var resolved = ReservedAliases.Contains(raw.ToLowerInvariant()) || CliSupport.TryParseHash(raw, out _)
                        ? raw : aliases.GetValueOrDefault(raw, raw);
                    if (!CliSupport.TryParseHash(resolved, out var hash))
                        diagnostic?.Invoke($"Invalid identity '{raw}' for {key}, ignoring.");
                    else result.Add(Convert.ToHexStringLower(hash));
                }
            return result.ToFrozenSet(StringComparer.Ordinal);
        }
        static string Scalar(ConfigValue value) => !value.IsList
            ? value.Values[0] : throw new FormatException("rngit config: expected a scalar, not a list.");
        static TimeSpan Interval(int value, bool minutes)
        {
            try { return minutes ? TimeSpan.FromMinutes(value) : TimeSpan.FromHours(value); }
            catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
            {
                throw new FormatException("rngit config interval exceeds the supported TimeSpan range.", exception);
            }
        }
    }

    internal static string ResolveDirectory(string? directory, string home,
        Func<string, bool> directoryExists, Func<string, bool> fileExists)
    {
        if (directory is not null) return Path.GetFullPath(directory);
        var system = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "etc", "rngit"));
        if (directoryExists(system) && fileExists(Path.Combine(system, "config"))) return system;
        var xdg = Path.Combine(home, ".config", "rngit");
        // Preserve the reference's XDG detection followed by legacy destination.
        return Path.GetFullPath(directoryExists(xdg) && fileExists(Path.Combine(xdg, "config"))
            ? Path.Combine(home, ".rngit", "reticulum") : Path.Combine(home, ".rngit"));
    }

    /// <summary>Raw 64-byte identity, atomic first publication, owner-only Unix creation.
    /// Windows inherits directory ACLs; protect the configuration directory.
    /// Existing invalid identities are never replaced.</summary>
    public Identity LoadOrCreateIdentity()
    {
        if (OperatingSystem.IsWindows())
            diagnostic?.Invoke($"Identity '{IdentityPath}' inherits directory ACLs on Windows; protect the configuration directory.");
        if (!File.Exists(IdentityPath))
        {
            using var generated = Identity.Generate();
            PublishNew(IdentityPath, staging => IdentityFile.SaveNew(generated, staging));
        }
        using var stream = File.OpenRead(IdentityPath);
        if (stream.Length != Identity.KeySize) throw new InvalidDataException("Identity file must be exactly 64 bytes.");
        var key = new byte[Identity.KeySize];
        try
        {
            stream.ReadExactly(key);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Identity file changed while reading.");
            return Identity.FromPrivateKey(key);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void PublishNew(string destination, Action<string> write)
    {
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            write(staging);
            try { File.Move(staging, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        finally { File.Delete(staging); }
    }

    private sealed record ConfigValue(List<string> Values, bool IsList);

    private static Dictionary<string, Dictionary<string, ConfigValue>> Parse(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, ConfigValue>>(StringComparer.Ordinal);
        Dictionary<string, ConfigValue>? section = null;
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            if (++lineNumber > 16384) throw Error("exceeds 16384 lines");
            if (raw.Length > 8192) throw Error("line exceeds 8192 characters");
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (line.StartsWith("[[", StringComparison.Ordinal) || close <= 1
                    || (line[(close + 1)..].Trim() is { Length: > 0 } tail && tail[0] != '#'))
                    throw Error("expected a flat [section] header");
                var name = line[1..close].Trim();
                section = new Dictionary<string, ConfigValue>(StringComparer.Ordinal);
                if (name.Length == 0 || !sections.TryAdd(name, section)) throw Error("empty or duplicate section");
                continue;
            }
            var equals = line.IndexOf('=');
            if (section is null || equals <= 0) throw Error("expected key = value inside a section");
            var keyParts = Tokens(line[..equals].Trim());
            if (keyParts.IsList || keyParts.Values.Count != 1 || keyParts.Values[0].Length == 0)
                throw Error("expected a scalar key");
            if (!section.TryAdd(keyParts.Values[0], Tokens(line[(equals + 1)..].Trim())))
                throw Error("duplicate key");
        }
        return sections;

        FormatException Error(string message) => new($"rngit config line {lineNumber}: {message}.");
        ConfigValue Tokens(string value)
        {
            if (value.Contains("%(", StringComparison.Ordinal) || value.Contains("\"\"\"", StringComparison.Ordinal)
                || value.Contains("'''", StringComparison.Ordinal))
                throw Error("interpolation and multiline values are unsupported");
            var values = new List<string>();
            var cursor = 0;
            var isList = false;
            var firstQuoted = false;
            while (true)
            {
                while (cursor < value.Length && char.IsWhiteSpace(value[cursor])) cursor++;
                string token;
                var quoted = cursor < value.Length && value[cursor] is '"' or '\'';
                if (quoted)
                {
                    var quote = value[cursor++];
                    var start = cursor;
                    while (cursor < value.Length && value[cursor] != quote) cursor++;
                    if (cursor == value.Length) throw Error("unterminated quoted value");
                    token = value[start..cursor++];
                    while (cursor < value.Length && char.IsWhiteSpace(value[cursor])) cursor++;
                    if (cursor < value.Length && value[cursor] is not ',' and not '#')
                        throw Error("unexpected text after quoted value");
                }
                else
                {
                    var start = cursor;
                    while (cursor < value.Length && value[cursor] is not ',' and not '#') cursor++;
                    token = value[start..cursor].TrimEnd();
                    if (token.Contains('"') || token.Contains('\'')) throw Error("embedded quotes require quoting the entire value");
                }
                if (token.Length == 0 && isList && !quoted) break;
                if (values.Count == 0) firstQuoted = quoted;
                values.Add(token);
                if (cursor == value.Length || value[cursor] == '#') break;
                isList = true;
                cursor++;
            }
            if (cursor < value.Length && value[cursor] is not '#')
                throw Error("empty entries inside lists are unsupported");
            if (isList && !firstQuoted && values.Count == 1 && values[0].Length == 0) values.Clear();
            return new ConfigValue(values, isList);
        }
    }
}
