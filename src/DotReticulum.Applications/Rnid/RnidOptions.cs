using System.Globalization;

namespace DotReticulum.Applications.Rnid;

internal sealed class RnidFailure(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

internal sealed class RnidOptions
{
    public readonly Dictionary<string, string?> Values = new(StringComparer.Ordinal);
    public string? Operation { get; private set; }
    public readonly List<string> Paths = [];
    public int Verbosity { get; private set; }
    public string Format => Has("--base32") ? "base32" : Has("--base64") ? "base64" : Has("--base256") ? "base256" : Has("--hex") ? "hex" : "bin";
    public bool Has(string key) => Values.ContainsKey(key);
    public string? Get(string key) => Values.GetValueOrDefault(key);
    public double Timeout => Has("-t") ? double.Parse(Get("-t")!, CultureInfo.InvariantCulture) : 15;
    public static RnidOptions Parse(string[] args)
    {
        if (args.Length > 1024 || args.Any(a => a.Length > 32768)) throw new RnidFailure(2, "Too many or oversized arguments.");
        var result = new RnidOptions();
        Dictionary<string, string> aliases = new()
        {
            ["-i"] = "--identity", ["-g"] = "--generate", ["-m"] = "--import-pub", ["-M"] = "--import-prv",
            ["-x"] = "--export-pub", ["-X"] = "--export-prv", ["-a"] = "--announce", ["-H"] = "--hash",
            ["-d"] = "--decrypt", ["-e"] = "--encrypt", ["-V"] = "--validate", ["-s"] = "--sign",
            ["-S"] = "--sign-message", ["-E"] = "--embed-meta", ["-w"] = "--write", ["-r"] = "--read",
            ["-f"] = "--force", ["-I"] = "--stdin", ["-O"] = "--stdout", ["-R"] = "--request",
            ["-N"] = "--no-cache", ["-p"] = "--print-identity", ["-P"] = "--print-private", ["-B"] = "--base32",
            ["-b"] = "--base64", ["-U"] = "--base256", ["-F"] = "--hex", ["-h"] = "--help"
        };
        string[] required = ["--config", "--identity", "--generate", "--import-pub", "--import-prv", "--hash", "--write", "--read", "-t", "--editor", "--editor-args", "--editor-timeout", "--show"];
        string[] optional = ["--announce", "--sign-message", "--embed-meta", "--meta-spec"];
        string[] flags = ["--export-pub", "--export-prv", "--force", "--stdin", "--stdout", "--request", "--no-cache", "--print-identity", "--print-private", "--base32", "--base64", "--base256", "--hex", "--raw", "--meta", "--help", "--version", "--allow-editor", "-v", "-q", "--verbose", "--quiet"];
        string[] operations = ["--sign", "--validate", "--encrypt", "--decrypt"];
        var longOptions = required.Concat(optional).Concat(flags).Concat(operations)
            .Where(o => o.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var tokens = args.ToList();
        args = tokens.ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            var pieces = args[i].Split('=', 2);
            if (pieces[0].StartsWith("--", StringComparison.Ordinal) && !longOptions.Contains(pieces[0]))
            {
                var matches = longOptions.Where(o => o.StartsWith(pieces[0], StringComparison.Ordinal)).ToArray();
                if (matches.Length > 1) throw new RnidFailure(2, $"ambiguous option: {pieces[0]} could match {string.Join(", ", matches)}");
                if (matches.Length == 1) pieces[0] = matches[0];
            }
            if (pieces[0].Length > 2 && pieces[0][0] == '-' && pieces[0][1] != '-'
                && !IsNegativeNumber(pieces[0]))
            {
                var cluster = new List<string>();
                for (var offset = 1; offset < pieces[0].Length; offset++)
                {
                    var shortOption = "-" + pieces[0][offset];
                    var canonical = aliases.GetValueOrDefault(shortOption, shortOption);
                    if (required.Contains(canonical) || optional.Contains(canonical) || operations.Contains(canonical))
                    {
                        var attached = pieces[0][(offset + 1)..];
                        if (pieces.Length == 2) attached += "=" + pieces[1];
                        cluster.Add(attached.Length == 0 ? shortOption : shortOption + "=" + attached);
                        break;
                    }
                    if (!flags.Contains(canonical))
                        throw new RnidFailure(2, $"argument {shortOption}: ignored explicit argument '{pieces[0][(offset + 1)..]}'");
                    cluster.Add(shortOption);
                    if (offset == pieces[0].Length - 1 && pieces.Length == 2)
                        throw new RnidFailure(2, $"argument {shortOption}: ignored explicit argument '{pieces[1]}'");
                }
                tokens.RemoveAt(i);
                tokens.InsertRange(i, cluster);
                args = tokens.ToArray();
                pieces = args[i].Split('=', 2);
            }
            var key = aliases.GetValueOrDefault(pieces[0], pieces[0]);
            if (key is "--help" or "--version" && pieces.Length == 1)
            {
                result.Values[key] = null;
                return result;
            }
            if (key is "--sign" or "--validate" or "--encrypt" or "--decrypt")
            {
                SetOperation(key);
                if (pieces.Length == 2) result.Paths.Add(pieces[1]);
                while (i + 1 < args.Length && IsValue(args[i + 1])) result.Paths.Add(args[++i]);
                result.Values[key] = null;
            }
            else if (required.Contains(key) || optional.Contains(key))
            {
                string? value;
                if (pieces.Length == 2) value = pieces[1];
                else if (i + 1 < args.Length && (IsValue(args[i + 1]) || key == "--editor-args"))
                    value = args[++i];
                else if (optional.Contains(key))
                    value = key == "--announce" ? "rns.id" : null;
                else throw new RnidFailure(2, $"argument {key}: expected one argument");
                result.Values[key] = value;
                if (key == "--sign-message") SetOperation(key);
            }
            else if (flags.Contains(key))
            {
                if (pieces.Length != 1) throw new RnidFailure(2, $"argument {key}: ignored explicit argument");
                result.Values[key] = null;
                if (key is "-v" or "--verbose") result.Verbosity++;
                if (key is "-q" or "--quiet") result.Verbosity--;
            }
            else if (key.Length > 2 && key[0] == '-' && key[1..].All(c => c is 'v' or 'q'))
            {
                result.Values[key] = null;
                result.Verbosity += key[1..].Sum(c => c == 'v' ? 1 : -1);
            }
            else throw new RnidFailure(2, $"unrecognized arguments: {args[i]}");
        }
        if (new[] { "--identity", "--generate", "--import-pub", "--import-prv", "--show" }.Count(result.Has) > 1)
            throw new RnidFailure(1, "The -i, -g, -m and -M args are mutually exclusive");
        if (new[] { "--base64", "--base32", "--base256", "--hex" }.Count(result.Has) > 1)
            throw new RnidFailure(1, "The -b, -B, --hex and --base256 args are mutually exclusive");
        if (!double.TryParse(result.Get("-t") ?? "15", NumberStyles.Float, CultureInfo.InvariantCulture, out var timeout)
            || !double.IsFinite(timeout) || timeout < 0 || timeout > 3600)
            throw new RnidFailure(2, "Identity request timeout must be finite and between 0 and 3600 seconds.");
        return result;

        void SetOperation(string key)
        {
            if (result.Operation is not null && result.Operation != key)
                throw new RnidFailure(1, "This utility currently only supports one of the encrypt, decrypt, sign or verify operations per invocation");
            result.Operation = key;
        }
    }

    private static bool IsValue(string value) => !value.StartsWith('-') || value == "-" || IsNegativeNumber(value);

    private static bool IsNegativeNumber(string value) => value.Length > 1 && value[0] == '-'
        && double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out _);
}
