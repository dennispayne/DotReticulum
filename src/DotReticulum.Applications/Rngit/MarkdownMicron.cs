using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DotReticulum.Applications.Rngit;

/// <summary>
/// Native bounded rngit MarkdownToMicron block/inline rendering. No external syntax lexer is
/// executed: fenced code has the reference's plain-literal fallback, including rawmu passthrough.
/// Display widths use Unicode code-point counts, matching the reference without optional wcwidth.
/// </summary>
public static class MarkdownMicron
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static Match Match(string text, string pattern) => Regex.Match(text, pattern, RegexOptions.CultureInvariant, RegexTimeout);
    private static string Replace(string text, string pattern, MatchEvaluator replacement) => Regex.Replace(text, pattern, replacement, RegexOptions.CultureInvariant, RegexTimeout);
    private static string Replace(string text, string pattern, string replacement) => Regex.Replace(text, pattern, replacement, RegexOptions.CultureInvariant, RegexTimeout);
    private static string Literals(string text) => text.Replace("`", "\\`", StringComparison.Ordinal);

    public static string Format(string text, string? urlScope = null, int maxWidth = 100)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 1024 * 1024 || maxWidth is < 4 or > 1000) throw new ArgumentOutOfRangeException(nameof(text));
        urlScope ??= ":/page/";
        var result = new List<string>();
        var code = new List<string>();
        var table = new List<string>();
        var quote = new List<string>();
        var inCode = false;
        string language = "";
        void FlushQuote()
        {
            if (quote.Count == 0) return;
            result.AddRange(Wrap(Inline(string.Join(' ', quote), urlScope), maxWidth - 3).Select(line => " │ " + line));
            quote.Clear();
        }
        void FlushTable()
        {
            if (table.Count >= 2 && Match(table[1], @"^\s*\|?(?:\s*:?-+:?\s*\|)+\s*$").Success)
                result.AddRange(Table(table, urlScope, maxWidth));
            else result.AddRange(table.Select(line => Line(line, urlScope)));
            table.Clear();
        }
        void FlushCode()
        {
            if (code.Count == 0) return;
            var content = string.Join('\n', code);
            if (language.Equals("rawmu", StringComparison.OrdinalIgnoreCase)) result.Add(content);
            else result.AddRange(["`BT282828`Fddd", "`=", language.Length == 0 ? Literals(content) : Literals(content).Replace("\\", "\\\\", StringComparison.Ordinal), "`=", "`f`b"]);
            code.Clear();
        }
        foreach (var line in text.Split('\n'))
        {
            var fence = Match(line, @"^(\s*)```(.*)$");
            if (fence.Success)
            {
                FlushQuote();
                FlushTable();
                if (inCode) { FlushCode(); inCode = false; language = ""; }
                else { inCode = true; language = fence.Groups[2].Value.Trim(); }
                continue;
            }
            if (inCode) { code.Add(line); continue; }
            var quoted = Match(line, @"^>\s?(.*)$");
            if (quoted.Success) { FlushTable(); quote.Add(quoted.Groups[1].Value); continue; }
            FlushQuote();
            if (line.Contains('|') && (line.TrimStart().StartsWith('|') || line.Trim('|', ' ').Contains('|'))) table.Add(line);
            else { FlushTable(); result.Add(Line(line, urlScope)); }
            if (result.Count > 100000) throw new InvalidDataException("Markdown output exceeds line limit.");
        }
        FlushQuote();
        FlushTable();
        if (inCode) FlushCode();
        var output = string.Join('\n', result);
        if (output.Length > 8 * 1024 * 1024) throw new InvalidDataException("Markdown output exceeds byte limit.");
        return output;
    }
    private static string Line(string line, string scope)
    {
        line = line.Replace("\\", "\\\\", StringComparison.Ordinal);
        if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal) && !line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith('<')) line = "\\" + line;
        if (Match(line, @"^(\s*)(---+|===+|\*\*\*+|___+)\s*$").Success) return "-";
        var header = Match(line, @"^(#{1,6})\s+(.+)$");
        if (header.Success) return new string('>', header.Groups[1].Length) + Inline(header.Groups[2].Value, scope);
        var list = Match(line, @"^(\s*)([-*+])\s+(.+)$");
        if (list.Success) return list.Groups[1].Value + " • " + Inline(list.Groups[3].Value, scope);
        return Inline(line, scope);
    }
    private static string Inline(string text, string scope)
    {
        var links = new List<(string Text, string Url)>();
        var code = new List<string>();
        text = Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", match =>
        {
            links.Add((match.Groups[1].Value, match.Groups[2].Value));
            return "\0LINK" + (links.Count - 1) + "\0";
        });
        text = Replace(text, "`([^`]+)`", match =>
        {
            code.Add(match.Groups[1].Value);
            return "\0CODE" + (code.Count - 1) + "\0";
        });
        text = Replace(text, @"\*\*(.+?)\*\*|__(.+?)__", m => "`!" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "`!");
        text = Replace(text, @"\*(.+?)\*|_(.+?)_", m => "`*" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "`*");
        text = Replace(text, "\0LINK([0-9]+)\0", match =>
        {
            var link = links[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)];
            var parts = link.Url.Split('#');
            var url = parts[0];
            if (!url.Contains(":/", StringComparison.Ordinal)) url = scope + url + (parts.Length > 1 && parts[1].Length > 0 ? "|anchor=" + parts[1] : "");
            return "`_`!`[" + link.Text.Replace("`", "", StringComparison.Ordinal) + "`" + url + "]`!`_";
        });
        return Replace(text, "\0CODE([0-9]+)\0", match => "`BT383838`Fddd" + Literals(code[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)]) + "`f`b");
    }
    private static string[] Cells(string row)
    {
        row = row.Trim();
        if (row.StartsWith('|')) row = row[1..];
        if (row.EndsWith('|')) row = row[..^1];
        var cells = new List<string>();
        var current = new StringBuilder();
        var escaped = false;
        foreach (var c in row)
        {
            if (escaped) { current.Append(c); escaped = false; }
            else if (c == '\\') escaped = true;
            else if (c == '|') { cells.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(c);
        }
        cells.Add(current.ToString().Trim());
        return cells.ToArray();
    }
    private static int Width(string text)
    {
        text = Replace(text, "`[FB][0-9a-fA-F]{3}", "");
        text = Replace(text, "`[FB]T[0-9a-fA-F]{6}", "");
        text = Replace(text, "`[!*_=fb]", "");
        return text.EnumerateRunes().Count();
    }
    private static IEnumerable<string> Table(List<string> rows, string scope, int maxWidth)
    {
        var header = Cells(rows[0]);
        if (header.Length > 128 || rows.Count > 16384) throw new InvalidDataException("Markdown table exceeds limits.");
        var alignments = Cells(rows[1]);
        var parsed = new List<string[]> { header };
        parsed.AddRange(rows.Skip(2).Select(row => Cells(row).Concat(Enumerable.Repeat("", header.Length)).Take(header.Length).ToArray()));
        var widths = Enumerable.Range(0, header.Length).Select(i => Math.Max(3, parsed.Max(row => Width(Inline(row[i], scope))))).ToArray();
        var excess = widths.Sum() + header.Length * 3 + 1 - maxWidth;
        foreach (var i in Enumerable.Range(0, widths.Length).OrderByDescending(i => widths[i]))
        {
            if (excess <= 0) break;
            var reduction = Math.Min(excess, widths[i] - 3);
            widths[i] -= reduction;
            excess -= reduction;
        }
        string Border(char left, char middle, char right) => left + string.Join(middle, widths.Select(w => new string('─', w + 2))) + right;
        yield return "`c";
        yield return Border('┌', '┬', '┐');
        string FormatRow(string[] row, bool heading)
        {
            var result = new StringBuilder("│");
            for (var i = 0; i < widths.Length; i++)
            {
                var cell = Inline(row[i], scope);
                if (Width(cell) > widths[i]) cell = Truncate(cell, widths[i]);
                var padding = Math.Max(0, widths[i] - Width(cell));
                var alignment = i < alignments.Length ? alignments[i] : "";
                var left = !heading && alignment.EndsWith(':') ? alignment.StartsWith(':') ? padding / 2 : padding : 0;
                result.Append(' ').Append(' ', left).Append(cell).Append(' ', padding - left).Append(" │");
            }
            return result.ToString();
        }
        yield return Literals(FormatRow(header, true));
        yield return Border('├', '┼', '┤');
        foreach (var row in parsed.Skip(1)) yield return FormatRow(row, false);
        yield return Border('└', '┴', '┘');
        yield return "`a";
    }
    private static string Truncate(string text, int width)
    {
        var cut = text.Length;
        while (cut > 0 && Width(text[..cut]) >= width)
        {
            cut--;
            if (cut > 0 && char.IsLowSurrogate(text[cut]) && char.IsHighSurrogate(text[cut - 1])) cut--;
        }
        var truncated = text[..cut];
        var active = new HashSet<char>();
        var foreground = false;
        var background = false;
        for (var i = 0; i + 1 < truncated.Length; i++)
        {
            if (truncated[i] != '`') continue;
            var tag = truncated[++i];
            if ("!*_=".Contains(tag)) { if (!active.Add(tag)) active.Remove(tag); }
            else if (tag == 'f') foreground = false;
            else if (tag == 'b') background = false;
            else if (tag is 'F' or 'B')
            {
                if (tag == 'F') foreground = true;
                else background = true;
                i += i + 1 < truncated.Length && truncated[i + 1] == 'T' ? 6 : 3;
            }
        }
        return truncated + (foreground ? "`f" : "") + (background ? "`b" : "") + string.Concat(active.Order().Select(tag => "`" + tag)) + "…";
    }
    private static IEnumerable<string> Wrap(string text, int width)
    {
        if (text.Length == 0) return [""];
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Width(word) > width)
            {
                if (current.Length > 0) { lines.Add(current); current = ""; }
                var rest = word;
                while (rest.Length > 0)
                {
                    var boundaries = new List<int> { 0 };
                    foreach (var rune in rest.EnumerateRunes()) boundaries.Add(boundaries[^1] + rune.Utf16SequenceLength);
                    var fit = 0;
                    var low = 1;
                    var high = boundaries.Count - 1;
                    while (low <= high)
                    {
                        var mid = (low + high) / 2;
                        if (Width(rest[..boundaries[mid]]) <= width) { fit = mid; low = mid + 1; }
                        else high = mid - 1;
                    }
                    fit = boundaries[Math.Max(1, fit)];
                    lines.Add(rest[..fit]);
                    rest = rest[fit..];
                }
            }
            else if (Width(current) + (current.Length > 0 ? 1 : 0) + Width(word) <= width)
                current += (current.Length == 0 ? "" : " ") + word;
            else { lines.Add(current); current = word; }
        }
        if (current.Length > 0) lines.Add(current);
        return lines.Count == 0 ? [""] : lines;
    }
}
