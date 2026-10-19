using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

/// <summary>
/// Pinned rngit /mgmt/work documents. Management owns the shared repository lifecycle lease;
/// the per-store mutation gate also serializes page snapshots with document/comment mutation.
/// </summary>
public sealed partial class RngitWorkService
{
    public const int ContentLimit = 256 * 1024;
    private const int StorageLimit = ContentLimit * 4 + 4096;
    private const int EntryLimit = 16384;
    private static readonly string[] Scopes = ["active", "completed", "proposed"];
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly RngitRepositoryStore store;
    private readonly Action<string>? diagnostic;
    private static readonly ConditionalWeakTable<RngitRepositoryStore, SemaphoreSlim> Mutations = new();
    private readonly SemaphoreSlim mutation;
    internal Func<double> Clock { get; set; } = () =>
        (DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;

    public RngitWorkService(RngitRepositoryStore store, Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.diagnostic = diagnostic;
        mutation = Mutations.GetValue(store, _ => new(1, 1));
    }

    public async Task<object?> HandleAsync(object? data, Identity? caller, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caller is null) return Status(1, "Not identified");
        if (data is not Dictionary<object, object?> map) return Status(2, "Invalid request");
        if (!map.ContainsKey(0) && !map.ContainsKey(0L)) return Status(2, "No repository specified");
        var operation = Get(data, "operation");
        if (!Truthy(operation)) return Status(2, "Invalid request");
        if (Get(data, 0) is not string repository) return Status(255, "Remote error");
        IDisposable lease;
        try
        {
            if (store.Find(repository) is null) return Status(3, "Not found");
            lease = await store.LockRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException) { return Status(3, "Not found"); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Report($"Cannot lock work repository {repository}: {e.Message}");
            return Status(255, "Remote error");
        }
        using var repositoryLease = lease;
        await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = store.ResolvePermission(caller, repository, "read");
            var write = store.ResolvePermission(caller, repository, "write");
            var interact = store.ResolvePermission(caller, repository, "interact");
            var propose = store.ResolvePermission(caller, repository, "propose");
            var admin = store.ResolvePermission(caller, repository, "admin");
            if (!read) return Status(3, "Not found");
            var op = operation as string;
            var rawId = Get(data, "doc_id");
            if (op is "read" or "view" or "comment" or "edit" or "delete" or "perms" && Truthy(rawId))
            {
                if (!TryId(rawId, out var docId)) return Status(2, "Invalid request");
                read = store.ResolveDocumentPermission(caller, repository, docId, "read") || admin;
                if (!read) return Status(3, "Document not found");
                if (op is "comment" or "edit")
                    interact |= store.ResolveDocumentPermission(caller, repository, docId, "interact");
                if (op == "edit") write |= store.ResolveDocumentPermission(caller, repository, docId, "write");
            }
            var manage = interact && write;
            var allowed = op switch
            {
                "list" or "view" => read,
                "comment" => interact && (read || write),
                "propose" => propose,
                "create" or "edit" or "delete" or "complete" or "activate" => manage,
                "perms" => admin,
                _ => false,
            };
            if (!allowed) return Status(1, "Not allowed");
            var found = store.Find(repository);
            if (found is null) return Status(3, "Not found");
            var work = found.Path + ".work";
            EnsureUnlinked(work);
            cancellationToken.ThrowIfCancellationRequested();
            return op switch
            {
                "list" => List(work, data, id => store.ResolveDocumentPermission(caller, repository, id, "read"), cancellationToken),
                "view" => View(work, data, cancellationToken),
                "create" or "propose" => Create(work, data, caller, op == "propose", cancellationToken),
                "edit" => Edit(work, data, caller, cancellationToken),
                "comment" => Comment(work, data, caller, cancellationToken),
                "delete" => Delete(work, data, caller, repository, cancellationToken),
                "complete" or "activate" => Transition(work, data, caller, repository, op == "complete", cancellationToken),
                "perms" => Permissions(work, data, caller, repository, cancellationToken),
                _ => Status(2, "Invalid request"),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Report($"Work request {repository} failed: {e.Message}");
            return Status(255, "Remote error");
        }
        finally { mutation.Release(); }
    }

    /// <summary>
    /// Authorized page/API view; anonymous public reads are supported. The caller must own
    /// the repository lifecycle lease; pages already hold it and must not acquire it twice.
    /// </summary>
    public Task<object?> ReadDocumentAsync(string repository, long documentId, Identity? caller, CancellationToken cancellationToken) =>
        ReadAsync(repository, Map(("doc_id", documentId), ("scope", "all")), caller, false, cancellationToken);

    /// <summary>Scoped page read under the caller's lifecycle lease; scope must contain the document.</summary>
    public Task<object?> ReadDocumentAsync(string repository, long documentId, string scope, Identity? caller, CancellationToken cancellationToken) =>
        ReadAsync(repository, Map(("doc_id", documentId), ("scope", scope)), caller, false, cancellationToken, exactScope: true);

    /// <summary>Authorized page listing under the caller's lifecycle lease. Scopes include all.</summary>
    public Task<object?> ListDocumentsAsync(string repository, string scope, Identity? caller, CancellationToken cancellationToken) =>
        ReadAsync(repository, Map(("scope", scope)), caller, true, cancellationToken);

    private async Task<object?> ReadAsync(string repository, object data, Identity? caller, bool list, CancellationToken token, bool exactScope = false)
    {
        await mutation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var read = caller is null ? store.ResolveAnonymousPermission(repository, "read")
                : store.ResolvePermission(caller, repository, "read");
            if (!read || store.Find(repository) is not { } found) return Status(3, "Not found");
            bool CanRead(long id) => caller is null ? store.ResolveAnonymousDocumentPermission(repository, id, "read")
                : store.ResolveDocumentPermission(caller, repository, id, "read");
            if (!list && !CanRead((long)Get(data, "doc_id")!)
                && !(caller is not null && store.ResolvePermission(caller, repository, "admin")))
                return Status(3, "Document not found");
            var work = found.Path + ".work";
            EnsureUnlinked(work);
            if (exactScope)
            {
                if (!ValidScope(data, "all")) return Status(2, "Invalid request");
                var scope = (string)Get(data, "scope")!;
                if (scope != "all" && Locate(work, (long)Get(data, "doc_id")!, scope).Directory is null)
                    return Status(3, "Not found");
            }
            return list ? List(work, data, CanRead, token) : View(work, data, token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Report($"Reading work documents {repository} failed: {e.Message}");
            return Status(255, "Remote error");
        }
        finally { mutation.Release(); }
    }

    private static object? Get(object? data, object key) => RngitWire.Get(data, key);
    private static byte[] Status(byte status, string message = "") => RngitWire.Status(status, message);
    private static Dictionary<object, object?> Map(params (object Key, object? Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value);
    private static bool Truthy(object? value) => value switch
    {
        null => false, bool b => b, string s => s.Length != 0, byte[] b => b.Length != 0,
        object?[] a => a.Length != 0, System.Collections.IDictionary m => m.Count != 0,
        int i => i != 0, long l => l != 0, double d => d != 0, _ => true,
    };
    private static bool TryId(object? value, out long id)
    {
        switch (value)
        {
            case long l: id = l; return true;
            case int i: id = i; return true;
            case ulong u when u <= long.MaxValue: id = (long)u; return true;
            case bool b: id = b ? 1 : 0; return true;
            case double d when double.IsFinite(d) && d >= long.MinValue && d < 9223372036854775808d:
                id = (long)d; return true;
            case string s: return ParseDecimal(s, out id);
            case byte[] bytes:
                if (bytes.Any(b => b >= 128)) { id = 0; return false; }
                return ParseDecimal(Utf8.GetString(bytes), out id);
            default: id = 0; return false;
        }
    }
    private static bool ParseDecimal(string value, out long id)
    {
        var text = value.Trim();
        var builder = new StringBuilder(24);
        var previousDigit = false;
        var anyDigit = false;
        var significant = false;
        var first = true;
        foreach (var rune in text.EnumerateRunes())
        {
            if (first && rune.Value is '+' or '-')
            {
                builder.Append((char)rune.Value);
                first = false;
                continue;
            }
            first = false;
            if (rune.Value == '_')
            {
                if (!previousDigit) { id = 0; return false; }
                previousDigit = false;
                continue;
            }
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.DecimalDigitNumber) { id = 0; return false; }
            var digit = (int)Rune.GetNumericValue(rune);
            significant |= digit != 0;
            if (significant)
            {
                if (builder.Length >= 21) { id = 0; return false; }
                builder.Append((char)('0' + digit));
            }
            previousDigit = true;
            anyDigit = true;
        }
        if (!anyDigit || !previousDigit) { id = 0; return false; }
        if (!significant) builder.Append('0');
        return long.TryParse(builder.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id);
    }
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static string String(object? data, string key, string fallback = "")
    {
        var map = (Dictionary<object, object?>)data!;
        return map.TryGetValue(key, out var value) ? value as string ?? throw new InvalidDataException($"{key} must be a string.") : fallback;
    }
    private static string Format(object? data)
    {
        var value = Get(data, "format");
        return value as string is "markdown" or "micron" ? (string)value : "markdown";
    }
    private static bool ValidScope(object? data, string fallback) =>
        Value(data!, "scope", fallback) is "active" or "completed" or "proposed" or "all";
    private static int Characters(string text) => text.EnumerateRunes().Count();
    private static bool TooLarge(params string[] values) => values.Sum(v => (long)Characters(v)) > ContentLimit
        || values.Sum(v => (long)Utf8.GetByteCount(v)) > ContentLimit * 4L;

    private void Report(string message)
    {
        try { diagnostic?.Invoke(message); }
        catch { /* An optional observer cannot interrupt storage processing. */ }
    }

    private static void EnsureUnlinked(string path)
    {
        for (var item = new FileInfo(path); item is not null; item = item.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((item.Exists || Directory.Exists(item.FullName)) && (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked work storage is not authorized.");
    }

    private static string[] Entries(string path, CancellationToken token)
    {
        EnsureUnlinked(path);
        if (!Directory.Exists(path)) return [];
        var result = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            token.ThrowIfCancellationRequested();
            if (result.Count >= EntryLimit) throw new InvalidDataException("Work directory entry limit exceeded.");
            EnsureUnlinked(entry);
            result.Add(entry);
        }
        return result.ToArray();
    }

    private static bool DecimalName(string name) => name.Length > 0 && name.All(c => c is >= '0' and <= '9');
    private static long NextId(string path, CancellationToken token)
    {
        long max = 0;
        foreach (var entry in Entries(path, token))
        {
            var name = Path.GetFileName(entry);
            if (!DecimalName(name)) continue;
            if (!long.TryParse(name, out var number)) throw new InvalidDataException("Work ID is outside supported range.");
            max = Math.Max(max, number);
        }
        return checked(max + 1);
    }

    private Dictionary<object, object?>? Load(string path)
    {
        try
        {
            EnsureUnlinked(path);
            using var stream = File.OpenRead(path);
            if (stream.Length > StorageLimit) throw new InvalidDataException("Work document storage limit exceeded.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (MsgPack.Unpack(bytes) is not Dictionary<object, object?> map || map.Count == 0)
                throw new InvalidDataException("Invalid work document.");
            if (Get(map, "meta") is not Dictionary<object, object?>)
                throw new InvalidDataException("Invalid work metadata.");
            return map;
        }
        catch (Exception e)
        {
            Report($"Cannot load work document {path}: {e.Message}");
            return null;
        }
    }

    private bool Save(string path, Dictionary<object, object?> document, CancellationToken token)
    {
        try { Publish(path, MsgPack.Pack(document), token); return true; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Report($"Cannot persist work document {path}: {e.Message}");
            return false;
        }
    }

    private static void Publish(string path, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > StorageLimit) throw new InvalidDataException("Work storage limit exceeded.");
        EnsureUnlinked(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + ".write-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            EnsureUnlinked(path);
            File.Move(staging, path, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static (string? Scope, string? Directory) Locate(string work, long id, params string[] scopes)
    {
        foreach (var scope in scopes)
        {
            var path = Path.Combine(work, scope, Id(id));
            EnsureUnlinked(path);
            if (Directory.Exists(path)) return (scope, path);
        }
        return (null, null);
    }

    private static object? Value(object data, string key, object? fallback) =>
        ((Dictionary<object, object?>)data).TryGetValue(key, out var value) ? value : fallback;
    private static string Author(object meta) => Get(meta, "author") is byte[] bytes ? Convert.ToHexStringLower(bytes) : "";
    private static bool IsAuthor(object doc, Identity caller) => Get(Get(doc, "meta"), "author") is byte[] author
        && author.AsSpan().SequenceEqual(caller.Hash);
}
