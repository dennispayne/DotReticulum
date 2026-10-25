using System.Globalization;
using System.Net;
using System.Text;
using DotReticulum.Applications.Rnsd;

namespace DotReticulum.Applications.Rnid;

/// <summary>Bounded ConfigObj-style RSG metadata and the built-in Validator value domain.</summary>
public static class RnidMetadata
{
    public static Dictionary<object, object?> Parse(string text, string? spec = null)
    {
        if (Encoding.UTF8.GetByteCount(text) > 1024 * 1024) throw new FormatException("Metadata exceeds the 1 MiB limit.");
        var parsed = ParseConfig(text);
        Interpolate(parsed, [], 0);
        if (Measure(parsed) > 1024 * 1024) throw new FormatException("Expanded metadata exceeds the 1 MiB limit.");
        if (spec is not null) Validate(parsed, ParseConfig(spec), 0);
        return parsed;
    }

    private static long Measure(object? value) => value switch
    {
        string text => Encoding.UTF8.GetByteCount(text),
        Dictionary<object, object?> map => map.Sum(p => Measure(p.Key) + Measure(p.Value)),
        object?[] array => array.Sum(Measure),
        _ => 8
    };

    private static Dictionary<object, object?> ParseConfig(string text)
    {
        var root = new Dictionary<object, object?>();
        var stack = new List<Dictionary<object, object?>> { root };
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = StripComment(lines[index]).Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[')
            {
                var depth = line.TakeWhile(c => c == '[').Count();
                if (depth > 32 || depth > stack.Count || !line.EndsWith(new string(']', depth), StringComparison.Ordinal))
                    throw new FormatException("Invalid metadata section nesting.");
                var name = Unquote(line[depth..^depth].Trim());
                if (name.Length == 0) throw new FormatException("Empty metadata section.");
                var parent = stack[depth - 1];
                var child = new Dictionary<object, object?>();
                if (!parent.TryAdd(name, child)) throw new FormatException($"Duplicate metadata section {name}.");
                if (stack.Count > depth) stack.RemoveRange(depth, stack.Count - depth);
                stack.Add(child);
                continue;
            }
            var split = line.IndexOf('=');
            if (split < 1) throw new FormatException("Metadata settings require key = value.");
            var key = Unquote(line[..split].Trim());
            var value = line[(split + 1)..].Trim();
            object? data;
            if (value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal))
            {
                var delimiter = value[..3];
                var content = new StringBuilder(value[3..]);
                while (!content.ToString().Contains(delimiter, StringComparison.Ordinal))
                {
                    if (++index >= lines.Length) throw new FormatException("Unterminated multiline metadata value.");
                    content.Append('\n').Append(lines[index]);
                }
                var full = content.ToString();
                var end = full.IndexOf(delimiter, StringComparison.Ordinal);
                if (full[(end + 3)..].Trim().Length != 0) throw new FormatException("Unexpected data after multiline value.");
                data = full[..end];
            }
            else
            {
                var parts = RnsdConfigParser.ParseList(value);
                data = HasComma(value) ? parts.Cast<object?>().ToArray() : parts.Count == 0 ? "" : parts[0];
            }
            if (!stack[^1].TryAdd(key, data)) throw new FormatException($"Duplicate metadata key {key}.");
        }
        return root;
    }

    private static void Validate(Dictionary<object, object?> data, Dictionary<object, object?> spec, int depth)
    {
        if (depth > 32) throw new FormatException("Metadata spec nesting exceeds 32 levels.");
        foreach (var (key, rule) in spec)
        {
            if (key is string wildcard && wildcard == "__many__")
            {
                foreach (var actualKey in data.Keys.Where(k => !spec.ContainsKey(k)).ToArray())
                    Apply(actualKey, rule);
            }
            else Apply(key, rule);
        }

        void Apply(object key, object? rule)
        {
            if (rule is Dictionary<object, object?> section)
            {
                if (data.GetValueOrDefault(key) is not Dictionary<object, object?> nested)
                    throw new FormatException($"Metadata did not pass spec validation: missing section {key}.");
                Validate(nested, section, depth + 1);
                return;
            }
            if (rule is not string expression) throw new FormatException("Invalid metadata validation expression.");
            var open = expression.IndexOf('(');
            var name = open < 0 ? expression : expression[..open];
            var arguments = open < 0 ? [] : SplitArguments(expression[(open + 1)..^1]);
            var positional = arguments.Where(a => !a.Contains('=')).ToArray();
            var named = arguments.Where(a => a.Contains('=')).Select(a => a.Split('=', 2))
                .ToDictionary(a => a[0].Trim(), a => Unquote(a[1].Trim()), StringComparer.Ordinal);
            if (!data.TryGetValue(key, out var value))
            {
                if (!named.TryGetValue("default", out var fallback)) throw new FormatException($"Metadata did not pass spec validation: missing {key}.");
                value = fallback == "None" ? null : fallback.StartsWith("list(", StringComparison.Ordinal) && fallback.EndsWith(')')
                    ? SplitArguments(fallback[5..^1]).Select(v => (object?)Unquote(v)).ToArray() : fallback;
            }
            try { data[key] = ConvertValue(name.Trim(), value, positional, named); }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            { throw new FormatException($"Metadata did not pass spec validation for {key}: {ex.Message}", ex); }
        }
    }

    private static object? ConvertValue(string type, object? value, string[] positional, Dictionary<string, string> named)
    {
        type = type switch { "int_list" => "integer_list", "bool_list" => "boolean_list", "tuple" => "list", "int" => "integer", "bool" => "boolean", "str" => "string", "" => "pass", _ => type };
        if (value is null) return null;
        if (type == "pass") return value;
        var text = value as string;
        if (type is "list" or "force_list" or "string_list" or "integer_list" or "float_list" or "boolean_list" or "ip_addr_list" or "mixed_list")
        {
            var list = value as object?[];
            if (list is null)
            {
                if (type != "force_list") throw new FormatException("Expected a list.");
                list = [value];
            }
            var subtype = type switch { "string_list" => "string", "integer_list" => "integer", "float_list" => "float", "boolean_list" => "boolean", "ip_addr_list" => "ip_addr", _ => "pass" };
            if (type == "mixed_list")
            {
                if (list.Length != positional.Length) throw new FormatException("Wrong mixed list length.");
                return list.Select((v, i) => ConvertValue(Unquote(positional[i]), v, [], [])).ToArray();
            }
            Bounds(list.Length);
            return list.Select(v => ConvertValue(subtype, v, [], [])).ToArray();
        }
        if (text is null) throw new FormatException("Expected a scalar.");
        object result = type switch
        {
            "string" => text,
            "integer" => long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
            "float" => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
            "boolean" => text.ToLowerInvariant() switch
            {
                "true" or "yes" or "on" or "1" => true,
                "false" or "no" or "off" or "0" => false,
                _ => throw new FormatException("Invalid boolean.")
            },
            "option" => positional.Select(Unquote).Contains(text) ? text : throw new FormatException("Invalid option."),
            "ip_addr" => IPAddress.TryParse(text, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? text : throw new FormatException("Invalid IPv4 address."),
            "pass" => text,
            _ => throw new FormatException($"Unsupported metadata validator {type}.")
        };
        if (result is double d && !double.IsFinite(d)) throw new FormatException("Non-finite metadata number.");
        if (type == "integer") Bounds((long)result);
        else if (type == "float") Bounds((double)result);
        else if (type == "string") Bounds(text.Length);
        return result;

        void Bounds(double actual)
        {
            var min = named.GetValueOrDefault("min") ?? positional.ElementAtOrDefault(0);
            var max = named.GetValueOrDefault("max") ?? positional.ElementAtOrDefault(1);
            if (min is not null && min != "None" && actual < double.Parse(min, CultureInfo.InvariantCulture)
                || max is not null && max != "None" && actual > double.Parse(max, CultureInfo.InvariantCulture))
                throw new FormatException("Value outside metadata spec bounds.");
        }
    }

    private static bool HasComma(string value)
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

    private static string StripComment(string text)
    {
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            if (quote != '\0') { if (text[i] == quote) quote = '\0'; }
            else if (text[i] is '"' or '\'') quote = text[i];
            else if (text[i] == '#') return text[..i];
        }
        return text;
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && text[0] == text[^1] && text[0] is '"' or '\'' ? text[1..^1] : text;

    private static string[] SplitArguments(string text)
    {
        var values = new List<string>();
        var start = 0;
        var depth = 0;
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) { values.Add(Unquote(text[start..i].Trim())); start = i + 1; }
            if (depth < 0 || depth > 32) throw new FormatException("Invalid validator nesting.");
        }
        if (quote != '\0' || depth != 0) throw new FormatException("Invalid validator expression.");
        if (start < text.Length) values.Add(Unquote(text[start..].Trim()));
        return values.ToArray();
    }

    private static void Interpolate(Dictionary<object, object?> section, List<Dictionary<object, object?>> parents, int depth,
        long[]? budget = null)
    {
        if (depth > 32) throw new FormatException("Metadata interpolation exceeds 32 levels.");
        budget ??= [1024 * 1024];
        var scope = new List<Dictionary<object, object?>> { section };
        scope.AddRange(parents);
        foreach (var key in section.Keys.ToArray())
        {
            if (section[key] is Dictionary<object, object?> child) Interpolate(child, scope, depth + 1, budget);
            else
            {
                if (section[key] is string value) section[key] = Expand(value, new HashSet<string>(StringComparer.Ordinal), 0);
                else if (section[key] is object?[] list)
                    section[key] = list.Select(v => v is string text ? Expand(text, new HashSet<string>(StringComparer.Ordinal), 0) : v).ToArray();
                budget[0] -= Measure(key) + Measure(section[key]);
                if (budget[0] < 0) throw new FormatException("Expanded metadata exceeds the 1 MiB limit.");
            }
        }

        string Expand(string text, HashSet<string> trail, int nesting)
        {
            if (nesting > 32) throw new FormatException("Metadata interpolation exceeds 32 levels.");
            var result = new StringBuilder();
            for (var index = 0; index < text.Length;)
            {
                if (text[index] != '%' || index + 1 >= text.Length) { result.Append(text[index++]); continue; }
                if (text[index + 1] == '%') { result.Append('%'); index += 2; continue; }
                if (text[index + 1] != '(') { result.Append(text[index++]); continue; }
                var end = text.IndexOf(")s", index + 2, StringComparison.Ordinal);
                if (end < 0) { result.Append(text[index++]); continue; }
                var name = text[(index + 2)..end];
                if (!trail.Add(name)) throw new FormatException("Metadata interpolation loop detected.");
                string? replacement = null;
                foreach (var current in scope)
                {
                    if (current.GetValueOrDefault(name) is string scalar) { replacement = scalar; break; }
                    if (current.GetValueOrDefault("DEFAULT") is Dictionary<object, object?> defaults
                        && defaults.GetValueOrDefault(name) is string fallback) { replacement = fallback; break; }
                }
                if (replacement is null) throw new FormatException($"Missing metadata interpolation option {name}.");
                result.Append(Expand(replacement, trail, nesting + 1));
                trail.Remove(name);
                if (result.Length > 1024 * 1024) throw new FormatException("Interpolated metadata exceeds the 1 MiB limit.");
                index = end + 2;
            }
            return result.ToString();
        }
    }
}
