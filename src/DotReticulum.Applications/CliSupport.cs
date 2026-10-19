using System.Globalization;
using DotReticulum.Core;

namespace DotReticulum.Applications;

/// <summary>Helpers shared by the command line front ends (rncp, rnx, telemetry, lxmf).</summary>
internal static class CliSupport
{
    private static readonly string[] LevelNames = ["CRITICAL", "ERROR", "WARNING", "NOTICE", "INFO", "VERBOSE", "DEBUG", "EXTREME"];

    public const int HashHexLength = Identity.HashSize * 2;

    public static bool TryParseHash(string? text, out byte[] hash)
    {
        hash = [];
        if (text is null || text.Length != HashHexLength) return false;
        try { hash = Convert.FromHexString(text); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>Reads 32-character hex identity hashes, one per line, ignoring anything else (like upstream's allowed_identities files).</summary>
    public static List<byte[]> ReadAllowedFile(string path)
    {
        var result = new List<byte[]>();
        foreach (var line in File.ReadAllText(path).Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            if (TryParseHash(line.Trim(), out var hash)) result.Add(hash);
        }

        return result;
    }

    /// <summary>Default allowed_identities locations of an upstream-style tool: <c>~/.&lt;tool&gt;/allowed_identities</c> and <c>~/.config/&lt;tool&gt;/allowed_identities</c>.</summary>
    public static IEnumerable<string> DefaultAllowedFiles(string tool)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".config", tool, "allowed_identities");
        yield return Path.Combine(home, "." + tool, "allowed_identities");
    }

    /// <summary>Loads the identity at <paramref name="path"/>, creating it when missing; without a path uses <c>storage/identities/&lt;name&gt;</c> of the node.</summary>
    public static Identity LoadOrCreateIdentity(ReticulumNode node, string? path, string name)
    {
        if (path is null) return node.LoadOrCreateIdentity(Path.Combine("identities", name));
        path = Path.GetFullPath(path);
        if (File.Exists(path)) return IdentityFile.Load(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var identity = Identity.Generate();
        IdentityFile.SaveNew(identity, path);
        return identity;
    }

    /// <summary>Returns a thread-safe logger writing "[time] [LEVEL] message" lines for messages up to <paramref name="level"/>.</summary>
    public static Action<int, string> Logger(TextWriter writer, int level)
    {
        var gate = new object();
        return (messageLevel, message) =>
        {
            if (messageLevel > level) return;
            lock (gate) writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{LevelNames[Math.Clamp(messageLevel, 0, 7)]}] {message}");
        };
    }

    public static string PrettyHex(byte[] hash) => "<" + Convert.ToHexStringLower(hash) + ">";

    public static string SizeString(double bytes, string suffix = "B")
    {
        string[] units = ["", "K", "M", "G", "T", "P", "E", "Z"];
        var value = bytes;
        if (suffix == "b") value *= 8;
        foreach (var unit in units)
        {
            if (Math.Abs(value) < 1000.0) return (unit.Length == 0 ? value.ToString("0", CultureInfo.InvariantCulture) + " " + suffix : value.ToString("0.00", CultureInfo.InvariantCulture) + " " + unit + suffix);
            value /= 1000.0;
        }

        return value.ToString("0.00", CultureInfo.InvariantCulture) + "Y" + suffix;
    }

    /// <summary>Splits a command line like Python's <c>shlex.split</c> (POSIX mode): whitespace separates, quotes group, backslash escapes outside single quotes.</summary>
    public static List<string> ShellSplit(string command)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inToken = false;
        char quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0'; else current.Append(c);
            }
            else if (quote == '"')
            {
                if (c == '"') quote = '\0';
                else if (c == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\') current.Append(command[++i]);
                else current.Append(c);
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                inToken = true;
            }
            else if (c == '\\' && i + 1 < command.Length)
            {
                current.Append(command[++i]);
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken || current.Length > 0) args.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (quote != '\0') throw new FormatException("No closing quotation");
        if (inToken || current.Length > 0) args.Add(current.ToString());
        return args;
    }
}
