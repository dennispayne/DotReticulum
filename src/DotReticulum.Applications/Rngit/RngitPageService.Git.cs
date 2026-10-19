using System.Globalization;
using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public sealed partial class RngitPageService
{
    private sealed record TreeEntry(string Mode, string Type, string Hash, long Size, string Name);
    private async Task<(int Code, byte[] Output)> GitAsync(RngitHostedRepository repo, string[] arguments, CancellationToken token, int limit = OutputLimit)
    {
        EnsureUnlinked(repo.Path);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) environment[key] = null;
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        var command = new[] { "--no-pager", "--git-dir=" + repo.Path, "-c", "core.hooksPath=", "-c", "protocol.allow=never", "-c", "core.quotePath=false" }.Concat(arguments).ToArray();
        var result = await RngitProcess.RunAsync("git", command, repo.Path, timeout: TimeSpan.FromSeconds(8),
            maxOutputBytes: limit, stop: token, environment: environment).ConfigureAwait(false);
        return (result.Code, result.Output);
    }
    private async Task<string?> TextAsync(RngitHostedRepository repo, string[] args, CancellationToken token)
    {
        var result = await GitAsync(repo, args, token).ConfigureAwait(false);
        return result.Code == 0 ? Utf8.GetString(result.Output).Replace("\r\n", "\n", StringComparison.Ordinal) : null;
    }
    private async Task<string?> ResolveAsync(RngitHostedRepository repo, string reference, CancellationToken token)
    {
        if (reference.Length is 0 or > 1024 || reference.StartsWith('-') || reference.Contains('\0') || reference.Any(c => c < 32)) return null;
        var value = (await TextAsync(repo, ["rev-parse", "--verify", "--end-of-options", reference], token).ConfigureAwait(false))?.Trim();
        return value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }
    private async Task<string> DescriptionAsync(RngitHostedRepository repo, CancellationToken token)
    {
        var value = (await TextAsync(repo, ["config", "--get", "repository.description"], token).ConfigureAwait(false))?.Trim();
        if (!string.IsNullOrEmpty(value)) return value;
        var path = repo.Path + ".description";
        EnsureUnlinked(path);
        if (!File.Exists(path)) return "";
        var bytes = await ReadFileAsync(path, BlobLimit, token).ConfigureAwait(false);
        return Utf8.GetString(bytes).Trim();
    }
    private async Task<List<TreeEntry>?> EntriesAsync(RngitHostedRepository repo, string sha, string directory, CancellationToken token)
    {
        if (!SafeGitPath(directory)) return null;
        var output = await TextAsync(repo, ["ls-tree", "-z", "-l", directory.Length == 0 ? sha : sha + ":" + directory], token).ConfigureAwait(false);
        if (output is null) return null;
        var entries = new List<TreeEntry>();
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var parts = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            entries.Add(new(parts[0], parts[1], parts[2], long.TryParse(parts[3], out var size) ? size : 0, record[(tab + 1)..]));
            if (entries.Count > 100000) throw new InvalidDataException("Tree exceeds entry limit.");
        }
        return entries;
    }
    private static int PageNumber(object? data) => TryDecimal(Field(data, "page", "0"), out var page) ? (int)Math.Clamp(page, 0, 1000000) : 0;
    private static string FileNavigation(RngitHostedRepository repo, string reference, string file)
    {
        var suffix = Link("files", "/page/tree.mu", true, ("g", repo.Group), ("r", repo.Name));
        var parts = file.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        for (var i = 0; i < parts.Length; i++)
        {
            current = current.Length == 0 ? parts[i] : current + "/" + parts[i];
            suffix += " / " + (i == parts.Length - 1 ? Escape(parts[i]) : Link(parts[i], "/page/tree.mu", true,
                ("g", repo.Group), ("r", repo.Name), ("ref", reference), ("path", current)));
        }
        if (file.Length == 0) suffix += " / ";
        return Navigation(repo.Group, repo.Name, suffix);
    }
    private async Task<byte[]> RefFailure(string reference, string title, CancellationToken token, string? more = null) =>
        await Page($">{title}\n\n\nThe ref '{Escape(reference)}' does not exist in this repository.\n" + more, token: token).ConfigureAwait(false);

    private async Task<byte[]> TreeAsync(RngitHostedRepository repo, object? data, CancellationToken token)
    {
        var reference = Field(data, "ref", "HEAD");
        var directory = Decode(Field(data, "path")).Trim('/');
        var sha = await ResolveAsync(repo, reference, token).ConfigureAwait(false);
        if (sha is null) return await RefFailure(reference, ">Error", token,
            "\n" + Link("View All Refs", "/page/refs.mu", true, ("g", repo.Group), ("r", repo.Name)) + "\n").ConfigureAwait(false);
        var entries = await EntriesAsync(repo, sha, directory, token).ConfigureAwait(false);
        var content = new StringBuilder();
        if (entries is null) content.Append("Error reading directory contents.\n");
        else if (entries.Count == 0) content.Append("Empty directory.\n");
        else
        {
            entries = entries.OrderBy(e => e.Type is "tree" or "commit" ? 0 : 1).ThenBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
            var page = PageNumber(data);
            var start = (long)page * 1000;
            content.Append($">>Contents: {Escape(reference)} ({sha[..8]})\n\n");
            if (entries.Count > 1000) content.Append($"`F666Showing {start + 1}-{Math.Min(start + 1000, entries.Count)} of {entries.Count} entries`f\n\n");
            string EntryLink(string label, string route, string value) => Link(label, route, false,
                ("g", repo.Group), ("r", repo.Name), ("ref", reference), ("path", value));
            if (directory.Length != 0)
            {
                var parent = directory.Contains('/') ? directory[..directory.LastIndexOf('/')] : "";
                content.Append("`Ffe6").Append(EntryLink(Icon("folder"), "/page/tree.mu", parent)).Append("`f").Append(EntryLink(" ../", "/page/tree.mu", parent)).Append('\n');
            }
            foreach (var entry in entries.Skip((int)Math.Min(start, int.MaxValue)).Take(1000))
            {
                var subpath = directory.Length == 0 ? entry.Name : directory + "/" + entry.Name;
                if (entry.Type == "commit") content.Append($"`Ffe6⧉`f {Escape(entry.Name)} `F666(submodule)`f\n");
                else if (entry.Mode == "120000")
                {
                    var target = await TextAsync(repo, ["cat-file", "blob", entry.Hash], token).ConfigureAwait(false);
                    content.Append($"`F66d↳`f {Escape(entry.Name)} `F666→ {Escape(target?.Trim() ?? "unknown")}`f\n");
                }
                else
                {
                    var tree = entry.Type == "tree";
                    var route = tree ? "/page/tree.mu" : "/page/blob.mu";
                    content.Append(tree ? "`Ffe6" : "`F66d").Append(EntryLink(Icon(tree ? "folder" : "file"), route, subpath))
                        .Append("`f").Append(EntryLink(" " + entry.Name + (tree ? "/" : ""), route, subpath))
                        .Append(tree ? "\n" : $" `F666({Size(entry.Size)})`f\n");
                }
            }
            if (entries.Count > 1000)
            {
                content.Append('\n');
                var links = new List<string>();
                if (page > 0) links.Add(Link("« Previous", "/page/tree.mu", true, ("g", repo.Group), ("r", repo.Name), ("ref", reference), ("path", directory), ("page", page - 1)));
                links.Add($"Page {page + 1} of {(entries.Count + 999) / 1000}");
                if (start + 1000 < entries.Count) links.Add(Link("Next »", "/page/tree.mu", true, ("g", repo.Group), ("r", repo.Name), ("ref", reference), ("path", directory), ("page", page + 1)));
                content.Append(string.Join(" | ", links)).Append('\n');
            }
        }
        View(repo);
        return await Page(content.ToString(), FileNavigation(repo, reference, directory), "tree", token).ConfigureAwait(false);
    }

    private async Task<(string Type, long Size, bool Symlink, byte[]? Content, bool Binary)?> BlobInfoAsync(RngitHostedRepository repo, string sha, string path, CancellationToken token, int limit)
    {
        if (path.Length == 0 || !SafeGitPath(path)) return null;
        var spec = sha + ":" + path;
        var type = (await TextAsync(repo, ["cat-file", "-t", spec], token).ConfigureAwait(false))?.Trim();
        if (type is not ("tree" or "blob")) return null;
        var sizeString = await TextAsync(repo, ["cat-file", "-s", spec], token).ConfigureAwait(false);
        if (!long.TryParse(sizeString?.Trim(), out var size) || size < 0) return null;
        var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        var entries = await EntriesAsync(repo, sha, parent, token).ConfigureAwait(false);
        var symlink = entries?.FirstOrDefault(e => e.Name == path.Split('/')[^1])?.Mode == "120000";
        byte[]? content = null;
        var binary = false;
        if (type == "blob" && size <= limit)
        {
            var result = await GitAsync(repo, ["cat-file", "blob", spec], token, limit).ConfigureAwait(false);
            if (result.Code == 0 && result.Output.LongLength == size) content = result.Output;
            binary = result.Output.AsSpan(0, Math.Min(result.Output.Length, 8192)).Contains((byte)0);
        }
        else if (type == "blob")
        {
            // Intentional bounded prefix probe: the runner terminates the owned process after
            // 8192 bytes instead of buffering upstream's entire oversized blob for detection.
            var sample = await GitAsync(repo, ["cat-file", "blob", spec], token, 8192).ConfigureAwait(false);
            binary = sample.Output.AsSpan().Contains((byte)0);
        }
        return (type, size, symlink, content, binary);
    }

    private async Task<byte[]> BlobAsync(RngitHostedRepository repo, object? data, CancellationToken token)
    {
        var reference = Field(data, "ref", "HEAD");
        var file = Decode(Field(data, "path"));
        if (file.StartsWith("./", StringComparison.Ordinal)) file = file[2..];
        file = file.Replace("/./", "/", StringComparison.Ordinal);
        var sha = await ResolveAsync(repo, reference, token).ConfigureAwait(false);
        if (sha is null) return await RefFailure(reference, "Ref Not Found", token).ConfigureAwait(false);
        if (file.Length == 0) return await Page(">Invalid Path\n\n\nNo file path specified.\n", token: token).ConfigureAwait(false);
        var extension = Path.GetExtension(file).ToLowerInvariant();
        var renderable = extension is ".md" or ".mu";
        var render = renderable && Field(data, "raw").Length == 0;
        var info = await BlobInfoAsync(repo, sha, file, token, BlobLimit).ConfigureAwait(false);
        if (info?.Type == "tree") return await TreeAsync(repo, data, token).ConfigureAwait(false);
        var sep = Icon("sep");
        string FileLink(string label, string route, params (string Key, object Value)[] extra) => Link(label, route, true,
            new[] { ("g", (object)repo.Group), ("r", (object)repo.Name), ("ref", (object)reference), ("path", (object)file) }.Concat(extra).ToArray());
        var controls = renderable ? $"Displaying {(render ? "Rendered" : "Raw")} {sep} {FileLink(render ? "View raw" : "View rendered", "/page/blob.mu", (render ? "raw" : "render", "y"))}" : "Displaying Raw";
        var nav = FileNavigation(repo, reference, file) + $"\n{controls} {sep} {FileLink("Download", "/file/download")}"
            + (extension == ".md" ? $" `F666{FileLink("as micron", "/file/download", ("fmt", "mu"))}`f" : "") + "\n";
        var content = new StringBuilder();
        if (info is null) content.Append("File not found at this ref.\n");
        else
        {
            var blob = info.Value;
            var binary = blob.Binary;
            var target = blob.Symlink && blob.Content is not null ? Utf8.GetString(blob.Content).Trim() : null;
            content.Append($">>{Escape(file)} `F444{Escape(reference)} ({sha[..8]}) {(binary ? "Binary" : "Text")}, {Size(blob.Size)}")
                .Append(blob.Symlink ? " | Symlink → " + Escape(target ?? "unknown") : "").Append("`f\n\n");
            if (blob.Symlink) content.Append($"`*{Escape(target ?? "unknown")}`*\n");
            else if (binary)
            {
                if (ImageExtensions.Contains(extension)) content.Append($"`(Image file`w=n`a=c`:/media/{Encode(repo.Group)}/{Encode(repo.Name)}/{Encode(reference)}/{Encode(file)})\n");
                else content.Append("This file appears to be binary and cannot be displayed as text.\n");
            }
            else if (blob.Size > BlobLimit) content.Append($"This file is {Size(blob.Size)}, which exceeds the display limit of {Size(BlobLimit)}.\n");
            else if (blob.Content is null) content.Append("Error reading file content.\n");
            else
            {
                string text;
                var decoded = true;
                try { text = Utf8.GetString(blob.Content).Replace("\r\n", "\n", StringComparison.Ordinal); }
                catch (DecoderFallbackException) { text = ""; decoded = false; content.Append("Error reading file content.\n"); }
                if (decoded)
                {
                    if (render) content.Append((extension == ".md" ? MarkdownMicron.Format(text, Scope(repo, reference, file)) : text).TrimEnd()).Append('\n');
                    else content.Append("`=\n").Append(Escape(text).Replace("\\", "\\\\", StringComparison.Ordinal)).Append("\n`=\n");
                }
            }
        }
        View(repo);
        return await Page(content.ToString(), nav, "blob", token).ConfigureAwait(false);
    }
    private static string Scope(RngitHostedRepository repo, string reference, string file) =>
        ":/page/blob.mu`g=" + repo.Group + "|r=" + repo.Name + "|ref=" + reference + "|path=" + (file.Contains('/') ? file[..(file.LastIndexOf('/') + 1)] : "");

    private async Task<byte[]> RepositoryAsync(RngitHostedRepository repo, object? data, Identity? caller, CancellationToken token)
    {
        var nav = RepositoryNavigation(repo.Group, repo.Name) + await ProvenanceAsync(repo, token).ConfigureAwait(false);
        var reference = Field(data, "ref", "HEAD");
        var sha = await ResolveAsync(repo, reference, token).ConfigureAwait(false);
        var count = sha is null ? "0" : (await TextAsync(repo, ["rev-list", "--count", sha], token).ConfigureAwait(false))?.Trim() ?? "0";
        var refs = await RefRowsAsync(repo, token).ConfigureAwait(false);
        var content = new StringBuilder();
        var description = await DescriptionAsync(repo, token).ConfigureAwait(false);
        if (description.Length > 0) content.Append(Escape(description)).Append("\n\n");
        var menus = new List<string>();
        string Menu(string label, string route, params (string Key, object Value)[] extra) => Link(label, route, false,
            new[] { ("g", (object)repo.Group), ("r", (object)repo.Name) }.Concat(extra).ToArray());
        menus.Add(Menu(Icon("folder") + " Files", "/page/tree.mu", ("ref", "HEAD")));
        menus.Add(Menu(Icon("work") + " Work (" + CountWork(repo, caller) + ")", "/page/work.mu"));
        menus.Add(Menu(Icon("commits") + $" Commits ({count})", "/page/commits.mu", ("ref", "HEAD")));
        menus.Add(Menu(Icon("branch") + $" Branches ({refs.Count(r => r.Full.StartsWith("refs/heads/", StringComparison.Ordinal))})", "/page/refs.mu", ("type", "heads")));
        menus.Add(Menu(Icon("tag") + $" Tags ({refs.Count(r => r.Full.StartsWith("refs/tags/", StringComparison.Ordinal))})", "/page/refs.mu", ("type", "tags")));
        var thanks = await ThanksAsync(repo.Path + ".thanks", repo.Path, Field(data, "thanks").Length > 0, token).ConfigureAwait(false);
        menus.Add(Menu(Icon("heart") + $" Thanks ({thanks})", "/page/repo.mu", ("thanks", "y")));
        if (Allowed(caller, repo.Group + "/" + repo.Name, "stats")) menus.Add(Menu(Icon("stats") + " Stats", "/page/stats.mu"));
        var releases = await PublishedReleasesAsync(repo, token).ConfigureAwait(false);
        if (releases.Count > 0) menus.Insert(1, Menu(Icon("package") + $" Releases ({releases.Count})", "/page/releases.mu"));
        content.Append(string.Join(" " + Icon("sep") + " ", menus)).Append("\n\n<");
        string? readme = null;
        var markdown = false;
        var headSha = await ResolveAsync(repo, "HEAD", token).ConfigureAwait(false);
        foreach (var file in new[] { "README.mu", "Readme.mu", "readme.mu", "README", "readme", "README.md", "readme.md", "README.rst", "README.txt", "readme.rst", "readme.txt" })
        {
            if (headSha is null) break;
            var info = await BlobInfoAsync(repo, headSha, file, token, BlobLimit).ConfigureAwait(false);
            if (info is { Content: not null, Symlink: false })
            {
                readme = Utf8.GetString(info.Value.Content).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                markdown = file.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                break;
            }
        }
        if (readme is null) content.Append("-─\n\n`*No README file found in this repository.`*\n");
        else
        {
            if (!readme.TrimStart().StartsWith('#') && !readme.TrimStart().StartsWith('>')) content.Append("-─\n");
            content.Append(markdown ? MarkdownMicron.Format(readme, Scope(repo, reference, "")) : "\n" + readme.TrimEnd() + "\n");
        }
        View(repo);
        return await Page(content.ToString(), nav, "repo", token).ConfigureAwait(false);
    }

    private sealed record RefRow(string Full, string Name, string Hash, string Type, string Subject, string Message);
    private async Task<List<RefRow>> RefRowsAsync(RngitHostedRepository repo, CancellationToken token)
    {
        var output = await TextAsync(repo, ["for-each-ref", "--format=%(refname)%00%(refname:short)%00%(objectname)%00%(objecttype)%00%(contents:subject)%00%(contents)%00", "refs/heads", "refs/tags"], token).ConfigureAwait(false);
        var result = new List<RefRow>();
        if (output is null) return result;
        var parts = output.Split('\0');
        for (var i = 0; i + 5 < parts.Length; i += 6)
            result.Add(new(parts[i].TrimStart('\n'), parts[i + 1], parts[i + 2], parts[i + 3], parts[i + 4], parts[i + 5]));
        return result;
    }
    private async Task<byte[]> RefsAsync(RngitHostedRepository repo, object? data, CancellationToken token)
    {
        var kind = Field(data, "type");
        var head = (await TextAsync(repo, ["symbolic-ref", "HEAD"], token).ConfigureAwait(false))?.Trim();
        var rows = await RefRowsAsync(repo, token).ConfigureAwait(false);
        var content = new StringBuilder();
        content.Append(string.Join(" " + Icon("sep") + " ", new[]
        {
            Link("All", "/page/refs.mu", true, ("g", repo.Group), ("r", repo.Name)),
            Link("Branches only", "/page/refs.mu", true, ("g", repo.Group), ("r", repo.Name), ("type", "heads")),
            Link("Tags only", "/page/refs.mu", true, ("g", repo.Group), ("r", repo.Name), ("type", "tags")),
        })).Append("\n\n");
        foreach (var prefix in new[] { "heads", "tags" })
        {
            if (kind.Length > 0 && kind != prefix) continue;
            var selected = rows.Where(r => r.Full.StartsWith("refs/" + prefix + "/", StringComparison.Ordinal)).ToArray();
            if (selected.Length == 0) continue;
            content.Append($">>{(prefix == "heads" ? "Branches" : "Tags")} ({selected.Length})\n\n");
            foreach (var row in prefix == "tags" ? selected.Reverse() : selected)
            {
                var tree = Link("tree", "/page/tree.mu", true, ("g", repo.Group), ("r", repo.Name), ("ref", row.Name));
                var commits = Link("commits", "/page/commits.mu", true, ("g", repo.Group), ("r", repo.Name), ("ref", row.Name));
                if (prefix == "heads")
                {
                    content.Append(row.Full == head ? $"`F0a0{Escape(row.Name)}`f `F0a0(default)`f" : Escape(row.Name))
                        .Append($" [{tree}] [{commits}]\n{row.Hash[..7]}: {Escape(row.Subject)}\n\n");
                }
                else content.Append(Escape(row.Name)).Append(row.Type == "tag" ? " `Faa0(annotated)`f" : "")
                    .Append($" `F666{row.Hash[..7]}`f [{tree}] [{commits}]\n{Escape(row.Type == "tag" ? row.Subject[..Math.Min(512, row.Subject.Length)] : row.Subject)}\n\n");
            }
        }
        if (kind.Length == 0 && rows.Count == 0) content.Append("No refs found in this repository.\n");
        View(repo);
        return await Page(content.ToString().TrimEnd() + "\n", Navigation(repo.Group, repo.Name, "refs"), "refs", token).ConfigureAwait(false);
    }

    private async Task<byte[]> CommitsAsync(RngitHostedRepository repo, object? data, CancellationToken token)
    {
        var reference = Field(data, "ref", "HEAD");
        var file = Decode(Field(data, "path"));
        var sha = await ResolveAsync(repo, reference, token).ConfigureAwait(false);
        if (sha is null) return await RefFailure(reference, "Ref Not Found", token).ConfigureAwait(false);
        if (!SafeGitPath(file)) return await Page("Error reading commit history.\n", token: token).ConfigureAwait(false);
        var page = PageNumber(data);
        var args = new List<string> { "log", "--format=%H%x00%s%x00%an%x00%ae%x00%at", "--skip=" + ((long)page * 100).ToString(CultureInfo.InvariantCulture), "-n", "100", sha };
        if (file.Length > 0) args.AddRange(["--", ":(literal)" + file]);
        var output = await TextAsync(repo, args.ToArray(), token).ConfigureAwait(false);
        var content = new StringBuilder();
        var rows = output?.TrimEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (output is null) content.Append("Error reading commit history.\n");
        else if (rows!.Length == 0) content.Append("No commits found.\n");
        else
        {
            content.Append($">>Commits{(file.Length == 0 ? "" : " for " + Escape(file))} `F444{Escape(reference)} ({sha[..8]})`f\n\n");
            foreach (var row in rows)
            {
                var fields = row.Split('\0');
                if (fields.Length != 5 || !long.TryParse(fields[4], out var timestamp)) continue;
                var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime;
                content.Append("`F66d").Append(Link(fields[0][..7], "/page/commit.mu", true, ("g", repo.Group), ("r", repo.Name), ("ref", reference), ("h", fields[0])))
                    .Append($"`f {Escape(fields[2])} `F666{date:yyyy-MM-dd HH:mm:ss} - {RelativeTime(timestamp)}`f\n{Escape(fields[1])}\n\n");
            }
            if (page > 0 || rows.Length == 100)
            {
                var links = new List<string>();
                if (page > 0) links.Add(Link("« Newer", "/page/commits.mu", true, ("g", repo.Group), ("r", repo.Name), ("ref", reference), ("path", file), ("page", page - 1)));
                links.Add($"Page {page + 1}");
                if (rows.Length == 100) links.Add(Link("Older »", "/page/commits.mu", true, ("g", repo.Group), ("r", repo.Name), ("ref", reference), ("path", file), ("page", page + 1)));
                content.Append(string.Join(" | ", links)).Append('\n');
            }
        }
        View(repo);
        return await Page(content.ToString(), Navigation(repo.Group, repo.Name, (file.Length > 0 ? Escape(file) + " / " : "") + "commits"), "commits", token).ConfigureAwait(false);
    }
    private static string RelativeTime(long timestamp)
    {
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp;
        if (seconds < 60) return "just now";
        static string Ago(long count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";
        if (seconds < 3600) return Ago(seconds / 60, "minute");
        if (seconds < 86400) return Ago(seconds / 3600, "hour");
        if (seconds < 604800) return Ago(seconds / 86400, "day");
        if (seconds < 2592000) return Ago(seconds / 604800, "week");
        if (seconds < 31536000) return Ago(seconds / 2592000, "month");
        return Ago(seconds / 31536000, "year");
    }
    private async Task<byte[]> CommitAsync(RngitHostedRepository repo, object? data, CancellationToken token)
    {
        var reference = Field(data, "ref", "HEAD");
        if (await ResolveAsync(repo, reference, token).ConfigureAwait(false) is null) return await RefFailure(reference, "Ref Not Found", token).ConfigureAwait(false);
        var hash = Field(data, "h");
        if (hash.Length < 7) return await Page(">>Error\n\nNo valid commit hash specified.\n", token: token).ConfigureAwait(false);
        var sha = await ResolveAsync(repo, hash, token).ConfigureAwait(false);
        if (sha is null) return await Page($">>Error\n\nThe commit {Escape(hash)} does not exist in this repository.\n", token: token).ConfigureAwait(false);
        if ((await TextAsync(repo, ["cat-file", "-t", sha], token).ConfigureAwait(false))?.Trim() != "commit")
            return await Page($">>Error\n\nThe hash {Escape(hash)} does not refer to a commit.\n", token: token).ConfigureAwait(false);
        var metadata = await TextAsync(repo, ["show", "--no-patch", "--format=%P%n%an%n%ae%n%aI%n%cn%n%ce%n%cI%n%B", sha], token).ConfigureAwait(false);
        var lines = metadata?.Split('\n');
        if (lines is null || lines.Length < 7) return await Page(">>Error\n\n\nCould not retrieve commit information.\n", token: token).ConfigureAwait(false);
        var content = new StringBuilder($">>Commit {sha}\n\n");
        content.Append(Link(Icon("folder") + " Browse tree at this commit", "/page/tree.mu", true, ("g", repo.Group), ("r", repo.Name), ("ref", sha))).Append("\n\n");
        if (lines[0].Length > 0)
            content.Append("Parents    : ").Append(string.Join(' ', lines[0].Split(' ').Select(parent => Link(parent[..7], "/page/commit.mu", true, ("g", repo.Group), ("r", repo.Name), ("ref", reference), ("h", parent))))).Append('\n');
        content.Append($"Author     : {Escape(lines[1])} <{Escape(lines[2])}>\n");
        var signature = await CommitSignatureAsync(repo, sha, token).ConfigureAwait(false);
        if (signature is not null) content.Append($"Signature  : {signature}\n");
        content.Append($"Date       : {lines[3]}\n");
        if (lines[4] != lines[1]) content.Append($"Committer : {Escape(lines[4])} <{Escape(lines[5])}>\nDate      : {lines[6]}\n");
        content.Append('\n').Append(string.Join('\n', string.Join('\n', lines.Skip(7)).Trim().Replace("\\", "\\\\", StringComparison.Ordinal)
            .Split('\n').Select(line => Escape(line.StartsWith('-') ? "\\" + line : line)))).Append("\n\n");
        var changes = await TextAsync(repo, ["diff-tree", "--numstat", "-r", sha], token).ConfigureAwait(false);
        var files = new List<(string Path, int Adds, int Deletes, string Status)>();
        foreach (var line in (changes ?? "").Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length != 3) continue;
            var status = parts[0] == "-" ? "R" : "M";
            if (lines[0].Length > 0)
            {
                var parent = lines[0].Split(' ')[0];
                if ((await GitAsync(repo, ["cat-file", "-e", parent + ":" + parts[2]], token).ConfigureAwait(false)).Code != 0) status = "A";
                else if ((await GitAsync(repo, ["cat-file", "-e", sha + ":" + parts[2]], token).ConfigureAwait(false)).Code != 0) status = "D";
            }
            files.Add((parts[2], int.TryParse(parts[0], out var adds) ? adds : 0, int.TryParse(parts[1], out var deletes) ? deletes : 0, status));
        }
        if (files.Count > 0)
        {
            content.Append($">>Changes\n\n  {files.Count} files changed, {files.Sum(f => (long)f.Adds)} insertions(+), {files.Sum(f => (long)f.Deletes)} deletions(-)\n\n");
            foreach (var file in files)
            {
                var color = file.Status switch { "A" => "0a0", "D" => "900", "R" => "0aa", _ => "aa0" };
                content.Append($"  `F{color}{file.Status}`f ").Append(Link(file.Path, "/page/blob.mu", true, ("g", repo.Group), ("r", repo.Name), ("ref", sha), ("path", file.Path))).Append(' ');
                if (file.Adds > 0) content.Append($"`F0a0+{file.Adds}`f");
                if (file.Deletes > 0) content.Append((file.Adds > 0 ? " " : "") + $"`F900-{file.Deletes}`f");
                content.Append('\n');
            }
            content.Append('\n');
        }
        var diff = await TextAsync(repo, ["show", "--format=", "--no-ext-diff", "--no-textconv", sha], token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(diff)) content.Append(">>Diff\n\n").Append(RngitTemplates.FormatDiff(diff, OutputLimit, OutputLimit).TrimStart());
        View(repo);
        return await Page(content.ToString(), Navigation(repo.Group, repo.Name,
            Link("commits", "/page/commits.mu", true, ("g", repo.Group), ("r", repo.Name), ("ref", reference)) + " / " + sha[..7]), "commit", token).ConfigureAwait(false);
    }
}
