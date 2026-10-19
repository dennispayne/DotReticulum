using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Core;

[assembly: InternalsVisibleTo("DotReticulum.Applications.Tests")]

namespace DotReticulum.Applications.Rngit;

/// <summary>Bounded flat ConfigObj subset for the pinned upstream rngit client configuration.</summary>
public sealed class RngitClientConfiguration
{
    private const int MaxConfigBytes = 1024 * 1024;
    private const int MaxLines = 16384;
    private const string DefaultConfig = "[client]\nref_batch_size = 25\n\n[aliases]\n\n[logging]\nloglevel = 4\n";
    private readonly Dictionary<string, string> aliases = new(StringComparer.Ordinal);

    private RngitClientConfiguration(string directory) => DirectoryPath = directory;

    public string DirectoryPath { get; }
    public string ConfigPath => Path.Combine(DirectoryPath, "client_config");
    public string IdentityPath => Path.Combine(DirectoryPath, "client_identity");
    public string LogPath => Path.Combine(DirectoryPath, "client_log");
    public int RefBatchSize { get; private set; } = 25;
    public int LogLevel { get; private set; } = 4;

    public static RngitClientConfiguration Load(string? directory = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = new RngitClientConfiguration(ResolveDirectory(directory,
            Environment.GetEnvironmentVariable("RNGIT_CONFIG"), home, Directory.Exists, File.Exists));
        Directory.CreateDirectory(result.DirectoryPath);
        if (!File.Exists(result.ConfigPath))
            PublishNew(result.ConfigPath, path => File.WriteAllText(path, DefaultConfig, new UTF8Encoding(false)));
        using var stream = File.OpenRead(result.ConfigPath);
        if (stream.Length > MaxConfigBytes) throw new FormatException("rngit client_config exceeds 1048576 bytes.");
        var bytes = new byte[MaxConfigBytes + 1];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > MaxConfigBytes) throw new FormatException("rngit client_config exceeds 1048576 bytes.");
        result.Parse(new UTF8Encoding(false, true).GetString(bytes, 0, count).TrimStart('\uFEFF'));
        return result;
    }

    internal static string ResolveDirectory(string? directory, string? environmentDirectory, string home,
        Func<string, bool> directoryExists, Func<string, bool> fileExists)
    {
        if (directory is not null) return Path.GetFullPath(directory);
        if (environmentDirectory is not null) return Path.GetFullPath(environmentDirectory);
        var xdg = Path.Combine(home, ".config", "rngit");
        // Preserve the pinned client's surprising XDG detection/legacy destination combination.
        return Path.GetFullPath(directoryExists(xdg) && fileExists(Path.Combine(xdg, "config"))
            ? Path.Combine(home, ".rngit", "reticulum") : Path.Combine(home, ".rngit"));
    }

    public string ResolveUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var parts = url.Split('/');
        if (parts is not ["rns:", "", var destination, _, _])
            throw new ArgumentException("Expected rns://<destination or alias>/<group>/<repository>.", nameof(url));
        if (!CliSupport.TryParseHash(destination, out _))
        {
            if (!aliases.TryGetValue(destination, out var resolved))
                throw new ArgumentException($"Unknown rngit destination alias '{destination}'.", nameof(url));
            parts[2] = resolved;
        }
        var result = string.Join('/', parts);
        RngitAddress.Parse(result);
        return result;
    }

    /// <summary>Uses raw upstream identity files; Unix creation is owner-only, Windows inherits directory ACLs.</summary>
    public Identity LoadOrCreateIdentity()
    {
        if (!File.Exists(IdentityPath))
        {
            using var generated = Identity.Generate();
            PublishNew(IdentityPath, path => IdentityFile.SaveNew(generated, path));
        }
        using var stream = File.OpenRead(IdentityPath);
        if (stream.Length != Identity.KeySize)
            throw new InvalidDataException($"Identity file must be exactly {Identity.KeySize} bytes.");
        var key = new byte[Identity.KeySize];
        try
        {
            stream.ReadExactly(key);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Identity file changed while being read.");
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

    private void Parse(string text)
    {
        string? section = null;
        var sections = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            if (++lineNumber > MaxLines) throw new FormatException("rngit client_config exceeds 16384 lines.");
            if (raw.Length > 8192) throw Error("line exceeds 8192 characters");
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (line.StartsWith("[[", StringComparison.Ordinal) || close <= 1
                    || (line[(close + 1)..].Trim() is { Length: > 0 } tail && tail[0] != '#'))
                    throw Error("expected a flat [section] header");
                section = line[1..close].Trim();
                if (section.Length == 0 || !sections.TryAdd(section, new HashSet<string>(StringComparer.Ordinal)))
                    throw Error("empty or duplicate section");
                continue;
            }
            var equals = line.IndexOf('=');
            if (section is null || equals <= 0) throw Error("expected key = value inside a section");
            var key = Scalar(line[..equals].Trim(), key: true);
            if (key.Length == 0 || !sections[section].Add(key)) throw Error("empty or duplicate key");
            var value = Scalar(line[(equals + 1)..].Trim(), key: false);
            if (section == "client" && key == "ref_batch_size")
            {
                var size = Integer(value);
                if (size <= 0) throw Error("ref_batch_size must be positive (zero cannot advance upstream's fetch queue)");
                RefBatchSize = Math.Min(1024, size);
            }
            else if (section == "logging" && key == "loglevel") LogLevel = Math.Clamp(Integer(value), -1, 8);
            else if (section == "aliases")
            {
                if (!CliSupport.TryParseHash(value, out var hash)) throw Error($"alias '{key}' must contain exactly 32 hexadecimal characters");
                aliases.Add(key, Convert.ToHexStringLower(hash));
            }
        }

        int Integer(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number : throw Error("expected a signed 32-bit integer");
        FormatException Error(string message) => new($"rngit client_config line {lineNumber}: {message}.");

        string Scalar(string value, bool key)
        {
            if (value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal)
                || value.Contains("%(", StringComparison.Ordinal))
                throw Error("multiline values and interpolation are unsupported");
            if (value.Length > 0 && value[0] is '"' or '\'')
            {
                var close = value.IndexOf(value[0], 1);
                if (close < 0) throw Error("unterminated quoted scalar");
                var tail = value[(close + 1)..].Trim();
                if (tail.Length > 0 && (key || tail[0] != '#')) throw Error("unexpected text after quoted scalar");
                return value[1..close];
            }
            var comment = value.IndexOf('#');
            if (comment >= 0) value = value[..comment].TrimEnd();
            if (value.IndexOfAny([',', '"', '\'']) >= 0) throw Error("lists and embedded quotes are unsupported");
            return value;
        }
    }
}
