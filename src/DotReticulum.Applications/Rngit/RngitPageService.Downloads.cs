using System.Collections;
using System.Globalization;
using System.Text;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rngit;

public sealed partial class RngitPageService
{
    private static async Task<byte[]> ReadFileAsync(string path, int limit, CancellationToken token)
    {
        EnsureUnlinked(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length > limit) throw new InvalidDataException("File exceeds byte limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (stream.ReadByte() != -1) throw new InvalidDataException("File changed while reading.");
        return bytes;
    }
    private async Task<FileResponse?> DownloadAsync(RngitHostedRepository repo, object? data, CancellationToken token)
    {
        var file = Decode(Field(data, "path"));
        var format = Field(data, "fmt");
        if (file.Length == 0 || !SafeGitPath(file) || format.Length > 0 && (Path.GetExtension(file).ToLowerInvariant() != ".md" || format != "mu")) return null;
        var reference = Field(data, "ref", "HEAD");
        var sha = await ResolveAsync(repo, reference, token).ConfigureAwait(false);
        if (sha is null) return null;
        var info = await BlobInfoAsync(repo, sha, file, token, DownloadLimit).ConfigureAwait(false);
        // A Git symlink stores a target, not file contents. Downloads do not follow it.
        if (info is not { Type: "blob", Content: not null, Symlink: false }) return null;
        var bytes = info.Value.Content!;
        var name = file.Split('/')[^1];
        if (format == "mu")
        {
            bytes = Utf8.GetBytes(MarkdownMicron.Format(Utf8.GetString(bytes), Scope(repo, reference, file)).TrimEnd());
            if (bytes.Length == 0) return null;
            name = Path.ChangeExtension(name, ".mu");
        }
        return new(bytes, new Dictionary<object, object?> { ["name"] = Utf8.GetBytes(name) });
    }

    private async Task<object?> MediaAsync(object? data, Identity? caller, CancellationToken token)
    {
        if (Get(data, "key") is null || Get(data, "path") is not string mediaPath || mediaPath.Length > 8192) return false;
        var components = (mediaPath.StartsWith("/media", StringComparison.Ordinal) ? mediaPath[6..] : mediaPath).TrimStart('/').Split('/');
        if (components.Length < 4) return false;
        var repository = Decode(components[0]) + "/" + Decode(components[1]);
        var repo = store.Find(repository);
        if (repo is null || !Allowed(caller, repository, "read")) return false;
        using var lease = await store.LockRepositoryAsync(repository, token).ConfigureAwait(false);
        if (!Allowed(caller, repository, "read")) return false;
        var reference = Decode(components[2]);
        var file = Decode(string.Join('/', components.Skip(3)));
        if (!SafeGitPath(file)) return false;
        var sha = await ResolveAsync(repo, reference, token).ConfigureAwait(false);
        if (sha is null) return false;
        var info = await BlobInfoAsync(repo, sha, file, token, DownloadLimit).ConfigureAwait(false);
        if (info is not { Type: "blob", Content: not null, Symlink: false }) return false;
        var name = file.Split('/')[^1];
        var bytes = info.Value.Content!;
        if (configuration.MediaConversion)
        {
            var result = await new RngitMedia(diagnostic: Report).ConvertAsync(bytes, name, new(MaxInputBytes: DownloadLimit, MaxOutputBytes: DownloadLimit), token).ConfigureAwait(false);
            name = result.FileName;
            bytes = result.Data;
        }
        return new FileResponse(bytes, new Dictionary<object, object?> { ["name"] = Utf8.GetBytes(name) });
    }

    private static object? MapValue(object? map, string key) => map switch
    {
        IReadOnlyDictionary<object, object?> readOnly => readOnly.GetValueOrDefault(key),
        IDictionary dictionary when dictionary.Contains(key) => dictionary[key],
        _ => null,
    };
    private static string MapText(object? map, string key, string fallback = "") => MapValue(map, key) as string ?? fallback;
    private static double Number(object? map, string key) => MapValue(map, key) switch
    {
        double d => d, float f => f, long l => l, ulong u => u, int i => i, uint u => u, _ => 0,
    };
    private static List<object?> Items(object? value) => value is IEnumerable sequence && value is not string && value is not byte[]
        ? sequence.Cast<object?>().ToList() : [];
    private static object? UnpackResponse(object? response) => response is byte[] { Length: > 1 } bytes && bytes[0] == 0 ? MsgPack.Unpack(bytes.AsSpan(1)) : null;
    private static string Date(double seconds, string format, string fallback = "unknown") => seconds is <= 0 or > 253402300799 || double.IsNaN(seconds)
        ? fallback : DateTimeOffset.FromUnixTimeSeconds((long)seconds).LocalDateTime.ToString(format, CultureInfo.InvariantCulture);
    private static string Author(object? value, string fallback = "Unknown") => value switch
    {
        byte[] { Length: > 0 } bytes => "<" + Convert.ToHexStringLower(bytes) + ">",
        string { Length: > 0 } text => "<" + Escape(text) + ">",
        _ => fallback,
    };

    private async Task<List<object?>> PublishedReleasesAsync(RngitHostedRepository repo, CancellationToken token)
    {
        var list = await new RngitReleaseService(store, Report).ReadListAsync(repo, token).ConfigureAwait(false);
        return Items(MapValue(list, "releases")).Where(r => MapText(r, "status") == "published").ToList();
    }
    private async Task<object?> ArtifactAsync(RngitHostedRepository repo, object? data, CancellationToken token)
    {
        var tag = Decode(Field(data, "t"));
        var name = Decode(Field(data, "a"));
        if (!RngitRepositoryStore.ValidComponent(name)) return null;
        var service = new RngitReleaseService(store, Report);
        if (tag == "latest") tag = await LatestReleaseAsync(service, repo, token).ConfigureAwait(false) ?? "";
        if (!RngitRepositoryStore.ValidComponent(tag)) return null;
        var release = await service.ReadReleaseAsync(repo, tag, token).ConfigureAwait(false);
        if (MapText(release, "status") != "published") return null;
        if (Items(MapValue(release, "artifacts")).Any(a => MapText(a, "name") == name && Number(a, "size") > DownloadLimit)) return null;
        var response = await service.ReadArtifactAsync(repo, tag, name, token).ConfigureAwait(false);
        if (response?.Data.Length > DownloadLimit) return null;
        return response;
    }
    private static async Task<string?> LatestReleaseAsync(RngitReleaseService service, RngitHostedRepository repo, CancellationToken token)
    {
        var list = await service.ReadListAsync(repo, token).ConfigureAwait(false);
        var latest = MapText(list, "latest");
        if (latest.Length > 0) return latest;
        return Items(MapValue(list, "releases")).OrderByDescending(r => Number(r, "created")).Select(r => MapText(r, "tag")).FirstOrDefault();
    }
    private async Task<byte[]> ReleasesAsync(RngitHostedRepository repo, string path, object? data, CancellationToken token)
    {
        var service = new RngitReleaseService(store, Report);
        var nav = Navigation(repo.Group, repo.Name, "releases");
        if (path == "/page/releases.mu")
        {
            var list = await service.ReadListAsync(repo, token).ConfigureAwait(false);
            var all = Items(MapValue(list, "releases"));
            if (all.Count == 0) return await Page(">>Releases\n\nNo releases available for this repository.\n", nav, "repo", token).ConfigureAwait(false);
            var releases = all.Where(r => MapText(r, "status") == "published").ToArray();
            var content = new StringBuilder($">>Releases ({releases.Length})\n\n");
            foreach (var release in releases)
            {
                var tag = MapText(release, "tag", "unknown");
                var preview = MapText(release, "preview").Split('\n')[0];
                if (preview.Length > 2048) preview = preview[..2048] + "…";
                var artifacts = (int)Number(release, "artifacts");
                var latest = tag == MapText(list, "latest") ? " " + Icon("sep") + " `FT537855`*Latest`*`f" : "";
                content.Append(Link(tag, "/page/release.mu", true, ("g", repo.Group), ("r", repo.Name), ("t", tag)))
                    .Append($" `F666{Date(Number(release, "created"), "yyyy-MM-dd")} {Icon("sep")} `*{artifacts} artifact{(artifacts == 1 ? "" : "s")}`*{latest}`f\n");
                if (preview.Length > 0) content.Append(RenderDocument(preview, MapText(release, "format", "markdown"))).Append('\n');
                content.Append('\n');
            }
            View(repo);
            return await Page(content.ToString().TrimEnd() + "\n", nav, "releases", token).ConfigureAwait(false);
        }
        var requestedTag = Decode(Field(data, "t"));
        if (requestedTag.Length == 0) return await Page(">>Error\n\nInvalid request\n", token: token).ConfigureAwait(false);
        nav = Navigation(repo.Group, repo.Name, Link("releases", "/page/releases.mu", true, ("g", repo.Group), ("r", repo.Name)) + " / " + Escape(requestedTag));
        var resolvedTag = requestedTag == "latest" ? await LatestReleaseAsync(service, repo, token).ConfigureAwait(false) : requestedTag;
        if (resolvedTag is null) return await Page(">>Release Not Found\n\nNo releases exist.\n", nav, token: token).ConfigureAwait(false);
        if (!RngitRepositoryStore.ValidComponent(resolvedTag)) return await Page($">>Release Not Found\n\nThe release {Escape(resolvedTag)} does not exist.\n", nav, token: token).ConfigureAwait(false);
        var info = await service.ReadReleaseAsync(repo, resolvedTag, token).ConfigureAwait(false);
        if (info is null || MapText(info, "status") != "published")
            return await Page($">>Release Not Found\n\nThe release {Escape(resolvedTag)} does not exist.\n", nav, token: token).ConfigureAwait(false);
        var releaseDirectory = Path.Combine(repo.Path + ".releases", resolvedTag);
        var count = await ThanksAsync(Path.Combine(releaseDirectory, "THANKS"), releaseDirectory, Field(data, "thanks").Length > 0, token).ConfigureAwait(false);
        var body = new StringBuilder(Link(Icon("heart") + $" Thanks ({count})", "/page/release.mu", false,
            ("g", repo.Group), ("r", repo.Name), ("t", resolvedTag), ("thanks", "y")) + "\n\n");
        var timestamp = Number(info, "created");
        body.Append($">>Release {Escape(resolvedTag)}{(timestamp == 0 ? "" : " " + Icon("sep") + " " + Date(timestamp, "yyyy-MM-dd HH:mm:ss"))}\n\n");
        var notes = MapText(info, "notes").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (notes.Length > 0) body.Append(RenderDocument(notes, MapText(info, "notes_format", "text"))).Append("\n\n");
        var artifactsList = Items(MapValue(info, "artifacts"));
        body.Append($">>Artifacts{(artifactsList.Count == 0 ? "" : $" ({artifactsList.Count})")}\n\n");
        if (artifactsList.Count == 0) body.Append("`*No artifacts for this release`*\n");
        foreach (var artifact in artifactsList.OrderBy(a => MapText(a, "name"), StringComparer.Ordinal))
        {
            var name = MapText(artifact, "name", "unknown");
            var size = (long)Number(artifact, "size");
            body.Append(Link(Icon("file") + " " + name, "/file/artifact", false, ("g", repo.Group), ("r", repo.Name), ("t", resolvedTag), ("a", name)))
                .Append(" `F666").Append(Link("(" + (size == 0 ? "0 B" : Size(size)) + ")", "/file/artifact", false,
                    ("g", repo.Group), ("r", repo.Name), ("t", resolvedTag), ("a", name))).Append("`f\n");
        }
        View(repo);
        return await Page(body.ToString(), nav, "release", token).ConfigureAwait(false);
    }
    private static string RenderDocument(string text, string format) => format switch
    {
        "markdown" => MarkdownMicron.Format(text),
        "micron" => text,
        _ => "`=" + text + "`=\n",
    };

    private int CountWork(RngitHostedRepository repo, Identity? caller)
    {
        var directory = repo.Path + ".work";
        EnsureUnlinked(directory);
        var active = Path.Combine(directory, "active");
        if (!Directory.Exists(active)) return 0;
        var count = 0;
        foreach (var entry in Directory.EnumerateDirectories(active).Take(16385))
        {
            if (++count > 16384) throw new InvalidDataException("Work directory exceeds entry limit.");
            EnsureUnlinked(entry);
        }
        return Directory.EnumerateDirectories(active).Count(entry => long.TryParse(Path.GetFileName(entry), out var id) && DocumentAllowed(caller, repo.Group + "/" + repo.Name, id));
    }
    private async Task<byte[]> WorkAsync(RngitHostedRepository repo, string path, object? data, Identity? caller, CancellationToken token)
    {
        var service = new RngitWorkService(store, Report);
        var repository = repo.Group + "/" + repo.Name;
        var scope = Field(data, "scope", path == "/page/work.mu" ? "active" : "all");
        if (scope is not ("active" or "completed" or "proposed" or "all")) scope = "active";
        if (path == "/page/work.mu")
        {
            var list = UnpackResponse(await service.ListDocumentsAsync(repository, "all", caller, token).ConfigureAwait(false));
            var content = new StringBuilder();
            var filters = new List<string>();
            foreach (var s in new[] { "active", "completed", "proposed", "all" })
            {
                var count = s == "all" ? new[] { "active", "completed", "proposed" }.Sum(key => Items(MapValue(list, key)).Count) : Items(MapValue(list, s)).Count;
                var emphasis = s == scope ? "`_" : "";
                filters.Add(emphasis + Link(Capitalize(s), "/page/work.mu", true, ("g", repo.Group), ("r", repo.Name), ("scope", s)) + emphasis + $" ({count})");
            }
            content.Append(string.Join(" " + Icon("sep") + " ", filters)).Append("\n\n");
            foreach (var s in scope == "all" ? new[] { "active", "completed", "proposed" } : new[] { scope })
            {
                var docs = Items(MapValue(list, s)).OrderByDescending(d => Math.Max(Number(d, "created"), Number(d, "edited"))).ToArray();
                content.Append(">>").Append(Capitalize(s)).Append('\n');
                if (docs.Length == 0) content.Append($"\n`*No {s} work documents`*\n");
                else
                {
                    content.Append('\n');
                    foreach (var doc in docs)
                    {
                        var title = MapText(doc, "title", "Untitled");
                        if (title.Length > 92) title = title[..92] + "…";
                        var id = (long)Number(doc, "id");
                        content.Append(Link(Icon("file") + " " + title, "/page/work_doc.mu", true, ("g", repo.Group), ("r", repo.Name), ("id", id), ("scope", s)))
                            .Append($" `F666#{id}`f\n`F666{Date(Number(doc, "created"), "yyyy-MM-dd", "")} by {Author(MapValue(doc, "author"), "unknown")}`f\n");
                        var comments = (int)Number(doc, "comments");
                        if (comments > 0) content.Append($"`F666{comments} updates`f\n");
                        content.Append('\n');
                    }
                }
                if (docs.Length == 0) content.Append('\n');
            }
            View(repo);
            return await Page(content.ToString().TrimEnd('\n') + "\n", Navigation(repo.Group, repo.Name, "work"), "work", token).ConfigureAwait(false);
        }
        var rawId = Field(data, "id");
        if (rawId.Length == 0) return await Page(">>Error\n\nInvalid request\n", token: token).ConfigureAwait(false);
        if (!TryDecimal(rawId, out var documentId)) return await Page(">>Error\n\nInvalid document ID\n", token: token).ConfigureAwait(false);
        if (!DocumentAllowed(caller, repository, documentId)) return await Page(">>Error\n\nThe requested work document was not found\n", token: token).ConfigureAwait(false);
        var rawDocument = await service.ReadDocumentAsync(repository, documentId, scope, caller, token).ConfigureAwait(false);
        var document = UnpackResponse(rawDocument);
        if (rawDocument is byte[] { Length: > 0 } error && error[0] == 255)
            return await Page(">>Error\n\nCould not load work document\n", token: token).ConfigureAwait(false);
        if (document is null) return await Page(">>Not Found\n\nThe requested work document was not found\n", token: token).ConfigureAwait(false);
        var meta = MapValue(document, "meta");
        var titleText = MapText(meta, "title", "Untitled");
        if (titleText.Length > 256) titleText = titleText[..256] + "…";
        var text = MapText(document, "content");
        var created = Number(meta, "created");
        var edited = Number(meta, "edited");
        var signatureStatus = "Document not signed";
        if (MapValue(meta, "signature") is byte[] { Length: 64 } signature && MapValue(meta, "identity") is byte[] { Length: 64 } publicKey)
        {
            signatureStatus = "Not valid";
            try
            {
                using var identity = Identity.FromPublicKey(publicKey);
                if (identity.Verify(signature, Utf8.GetBytes(text))) signatureStatus = "Valid";
            }
            catch (ArgumentException) { }
        }
        var bodyText = new StringBuilder($">>{Escape(titleText)}\n\n`F666Author    : {Author(MapValue(meta, "author"))}`f\n`F666Signature : {signatureStatus}`f\n`F666Created   : {Date(created, "yyyy-MM-dd HH:mm")}`f\n");
        if (edited > 0 && edited != created) bodyText.Append($"`F666Edited    : {Date(edited, "yyyy-MM-dd HH:mm")}`f\n");
        var actualScope = MapText(document, "scope", scope);
        bodyText.Append($"`F666Status    : {Capitalize(actualScope)}`f\n\n");
        if (text.Trim().Length > 0) bodyText.Append(RenderDocument(text.Trim(), MapText(meta, "format", "markdown"))).Append('\n');
        var updates = Items(MapValue(document, "comments")).OrderBy(c => Number(c, "id")).ToArray();
        if (updates.Length > 0)
        {
            bodyText.Append($"\n>>Updates ({updates.Length})\n");
            foreach (var update in updates)
                bodyText.Append($"\n`F666#{(long)Number(update, "id")} by {Author(MapValue(update, "author"))} on {Date(Number(update, "created"), "yyyy-MM-dd HH:mm")}`f\n")
                    .Append(RenderDocument(MapText(update, "content"), MapText(update, "format", "markdown"))).Append('\n');
        }
        var navigation = Navigation(repo.Group, repo.Name, Link("work", "/page/work.mu", true, ("g", repo.Group), ("r", repo.Name)) + " / #" + documentId)
            + "\n" + Link("Download", "/file/workdoc", true, ("g", repo.Group), ("r", repo.Name), ("id", documentId)) + "\n";
        View(repo);
        return await Page(bodyText.ToString(), navigation, "work_doc", token).ConfigureAwait(false);
    }
    private static string Capitalize(string value) => value.Length == 0 ? "" : char.ToUpperInvariant(value[0]) + value[1..];
    private async Task<long> ThanksAsync(string path, string target, bool add, CancellationToken token)
    {
        if (add && requestLink.Value is not { Length: 16 })
        {
            Report("Thanks request needs an established link ID; no increment was applied.");
            add = false;
        }
        if (add)
        {
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([.. requestLink.Value!, .. Utf8.GetBytes(target)]));
            lock (thanksGate)
            {
                if (!thanksSeen.Add(hash)) add = false;
                else
                {
                    thanksOrder.Enqueue(hash);
                    if (thanksOrder.Count > 256) thanksSeen.Remove(thanksOrder.Dequeue());
                }
            }
        }
        try
        {
            EnsureUnlinked(path);
            var existed = File.Exists(path);
            var count = 0L;
            if (existed)
            {
                var raw = MapValue(MsgPack.Unpack(await ReadFileAsync(path, 65536, token).ConfigureAwait(false)), "count")
                    ?? throw new InvalidDataException("Invalid thanks count.");
                count = MsgPack.ToInt64(raw);
            }
            if (count < 0) throw new InvalidDataException("Invalid thanks count.");
            var value = checked(count + (add ? 1 : 0));
            var bytes = MsgPack.Pack(new Dictionary<object, object?> { ["count"] = value });
            var staging = path + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                await File.WriteAllBytesAsync(staging, bytes, token).ConfigureAwait(false);
                EnsureUnlinked(path);
                File.Move(staging, path, overwrite: true);
            }
            finally { File.Delete(staging); }
            // The pinned helper returns zero after first publication, even when it persisted one.
            return existed ? value : 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Report($"Thanks data failed: {e.Message}");
            return 0;
        }
    }
    private async Task<FileResponse?> WorkDownloadAsync(RngitHostedRepository repo, object? data, Identity? caller, CancellationToken token)
    {
        if (!TryDecimal(Field(data, "id"), out var id) || !DocumentAllowed(caller, repo.Group + "/" + repo.Name, id)) return null;
        var scope = Field(data, "scope", "all");
        // Pinned page download's whitelist accidentally excludes proposed.
        if (scope is not ("active" or "completed" or "all")) scope = "active";
        var doc = UnpackResponse(await new RngitWorkService(store, Report).ReadDocumentAsync(repo.Group + "/" + repo.Name, id, scope, caller, token).ConfigureAwait(false));
        if (doc is null) return null;
        var content = MapText(doc, "content").Trim();
        if (content.Length == 0) return null;
        var meta = MapValue(doc, "meta");
        var title = MapText(meta, "title", "Untitled");
        title = title[..Math.Min(title.Length, 256)];
        title = title.Replace('/', '_').Replace('\\', '_').Replace('\0', '_');
        var name = title + (MapText(meta, "format") == "micron" ? ".mu" : ".md");
        // Page workdoc uses the [name, bytes] convention rather than stream metadata.
        return new(Utf8.GetBytes(content), name);
    }

}
