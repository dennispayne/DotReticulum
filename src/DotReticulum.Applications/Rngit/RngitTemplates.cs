using System.Text;

namespace DotReticulum.Applications.Rngit;

public sealed record RngitEditor(string File, IReadOnlyList<string> Arguments, bool Authorized = false);

/// <summary>Bounded, explicitly authorized template and editor presentation helpers.</summary>
public sealed class RngitTemplates
{
    public const string BaseTemplate = "#!c=0\n> {NODE_NAME}\n\n{NAVIGATION}\n{PAGE_CONTENT}\n<\n-\n`a`F666`[Served by rngit {VERSION}`:/page/index.mu] - {GEN_TIME}`f";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string directory;
    private readonly bool allowExecutable;
    private readonly TimeSpan timeout;
    private readonly int maxOutputBytes;
    private readonly Action<string>? diagnostic;

    public RngitTemplates(string directory, bool allowExecutable = false, TimeSpan? timeout = null,
        int maxOutputBytes = 1024 * 1024, Action<string>? diagnostic = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.allowExecutable = allowExecutable;
        this.timeout = timeout ?? TimeSpan.FromSeconds(8);
        if (this.timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxOutputBytes is < 1 or > 256 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        this.maxOutputBytes = maxOutputBytes;
        this.diagnostic = diagnostic ?? Console.Error.WriteLine;
    }

    public async Task<string?> LoadAsync(string name, CancellationToken stop = default)
    {
        var path = TemplatePath(name);
        stop.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return null;
        try
        {
            RejectLinks(path);
            if (IsExecutable(path))
            {
                if (!allowExecutable)
                {
                    Report($"Executable template denied by policy: {name}");
                    return null;
                }
                var result = await RngitProcess.RunAsync(path, [], directory, timeout: timeout,
                    maxOutputBytes: maxOutputBytes, stop: stop).ConfigureAwait(false);
                if (result.Code != 0)
                {
                    Report($"Executable template {name} failed ({result.Code}): {result.Error}");
                    return null;
                }
                return Utf8.GetString(result.Output).TrimEnd();
            }
            return (await ReadBoundedAsync(path, stop).ConfigureAwait(false)).TrimEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Report($"Could not load template {name}: {error.Message}");
            return null;
        }
    }

    public Task<string?> Load(string name, CancellationToken stop = default) => LoadAsync(name, stop);

    public async Task<string?> RenderAsync(string name, IReadOnlyDictionary<string, string> replacements,
        CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        var text = await LoadAsync(name, stop).ConfigureAwait(false);
        if (text is null) return null;
        foreach (var (key, value) in replacements)
        {
            stop.ThrowIfCancellationRequested();
            if (key.Length == 0 || key.Contains('{') || key.Contains('}'))
                throw new ArgumentException("Replacement keys must be unbraced names.", nameof(replacements));
            text = ReplaceBounded(text, "{" + key + "}", value);
        }
        CheckSize(text);
        return text;
    }

    public async Task<byte[]> RenderPageAsync(string pageContent, string nodeName, string version,
        string? navigation = null, string? template = null, CancellationToken stop = default,
        Func<string>? generationTime = null)
    {
        CheckSize(pageContent);
        var content = FormatTabs(pageContent);
        if (template is not null)
        {
            var custom = await LoadAsync(template, stop).ConfigureAwait(false);
            var selected = string.IsNullOrEmpty(custom) ? DefaultTemplate(template) : custom;
            if (selected is not null) content = ReplaceBounded(selected, "{PAGE_CONTENT}", content);
        }
        var customBase = await LoadAsync("base", stop).ConfigureAwait(false);
        var result = string.IsNullOrEmpty(customBase) ? BaseTemplate : customBase;
        result = ReplaceBounded(result, "{NODE_NAME}", nodeName);
        result = ReplaceBounded(result, "{VERSION}", version);
        result = ReplaceBounded(result, "{NAVIGATION}", navigation ?? "");
        result = ReplaceBounded(result, "{GEN_TIME}", generationTime?.Invoke() ?? "Unknown generation time");
        result = ReplaceBounded(result, "{PAGE_CONTENT}", content);
        CheckSize(result);
        return Utf8.GetBytes(result);
    }

    public Task<string?> EditAsync(string input, string extension, bool authorized, CancellationToken stop = default)
    {
        if (!authorized)
        {
            stop.ThrowIfCancellationRequested();
            Report("Editor denied by policy.");
            return Task.FromResult<string?>(null);
        }
        var command = Environment.GetEnvironmentVariable("EDITOR");
        if (string.IsNullOrWhiteSpace(command))
        {
            var fallback = new[] { "nano", "vim", "vi" }.Select(RngitMedia.FindExecutable).FirstOrDefault(file => file is not null);
            if (fallback is not null) return EditAsync(input, new RngitEditor(fallback, [], true), extension, stop);
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            Report("No editor found. Please set the EDITOR environment variable.");
            return Task.FromResult<string?>(null);
        }
        return EditAsync(input, ParseEditor(command, authorized: true), extension, stop);
    }

    public Task<string?> EditAsync(string input, RngitEditor editor, CancellationToken stop = default)
        => EditAsync(input, editor, ".txt", stop);

    public async Task<string?> EditAsync(string input, RngitEditor editor, string extension, CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (extension.Length > 16 || !extension.StartsWith('.') || !extension.Skip(1).All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Editor extension must be a short dot-prefixed file extension.", nameof(extension));
        stop.ThrowIfCancellationRequested();
        if (!editor.Authorized)
        {
            Report("Editor denied by policy.");
            return null;
        }
        CheckSize(input);
        RejectLinks(directory);
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        var path = Path.Combine(directory, ".rngit-edit-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(path, options))
                await stream.WriteAsync(Utf8.GetBytes(input), stop).ConfigureAwait(false);
            var arguments = editor.Arguments.Concat([path]).ToArray();
            var result = await RngitProcess.RunAsync(editor.File, arguments, directory, timeout: timeout,
                maxOutputBytes: maxOutputBytes, stop: stop).ConfigureAwait(false);
            if (result.Code != 0)
            {
                Report($"Editor failed ({result.Code}): {result.Error}");
                return null;
            }
            RejectLinks(path);
            return await ReadBoundedAsync(path, stop).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Report($"Could not edit content: {error.Message}");
            return null;
        }
        finally { File.Delete(path); }
    }

    /// <summary>Parses quotes, but deliberately performs no shell expansion or metacharacter evaluation.</summary>
    public static RngitEditor ParseEditor(string command, bool authorized = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var values = new List<string>();
        var value = new StringBuilder();
        char quote = '\0';
        var started = false;
        foreach (var character in command)
        {
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else value.Append(character);
                started = true;
            }
            else if (character is '\'' or '"') { quote = character; started = true; }
            else if (char.IsWhiteSpace(character))
            {
                if (started) { values.Add(value.ToString()); value.Clear(); started = false; }
            }
            else { value.Append(character); started = true; }
        }
        if (quote != '\0') throw new ArgumentException("EDITOR contains an unclosed quote.", nameof(command));
        if (started) values.Add(value.ToString());
        if (values.Count == 0 || values[0].Length == 0) throw new ArgumentException("EDITOR has no executable.", nameof(command));
        return new(values[0], values.Skip(1).ToArray(), authorized);
    }

    public static string Icon(string name, bool unicodeIcons = false) => (name, unicodeIcons) switch
    {
        ("sep", _) => "•",
        ("folder", true) => "🗀", ("folder", false) => "󰉖",
        ("file", true) => "🗎", ("file", false) => "",
        ("branch", true) => "⑃", ("branch", false) => "󰘬",
        ("commits", true) => "🖹", ("commits", false) => "󰋚",
        ("tag", true) => "⌆", ("tag", false) => "󰓼",
        ("stats", true) => "🗠", ("stats", false) => "",
        ("heart", true) => "♥", ("heart", false) => "󰋑",
        ("package", true) => "◇", ("package", false) => "󰏗",
        ("work", true) => "☸", ("work", false) => "󱌣",
        _ => ""
    };

    public static string FormatTabs(string text) => text.Replace("\t", "   ", StringComparison.Ordinal);
    public static string Escape(string text) => text.Replace("`", "\\`", StringComparison.Ordinal);

    public static string Link(string label, string path, IReadOnlyDictionary<string, string>? fields = null,
        bool bold = false, string? remote = null)
    {
        var clean = label.Replace("[", "").Replace("]", "").Replace("`", "");
        var query = fields is { Count: > 0 } ? "`" + string.Join("|", fields.Select(pair =>
            pair.Key + "=" + QuotePlus(pair.Value))) : "";
        var result = $"`[{clean}`{remote}:{path}{query}]";
        return bold || remote is not null ? "`!" + result + "`!" : result;
    }

    public static string FormatDiff(string diff, int maxInputBytes = 256 * 1024, int maxOutputBytes = 1024 * 1024)
    {
        if (maxInputBytes < 1 || maxOutputBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxInputBytes));
        if (diff.Length > maxInputBytes || Utf8.GetByteCount(diff) > maxInputBytes)
            throw new IOException("Source diff exceeds its byte limit.");
        var lines = new List<string>();
        foreach (var line in diff.Replace("\\", "\\\\").Split('\n'))
        {
            if (line.StartsWith('+')) lines.Add(line.StartsWith("+++", StringComparison.Ordinal) ? Escape(line) : "`F0a0" + Escape(line) + "`f");
            else if (line.StartsWith('-')) lines.Add(line.StartsWith("---", StringComparison.Ordinal) ? Escape("\\" + line) : "`F900" + Escape(line) + "`f");
            else if (line.StartsWith("@@", StringComparison.Ordinal)) lines.Add("`F0aa" + Escape(line) + "`f");
            else if (line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("new file", StringComparison.Ordinal) || line.StartsWith("deleted file", StringComparison.Ordinal))
            {
                if (line.StartsWith("diff --git a", StringComparison.Ordinal)) lines.Add("");
                lines.Add("`F666" + Escape(line) + "`f");
            }
            else lines.Add(Escape(line));
        }
        if (lines.Sum(line => (long)line.Length) + lines.Count - 1 > maxOutputBytes)
            throw new IOException("Rendered diff exceeds its byte limit.");
        var result = string.Join("\n", lines);
        if (Utf8.GetByteCount(result) > maxOutputBytes) throw new IOException("Rendered diff exceeds its byte limit.");
        return result;
    }

    private static string QuotePlus(string value)
    {
        var result = new StringBuilder();
        foreach (var item in Encoding.UTF8.GetBytes(value))
            if (item is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~')
                result.Append((char)item);
            else if (item == 32) result.Append('+');
            else result.Append('%').Append(item.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        return result.ToString();
    }

    private string TemplatePath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || name is "." or ".." || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw new ArgumentException("Template names must be single local names without separators.", nameof(name));
        return Path.Combine(directory, name + ".mu");
    }

    private static string? DefaultTemplate(string name) => name switch
    {
        "front" => "> Groups\n\n{PAGE_CONTENT}",
        "no_ident" => ">>No Identity\n\nThis page requires identification, and none was received.\n",
        "group" or "repo" or "tree" or "blob" or "commits" or "commit" or "refs" or "stats"
            or "releases" or "release" or "work" or "work_doc" => "{PAGE_CONTENT}",
        _ => null
    };

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows X_OK accepts all existing files, which would disable static .mu.
            // Only native executables with an MZ header are executable templates here.
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Template/editor paths may not traverse symbolic links or reparse points.");
    }

    private async Task<string> ReadBoundedAsync(string path, CancellationToken stop)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > maxOutputBytes) throw new IOException("Template/editor input exceeds its byte limit.");
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, stop).ConfigureAwait(false);
            if (count == 0) break;
            if (content.Length + count > maxOutputBytes) throw new IOException("Template/editor input exceeds its byte limit.");
            content.Write(buffer, 0, count);
        }
        return Utf8.GetString(content.ToArray());
    }

    private string ReplaceBounded(string text, string token, string value)
    {
        long occurrences = 0;
        for (var index = 0; (index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0; index += token.Length) occurrences++;
        var length = text.Length + occurrences * (value.Length - token.Length);
        if (length > maxOutputBytes) throw new IOException("Rendered template exceeds its byte limit.");
        var result = text.Replace(token, value, StringComparison.Ordinal);
        CheckSize(result);
        return result;
    }

    private void CheckSize(string text)
    {
        if (text.Length > maxOutputBytes || Utf8.GetByteCount(text) > maxOutputBytes)
            throw new IOException("Presentation content exceeds its byte limit.");
    }

    private void Report(string message) => diagnostic?.Invoke(message);
}
