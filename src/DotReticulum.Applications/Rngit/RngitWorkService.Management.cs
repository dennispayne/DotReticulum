using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public sealed partial class RngitWorkService
{
    private byte[] Delete(string work, object data, Identity caller, string repository, CancellationToken token)
    {
        if (!ValidScope(data, "active")) return Status(2, "Invalid request");
        var raw = Get(data, "doc_id");
        if (raw is null) return Status(2, "No document ID specified");
        if (!TryId(raw, out var id)) return Status(2, "Invalid document ID");
        var (_, dir) = Locate(work, id, Scopes);
        if (dir is null) return Status(255, "Remote error");
        var root = Path.Combine(dir, "root");
        if (!File.Exists(root)) return Status(3, "Document not found");
        var doc = Load(root);
        if (doc is null) return Status(255, "Error loading document");
        if (!IsAuthor(doc, caller) && !store.ResolveDocumentPermission(caller, repository, id, "admin"))
            return Status(1, "No access, not author");
        var allowed = Path.Combine(work, Id(id) + ".allowed");
        EnsureUnlinked(allowed);
        token.ThrowIfCancellationRequested();
        // Upstream unlinks first and fails if absent; do not invent transactional rollback.
        if (!File.Exists(allowed))
        {
            Report($"Cannot delete permissions for document {id}: file does not exist.");
            return Status(255, "Remote error");
        }
        File.Delete(allowed);
        CheckTree(dir, token);
        Directory.Delete(dir, recursive: true);
        return Status(0);
    }

    private static void CheckTree(string root, CancellationToken token)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        var count = 0;
        while (directories.TryPop(out var dir))
        {
            foreach (var entry in Entries(dir, token))
            {
                if (++count > EntryLimit) throw new InvalidDataException("Work tree entry limit exceeded.");
                if (Directory.Exists(entry)) directories.Push(entry);
            }
        }
    }

    private byte[] Transition(string work, object data, Identity caller, string repository, bool complete, CancellationToken token)
    {
        var raw = Get(data, "doc_id");
        if (raw is null) return Status(2, "No document ID specified");
        if (!TryId(raw, out var id)) return Status(2, "Invalid document ID");
        var (_, dir) = Locate(work, id, complete ? ["active"] : ["completed", "proposed"]);
        if (dir is null) return Status(3, "Document not found");
        var doc = Load(Path.Combine(dir, "root"));
        if (doc is null) return Status(255, "Error loading document");
        if (!IsAuthor(doc, caller) && !store.ResolveDocumentPermission(caller, repository, id, "admin"))
            return Status(1, "Not allowed");
        token.ThrowIfCancellationRequested();
        var scope = complete ? "completed" : "active";
        var target = Path.Combine(work, scope, Id(id));
        EnsureUnlinked(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // shutil.move places the source directory inside an existing destination directory.
        if (Directory.Exists(target)) target = Path.Combine(target, Id(id));
        EnsureUnlinked(target);
        Directory.Move(dir, target);
        return BoundedPacked(Map(("id", id), ("scope", scope)));
    }

    private byte[] Permissions(string work, object data, Identity caller, string repository, CancellationToken token)
    {
        if (!store.ResolvePermission(caller, repository, "read")) return Status(3, "Not found");
        var manage = store.ResolvePermission(caller, repository, "write") && store.ResolvePermission(caller, repository, "interact");
        if (!manage) return Status(1, "Not allowed");
        var step = Get(data, "step");
        if (!Truthy(step)) return Status(2, "Invalid request");
        if (step as string is not ("get" or "set")) return Status(2, "Invalid step");
        var raw = Get(data, "doc_id");
        if (raw is null) return Status(2, "No document ID specified");
        if (!TryId(raw, out var id)) return Status(2, "Invalid document ID");
        var (_, dir) = Locate(work, id, Scopes);
        if (dir is null) return Status(3, "Document not found");
        var doc = Load(Path.Combine(dir, "root"));
        if (doc is null) return Status(255, "Error loading document");
        if (!(IsAuthor(doc, caller) && manage) && !store.ResolveDocumentPermission(caller, repository, id, "admin"))
            return Status(1, "Not allowed");
        var path = Path.Combine(work, Id(id) + ".allowed");
        var text = Equals(step, "set") ? String(data, "content") : "";
        try
        {
            EnsureUnlinked(path);
            if (Equals(step, "get"))
            {
                string content = "";
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    if (stream.Length > ContentLimit) throw new InvalidDataException("Permission file limit exceeded.");
                    var bytes = new byte[checked((int)stream.Length)];
                    stream.ReadExactly(bytes);
                    content = Utf8.GetString(bytes);
                }
                return BoundedPacked(Map(("content", content)));
            }
            if (Utf8.GetByteCount(text) > ContentLimit) return Status(2, "Content limit exceeded");
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            if (lines.Length > EntryLimit) return Status(2, "Content limit exceeded");
            for (var i = 0; i < lines.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = lines[i].Trim();
                if (entry.Length == 0 || entry.StartsWith('#')) continue;
                if (!store.IsValidPermissionEntry(entry)) return Status(2, $"Invalid permission \"{entry}\" on line {i + 1}");
            }
            Publish(path, Utf8.GetBytes(text), token);
            return Status(0);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Report($"Document {id} permissions {step} failed: {e.Message}");
            return Status(255, Equals(step, "get") ? "Error getting permissions" : "Error setting permissions");
        }
    }
}
