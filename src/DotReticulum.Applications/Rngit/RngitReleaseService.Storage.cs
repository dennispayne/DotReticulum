using System.Globalization;
using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rngit;

public sealed partial class RngitReleaseService
{
    private readonly object thanksGate = new();
    private readonly Queue<string> thanksQueue = new();
    private readonly HashSet<string> thanksHashes = new(StringComparer.Ordinal);

    internal static bool ValidName(string? name) => name is not null && RngitRepositoryStore.ValidComponent(name);
    internal static void EnsureUnlinked(string path)
    {
        for (var current = new FileInfo(Path.GetFullPath(path)); current is not null;
            current = current.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if (current.LinkTarget is not null || ((current.Exists || Directory.Exists(current.FullName))
                && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Linked release paths are not authorized.");
    }

    private static string Root(RngitHostedRepository repository)
    {
        EnsureUnlinked(repository.Path);
        var path = repository.Path + ".releases";
        EnsureUnlinked(path);
        return path;
    }

    internal static string Child(string root, string name)
    {
        if (!ValidName(name)) throw new InvalidDataException("Unsafe release path component.");
        var path = Path.Combine(root, name);
        EnsureUnlinked(path);
        return path;
    }

    private static string[] Entries(string path)
    {
        EnsureUnlinked(path);
        var entries = Directory.EnumerateFileSystemEntries(path).Take(MaximumReleases + MaximumArtifacts + 1).ToArray();
        if (entries.Length > MaximumReleases + MaximumArtifacts) throw new InvalidDataException("Release directory exceeds entry limit.");
        foreach (var entry in entries) EnsureUnlinked(entry);
        return entries;
    }

    private static void ValidateTree(string root, CancellationToken token, int depth = 0)
    {
        if (depth > 8) throw new InvalidDataException("Release directory exceeds nesting limit.");
        token.ThrowIfCancellationRequested();
        foreach (var path in Entries(root))
            if (Directory.Exists(path)) ValidateTree(path, token, depth + 1);
    }

    internal static async Task<byte[]> ReadBytesAsync(string path, int limit, CancellationToken token)
    {
        EnsureUnlinked(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length > limit) throw new InvalidDataException("Release file exceeds size limit.");
        var result = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(result, token).ConfigureAwait(false);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Release file changed while reading.");
        return result;
    }

    internal static byte[] EncodeNotes(string text, int limit)
    {
        if (text.Length > limit || Utf8.GetByteCount(text) > limit) throw new InvalidDataException("Release text exceeds size limit.");
        return Utf8.GetBytes(text);
    }

    internal async Task WriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        EnsureUnlinked(path);
        var staging = Child(Path.GetDirectoryName(path)!, ".release-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            EnsureUnlinked(path);
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
                try { File.Delete(staging); } catch (Exception e) { Report($"Cannot clean release write staging: {e.Message}"); }
        }
    }

    private Task WriteMetaAsync(string path, Dictionary<string, string> meta, CancellationToken token) =>
        WriteAsync(path, Utf8.GetBytes(string.Join('\n', meta.Select(pair => $"{pair.Key} = {MetaValue(pair.Value)}")) + "\n"), token);

    private static string MetaValue(string value)
    {
        if (value.IndexOfAny(['\r', '\n', '"']) >= 0) throw new InvalidDataException("Unsupported release metadata value.");
        return value.IndexOfAny([',', '#', '\'']) >= 0 || value.Trim() != value ? $"\"{value}\"" : value;
    }

    private static async Task<Dictionary<string, string>> ReadMetaAsync(string path, CancellationToken token)
    {
        var text = Utf8.GetString(await ReadBytesAsync(path, 65536, token).ConfigureAwait(false));
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var separator = trimmed.IndexOf('=');
            if (separator < 1) throw new InvalidDataException("Malformed release metadata.");
            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            if (value.StartsWith('"') || value.StartsWith('\''))
            {
                var end = value.IndexOf(value[0], 1);
                if (end < 1 || (value[(end + 1)..].Trim() is { Length: > 0 } remainder && !remainder.StartsWith('#')))
                    throw new InvalidDataException("Malformed quoted release metadata.");
                value = value[1..end];
            }
            else value = value.Split('#')[0].Trim();
            if (!meta.TryAdd(key, value)) throw new InvalidDataException("Duplicate release metadata key.");
        }
        return meta;
    }

    private static async Task<string?> LatestAsync(string root, CancellationToken token)
    {
        var path = Child(root, "latest");
        if (!File.Exists(path)) return null;
        var tag = Utf8.GetString(await ReadBytesAsync(path, 1024, token).ConfigureAwait(false)).Trim();
        if (!ValidName(tag)) throw new InvalidDataException("Unsafe latest release pointer.");
        return tag;
    }

    private async Task<Dictionary<object, object?>> InfoAsync(string directory, string tag, CancellationToken token)
    {
        var meta = await ReadMetaAsync(Child(directory, "META"), token).ConfigureAwait(false);
        var created = meta.TryGetValue("created", out var value) ? long.Parse(value, CultureInfo.InvariantCulture) : 0;
        return new() { ["tag"] = meta.GetValueOrDefault("tag", tag), ["hash"] = meta.GetValueOrDefault("hash", ""),
            ["created"] = created, ["status"] = meta.GetValueOrDefault("status", "unknown"), ["created_by"] = meta.GetValueOrDefault("created_by", "") };
    }

    private static async Task<(string Notes, string Format)> NotesAsync(string directory, bool preview, CancellationToken token)
    {
        foreach (var (name, format) in new[] { ("RELEASE.md", "markdown"), ("RELEASE.mu", "micron"), ("RELEASE.txt", "text") })
        {
            if (!preview && name == "RELEASE.txt") continue;
            var path = Child(directory, name);
            if (!File.Exists(path)) continue;
            var notes = Utf8.GetString(await ReadBytesAsync(path, MaximumNotesBytes, token).ConfigureAwait(false));
            if (preview) notes = string.Join('\n', notes.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
                .Where(line => !line.StartsWith('#') && !line.StartsWith('>'))).Trim();
            return (notes, format);
        }
        return ("", preview ? "markdown" : "text");
    }

    public async Task<object> ReadListAsync(RngitHostedRepository repository, CancellationToken cancellationToken)
    {
        var root = Root(repository);
        if (!Directory.Exists(root)) return Array.Empty<object>();
        var releases = new List<Dictionary<object, object?>>();
        foreach (var directory in Entries(root).Where(Directory.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(Child(directory, "META"))) continue;
            try
            {
                var info = await InfoAsync(directory, Path.GetFileName(directory), cancellationToken).ConfigureAwait(false);
                var (preview, format) = await NotesAsync(directory, true, cancellationToken).ConfigureAwait(false);
                info["preview"] = preview;
                info["format"] = format;
                var artifacts = Child(directory, "artifacts");
                info["artifacts"] = Directory.Exists(artifacts) ? Entries(artifacts).Count(File.Exists) : 0;
                releases.Add(info);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Report($"Cannot read release metadata {directory}: {e.Message}"); }
        }
        var latest = await LatestAsync(root, cancellationToken).ConfigureAwait(false);
        if (!releases.Any(info => Equals(info["tag"], latest) && Equals(info["status"], "published"))) latest = null;
        return new Dictionary<object, object?> { ["releases"] = releases.OrderByDescending(info => (long)info["created"]!).ToArray(), ["latest"] = latest };
    }

    public async Task<Dictionary<object, object?>?> ReadReleaseAsync(RngitHostedRepository repository, string tag, CancellationToken cancellationToken)
    {
        var root = Root(repository);
        if (tag == "latest") tag = await LatestAsync(root, cancellationToken).ConfigureAwait(false) ?? "";
        if (!ValidName(tag)) return null;
        var directory = Child(root, tag);
        if (!Directory.Exists(directory) || !File.Exists(Child(directory, "META"))) return null;
        var info = await InfoAsync(directory, tag, cancellationToken).ConfigureAwait(false);
        var (notes, format) = await NotesAsync(directory, false, cancellationToken).ConfigureAwait(false);
        info["notes"] = notes;
        info["notes_format"] = format;
        var artifacts = Child(directory, "artifacts");
        info["artifacts"] = Directory.Exists(artifacts) ? Entries(artifacts).Where(File.Exists)
            .Select(path => new Dictionary<object, object?> { ["name"] = Path.GetFileName(path), ["size"] = new FileInfo(path).Length }).ToArray() : [];
        info["thanks"] = await ReadThanksAsync(directory, cancellationToken).ConfigureAwait(false);
        return info;
    }

    public async Task<FileResponse?> ReadArtifactAsync(RngitHostedRepository repository, string tag, string name, CancellationToken cancellationToken)
    {
        var root = Root(repository);
        if (tag == "latest") tag = await LatestAsync(root, cancellationToken).ConfigureAwait(false) ?? "";
        if (!ValidName(tag) || !ValidName(name)) return null;
        var path = Child(Child(Child(root, tag), "artifacts"), name);
        if (!File.Exists(path)) return null;
        return new(await ReadBytesAsync(path, MaximumArtifactBytes, cancellationToken).ConfigureAwait(false),
            new Dictionary<object, object?> { ["name"] = Utf8.GetBytes(name) });
    }

    private static async Task<long> ReadThanksAsync(string directory, CancellationToken token)
    {
        var path = Child(directory, "THANKS");
        if (!File.Exists(path)) return 0;
        var bytes = await ReadBytesAsync(path, 1024, token).ConfigureAwait(false);
        if (!MsgPack.TryUnpack(bytes, out var data) || RngitWire.Get(data, "count") is not long and not int)
            throw new InvalidDataException("Malformed release THANKS; refusing to reset.");
        var count = Convert.ToInt64(RngitWire.Get(data, "count"), CultureInfo.InvariantCulture);
        if (count < 0) throw new InvalidDataException("Negative release thanks count.");
        return count;
    }

    public async Task<long> AddThanksAsync(RngitHostedRepository repository, string tag, byte[] linkId, CancellationToken cancellationToken)
    {
        if (linkId.Length != 16) throw new ArgumentException("Expected link hash.", nameof(linkId));
        var root = Root(repository);
        if (tag == "latest") tag = await LatestAsync(root, cancellationToken).ConfigureAwait(false) ?? "";
        var directory = Child(root, tag);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Release not found.");
        var count = await ReadThanksAsync(directory, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData([.. linkId, .. Utf8.GetBytes(directory)]));
        lock (thanksGate) if (thanksHashes.Contains(hash)) return count;
        await WriteAsync(Child(directory, "THANKS"), MsgPack.Pack(new Dictionary<object, object?> { ["count"] = checked(count + 1) }), cancellationToken).ConfigureAwait(false);
        lock (thanksGate)
        {
            if (thanksHashes.Add(hash)) thanksQueue.Enqueue(hash);
            while (thanksQueue.Count > 256) thanksHashes.Remove(thanksQueue.Dequeue());
        }
        return count + 1;
    }

    private void Report(string message) => diagnostic?.Invoke(message);
}
