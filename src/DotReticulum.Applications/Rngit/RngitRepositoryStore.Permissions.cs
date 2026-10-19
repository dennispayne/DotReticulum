using System.Buffers.Binary;
using System.Globalization;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public sealed partial class RngitRepositoryStore
{
    private static readonly string[] PermissionNames = ["read", "write", "create", "stats", "release", "interact", "propose", "admin"];
    private sealed class Permissions
    {
        public Dictionary<string, HashSet<string>> Values { get; } = PermissionNames.ToDictionary(p => p, _ => new HashSet<string>(StringComparer.Ordinal));
        public bool Failed { get; set; }
    }

    /// <summary>
    /// Optional operator-authorized executable resolver. The callback must own/terminate a bounded
    /// process and return at most MaxPermissionBytes UTF-8 bytes. No executable runs by default.
    /// </summary>
    public Func<string, string>? PermissionExecutor { get; set; }

    public bool IsValidPermissionEntry(string entry)
    {
        var parsed = ParsePermission(entry);
        return parsed.Permission is not null && parsed.Target is not null;
    }

    private (string? Permission, string? Target) ParsePermission(string entry)
    {
        var parts = entry.Split(':');
        if (parts.Length != 2) return (null, null);
        var permission = parts[0].ToLowerInvariant() switch
        {
            "r" or "read" => "read", "w" or "write" => "write", "rw" or "readwrite" => "readwrite",
            "c" or "create" => "create", "s" or "stats" => "stats", "rel" or "release" => "release",
            "i" or "interact" => "interact", "p" or "propose" => "propose", "adm" or "admin" => "admin", _ => null,
        };
        var target = parts[1];
        var special = target.ToLowerInvariant() is "n" or "none" or "nobody" or "a" or "all" or "everyone";
        if (!special && !(target.Length == 32 && target.All(Uri.IsHexDigit)))
            target = configuration.IdentityAliases.GetValueOrDefault(target) ?? target;
        target = target switch
        {
            "n" or "none" or "nobody" => "none", "a" or "all" or "everyone" => "all",
            _ => target.Length == 32 && target.All(Uri.IsHexDigit) ? target.ToLowerInvariant() : null,
        };
        return (permission, target);
    }

    private Permissions ParseAllowed(string content)
    {
        if (Utf8.GetByteCount(content) > MaxPermissionBytes) throw new InvalidDataException("Permission input exceeds limit.");
        var result = new Permissions();
        var lines = AllowedLines(content);
        if (lines.Length > 16384) throw new InvalidDataException("Permission input exceeds line limit.");
        foreach (var line in lines)
        {
            var entry = line.Trim();
            if (entry.StartsWith('#')) continue;
            var (permission, target) = ParsePermission(entry);
            if (permission is null || target is null) continue;
            if (permission == "readwrite")
            {
                result.Values["read"].Add(target);
                result.Values["write"].Add(target);
            }
            else result.Values[permission].Add(target);
        }
        return result;
    }

    private static string[] AllowedLines(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split(['\n', '\r', '\v', '\f', '\x1c', '\x1d', '\x1e', '\x85', '\u2028', '\u2029']);

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (!OperatingSystem.IsWindows())
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

        // Windows X_OK marks static .allowed text executable. Probe the actual PE
        // signature instead, reading only the fixed DOS header and four PE bytes.
        using var stream = File.OpenRead(path);
        Span<byte> dos = stackalloc byte[64];
        if (stream.Length < dos.Length) return false;
        stream.ReadExactly(dos);
        if (dos[0] != 'M' || dos[1] != 'Z') return false;
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos[60..]);
        if (peOffset < dos.Length || peOffset > stream.Length - 4) return false;
        stream.Position = peOffset;
        Span<byte> signature = stackalloc byte[4];
        stream.ReadExactly(signature);
        return signature.SequenceEqual("PE\0\0"u8);
    }

    private static string ReadAllowed(string path)
    {
        EnsureUnlinked(path);
        if (!File.Exists(path)) return "";
        using var stream = File.OpenRead(path);
        var bytes = new byte[MaxPermissionBytes + 1];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > MaxPermissionBytes) throw new InvalidDataException("Permission file exceeds limit.");
        return Utf8.GetString(bytes, 0, count);
    }

    private Permissions LoadAllowed(string path, bool execute = true)
    {
        try
        {
            EnsureUnlinked(path);
            var content = execute && IsExecutable(path)
                ? (PermissionExecutor ?? throw new InvalidOperationException("Executable permission resolvers require explicit operator authorization."))(path)
                : ReadAllowed(path);
            return ParseAllowed(content);
        }
        catch (Exception e)
        {
            Report($"Cannot load permission resolver {path} ({FailureDescription(e)}).");
            return new() { Failed = true };
        }
    }

    private Permissions GroupPermissions(string group)
    {
        var result = LoadAllowed(groups[group] + ".allowed");
        if (configuration.Access.TryGetValue(group, out var configured))
        {
            var added = ParseAllowed(string.Join('\n', configured));
            foreach (var permission in PermissionNames) result.Values[permission].UnionWith(added.Values[permission]);
        }
        return result;
    }

    private static bool? Evaluate(Permissions values, string permission, string hash, bool repositoryOverride)
    {
        if (values.Failed) return false;
        var targets = values.Values[permission];
        if (targets.Contains("none")) return false;
        if (targets.Contains("all") || targets.Contains(hash) || values.Values["admin"].Contains(hash)) return true;
        return repositoryOverride && targets.Count == 0 ? null : false;
    }

    public bool ResolvePermission(Identity? caller, string repository, string permission)
    {
        lock (gate)
        {
            if (caller is null || configuration.BlockedIdentities.Contains(caller.HexHash)
                || !PermissionNames.Contains(permission) || !repositories.TryGetValue(repository, out var found)) return false;
            var own = Evaluate(LoadAllowed(found.Path + ".allowed"), permission, caller.HexHash, true);
            return own ?? Evaluate(GroupPermissions(found.Group), permission, caller.HexHash, false) == true;
        }
    }

    // Upstream intentionally checks the blocklist only in repository resolution, not group/document
    // resolution. Host-wide blocking, if desired, belongs at the authenticated link boundary.
    public bool ResolveGroupPermission(Identity? caller, string group, string permission)
    {
        lock (gate)
            return caller is not null && PermissionNames.Contains(permission) && groups.ContainsKey(group)
                && Evaluate(GroupPermissions(group), permission, caller.HexHash, false) == true;
    }

    public bool ResolveDocumentPermission(Identity? caller, string repository, long docId, string permission)
    {
        lock (gate)
        {
            if (caller is null || permission is "stats" or "release" || !PermissionNames.Contains(permission)
                || !repositories.TryGetValue(repository, out var found)) return false;
            var path = System.IO.Path.Combine(found.Path + ".work", docId.ToString(CultureInfo.InvariantCulture) + ".allowed");
            var doc = LoadAllowed(path, execute: false);
            if (doc.Failed) return false;
            var targets = doc.Values[permission];
            if (targets.Contains("none")) return false;
            if (targets.Contains("all") || targets.Contains(caller.HexHash) || doc.Values["admin"].Contains(caller.HexHash)) return true;
            // Unlike repository overrides, an unmatched nonempty document list falls back.
            return Evaluate(LoadAllowed(found.Path + ".allowed"), permission, caller.HexHash, true)
                ?? Evaluate(GroupPermissions(found.Group), permission, caller.HexHash, false) == true;
        }
    }

    public async Task<object?> HandlePermissionsAsync(object? data, Identity? caller, CancellationToken cancellationToken)
    {
        if (caller is null) return Status(1, "Not identified");
        if (!IsMap(data)) return Status(2, "Invalid request");
        using var repositoryLease = Get(data, "operation") as string == "rperms"
            && Get(data, 0) is string repository && Find(repository) is not null
                ? await LockRepositoryAsync(repository, cancellationToken).ConfigureAwait(false) : null;
        await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                var operation = Get(data, "operation") as string;
                if (operation is not ("gperms" or "rperms")) return Status(2, "Invalid request");
                var groupOperation = operation == "gperms";
                var key = groupOperation ? 2 : 0;
                if (Get(data, key) is null) return Status(2, groupOperation ? "No group specified" : "No repository specified");
                if (Get(data, key) is not string scope) return Status(255, "Remote error");
                string group;
                RngitHostedRepository? repo = null;
                if (groupOperation) group = ValidComponent(scope) ? scope : "";
                else
                {
                    TryRepository(scope, out group, out _);
                    repo = Find(scope);
                }
                var read = groupOperation ? ResolveGroupPermission(caller, group, "read") : ResolvePermission(caller, scope, "read");
                var admin = groupOperation ? ResolveGroupPermission(caller, group, "admin") : ResolvePermission(caller, scope, "admin");
                if (!read) return Status(3, "Not found");
                if (!admin) return Status(1, "Not allowed");
                var step = Get(data, "step") as string;
                if (string.IsNullOrEmpty(step)) return Status(2, "Invalid request");
                if (step is not ("get" or "set")) return Status(2, "Invalid step");
                if (step == "set" && !groupOperation)
                {
                    // Pinned server requires group (not merely repository) admin to persist a change.
                    if (!ResolveGroupPermission(caller, group, "read")) return Status(3, "Not found");
                    if (!ResolveGroupPermission(caller, group, "admin")) return Status(1, "Not allowed");
                }
                var path = (groupOperation ? groups[group] : repo!.Path) + ".allowed";
                if (step == "get") return RngitWire.Packed(new Dictionary<object, object?> { ["content"] = ReadAllowed(path) });
                var raw = Get(data, "content");
                if (raw is not null && raw is not string) return Status(255, "Remote error");
                var content = raw as string ?? "";
                if (Utf8.GetByteCount(content) > MaxPermissionBytes) return Status(2, "Permission input exceeds limit");
                var lines = AllowedLines(content);
                if (lines.Length > 16384) return Status(2, "Permission input exceeds line limit");
                for (var i = 0; i < lines.Length; i++)
                {
                    var entry = lines[i].Trim();
                    if (entry.Length == 0 || entry.StartsWith('#')) continue;
                    var parsed = ParsePermission(entry);
                    if (parsed.Permission is null || parsed.Target is null)
                        return Status(2, $"Invalid permission \"{entry}\" on line {i + 1}");
                }
                EnsureUnlinked(path);
                if (IsExecutable(path)) return Status(1, "Executable permission resolvers can only be modified node-side");
                PublishAllowed(path, content);
                return Status(0);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Report($"Permission request failed ({FailureDescription(e)}).");
            return Status(255, Get(data, "step") as string == "get" ? "Error getting permissions" : "Error setting permissions");
        }
        finally { mutation.Release(); }
    }

    private static void PublishAllowed(string path, string content)
    {
        var staging = path + ".write-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(Utf8.GetBytes(content));
            EnsureUnlinked(path);
            File.Move(staging, path, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    /// <summary>All-only page policy; anonymous callers never acquire identity-specific/admin grants.</summary>
    public bool ResolveAnonymousGroupPermission(string group, string permission)
    {
        lock (gate) return groups.ContainsKey(group) && PermissionNames.Contains(permission)
            && Evaluate(GroupPermissions(group), permission, "", false) == true;
    }

    public bool ResolveAnonymousPermission(string repository, string permission)
    {
        lock (gate)
        {
            if (!PermissionNames.Contains(permission) || !repositories.TryGetValue(repository, out var repo)) return false;
            return Evaluate(LoadAllowed(repo.Path + ".allowed"), permission, "", true)
                ?? Evaluate(GroupPermissions(repo.Group), permission, "", false) == true;
        }
    }

    public bool ResolveAnonymousDocumentPermission(string repository, long id, string permission)
    {
        lock (gate)
        {
            if (permission is "stats" or "release" || !PermissionNames.Contains(permission) || !repositories.TryGetValue(repository, out var repo)) return false;
            var doc = LoadAllowed(System.IO.Path.Combine(repo.Path + ".work", id.ToString(CultureInfo.InvariantCulture) + ".allowed"), execute: false);
            if (doc.Failed || doc.Values[permission].Contains("none")) return false;
            return doc.Values[permission].Contains("all") || ResolveAnonymousPermission(repository, permission);
        }
    }
}
