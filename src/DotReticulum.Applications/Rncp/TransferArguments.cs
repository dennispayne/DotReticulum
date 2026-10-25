using System.Globalization;

namespace DotReticulum.Applications.Rncp;

internal static class TransferArguments
{
    public static string[] Expand(string[] args, string flags, string values, IReadOnlySet<string> longValues,
        out HashSet<int> attachedValues)
    {
        var result = new List<string>();
        attachedValues = [];
        var positionalOnly = false;
        var expectsValue = false;
        foreach (var arg in args)
        {
            if (positionalOnly) { result.Add(arg); continue; }
            if (expectsValue) { result.Add(arg); expectsValue = false; continue; }
            if (arg == "--") { result.Add(arg); positionalOnly = true; continue; }
            var equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2 && longValues.Contains(arg[..equals]))
            {
                result.Add(arg[..equals]);
                attachedValues.Add(result.Count);
                result.Add(arg[(equals + 1)..]);
                continue;
            }
            if (arg.Length > 2 && arg[0] == '-' && arg[1] != '-' && !double.TryParse(arg, CultureInfo.InvariantCulture, out _))
            {
                var expanded = new List<string>();
                var valid = true;
                for (var i = 1; i < arg.Length; i++)
                {
                    var option = arg[i];
                    if (values.Contains(option))
                    {
                        expanded.Add("-" + option);
                        if (i + 1 < arg.Length)
                        {
                            attachedValues.Add(result.Count + expanded.Count);
                            expanded.Add(arg[(i + 1)..]);
                        }
                        else expectsValue = true;
                        break;
                    }
                    if (!flags.Contains(option)) { valid = false; break; }
                    expanded.Add("-" + option);
                }
                if (valid) { result.AddRange(expanded); continue; }
            }
            result.Add(arg);
            expectsValue = longValues.Contains(arg) || (arg.Length == 2 && arg[0] == '-' && values.Contains(arg[1]));
        }
        return result.ToArray();
    }

    public static string Next(string[] args, ref int index, IReadOnlySet<int> attachedValues, bool numeric = false)
    {
        if (index + 1 >= args.Length || (!attachedValues.Contains(index + 1) && args[index + 1].StartsWith('-') &&
            (!numeric || !double.TryParse(args[index + 1], CultureInfo.InvariantCulture, out _))))
            throw new ArgumentException($"{args[index]} requires a value.");
        return args[++index];
    }
}
