using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public sealed partial class RngitWorkService
{
    private byte[] List(string work, object data, Func<long, bool> read, CancellationToken token)
    {
        var scope = Value(data, "scope", "active");
        var result = Map(("active", Array.Empty<object?>()), ("completed", Array.Empty<object?>()), ("proposed", Array.Empty<object?>()));
        var budget = 1024;
        foreach (var folder in Scopes)
        {
            if (!Equals(scope, folder) && !Equals(scope, "all")) continue;
            var documents = new List<Dictionary<object, object?>>();
            foreach (var dir in Entries(Path.Combine(work, folder), token))
            {
                if (!Directory.Exists(dir) || !TryId(Path.GetFileName(dir), out var id) || !read(id)) continue;
                var root = Path.Combine(dir, "root");
                if (!File.Exists(root)) continue;
                var doc = Load(root);
                if (doc is null) continue;
                var meta = (Dictionary<object, object?>)Get(doc, "meta")!;
                var count = Entries(dir, token).Count(f => File.Exists(f) && DecimalName(Path.GetFileName(f)));
                var summary = Map(("id", id), ("title", Value(meta, "title", "Untitled")),
                    ("created", Value(meta, "created", 0L)), ("edited", Value(meta, "edited", 0L)),
                    ("author", Author(meta)), ("format", Value(meta, "format", "markdown")), ("comments", count));
                Reserve(summary, ref budget);
                documents.Add(summary);
            }
            documents.Sort((a, b) => MsgPack.ToDouble(Get(b, "created")).CompareTo(MsgPack.ToDouble(Get(a, "created"))));
            result[folder] = documents.Cast<object?>().ToArray();
        }
        return BoundedPacked(result);
    }

    private byte[] View(string work, object data, CancellationToken token)
    {
        if (!ValidScope(data, "all")) return Status(2, "Invalid request");
        var raw = Get(data, "doc_id");
        if (raw is null) return Status(2, "No document ID specified");
        if (!TryId(raw, out var id)) return Status(2, "Invalid document ID");
        var (scope, dir) = Locate(work, id, Scopes);
        if (dir is null) return Status(3, "Not found");
        var root = Path.Combine(dir, "root");
        if (!File.Exists(root)) return Status(3, "Document not found");
        var doc = Load(root);
        if (doc is null) return Status(255, "Error loading document");
        var budget = MsgPack.Pack(doc).Length + 1024;
        var comments = new List<Dictionary<object, object?>>();
        foreach (var entry in Entries(dir, token))
        {
            var name = Path.GetFileName(entry);
            if (!DecimalName(name) || !File.Exists(entry) || !TryId(name, out var commentId)) continue;
            var comment = Load(entry);
            if (comment is null) continue;
            var cm = (Dictionary<object, object?>)Get(comment, "meta")!;
            var summary = Map(("id", commentId), ("content", Value(comment, "content", "")),
                ("created", Value(cm, "created", 0L)), ("edited", Value(cm, "edited", 0L)),
                ("author", Author(cm)), ("format", Value(cm, "format", "markdown")));
            Reserve(summary, ref budget);
            comments.Add(summary);
        }
        comments.Sort((a, b) => ((long)a["id"]!).CompareTo((long)b["id"]!));
        var meta = (Dictionary<object, object?>)Get(doc, "meta")!;
        return BoundedPacked(Map(("id", id), ("scope", scope), ("content", Value(doc, "content", "")),
            ("comments", comments.Cast<object?>().ToArray()),
            ("meta", Map(("title", Value(meta, "title", "Untitled")), ("created", Value(meta, "created", 0L)),
                ("edited", Value(meta, "edited", 0L)), ("author", Author(meta)), ("identity", Value(meta, "identity", null)),
                ("signature", Value(meta, "signature", null)), ("format", Value(meta, "format", "markdown"))))));
    }

    private static byte[] BoundedPacked(object result)
    {
        var packed = RngitWire.Packed(result);
        if (packed.Length > StorageLimit) throw new InvalidDataException("Work response storage limit exceeded.");
        return packed;
    }

    private static void Reserve(object value, ref int budget)
    {
        budget = checked(budget + MsgPack.Pack(value).Length);
        if (budget > StorageLimit) throw new InvalidDataException("Work response storage limit exceeded.");
    }

    private static byte[]? ValidateSignature(object data, Identity caller, string content)
    {
        var value = Get(data, "signature");
        if (!Truthy(value)) return Status(2, "No signature provided");
        var length = value switch
        {
            byte[] b => b.Length, string s => Characters(s), object?[] a => a.Length,
            System.Collections.IDictionary map => map.Count,
            _ => throw new InvalidDataException("Signature has no length."),
        };
        if (length != 64) return Status(2, "Invalid signature length");
        return value is byte[] signature && caller.Verify(signature, Utf8.GetBytes(content))
            ? null : Status(2, "Invalid signature");
    }

    private byte[] Create(string work, object data, Identity caller, bool proposed, CancellationToken token)
    {
        var title = String(data, "title").Trim();
        var content = String(data, "content").Trim();
        var format = String(data, "format", "markdown");
        var error = ValidateSignature(data, caller, content);
        if (error is not null) return error;
        if (TooLarge(title, content, format)) return Status(2, "Content limit exceeded");
        if (title.Length == 0) return Status(2, "Title is required");
        if (content.Length == 0) return Status(2, "Content is required");
        var id = Scopes.Max(s => NextId(Path.Combine(work, s), token));
        var scope = proposed ? "proposed" : "active";
        var now = Clock();
        var doc = Map(("content", content), ("meta", Map(("format", Format(data)), ("title", title),
            ("created", now), ("edited", now), ("author", caller.Hash.ToArray()),
            ("signature", Get(data, "signature")), ("identity", caller.ExportPublicKey()))));
        if (!Save(Path.Combine(work, scope, Id(id), "root"), doc, token)) return Status(255, "Error saving document");
        if (proposed)
        {
            try
            {
                Publish(Path.Combine(work, Id(id) + ".allowed"),
                    Utf8.GetBytes($"i:{caller.HexHash}\nw:{caller.HexHash}\n"), token);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Report($"Cannot set document {id} ownership: {e.Message}");
                return Status(255, "Error setting document ownership");
            }
        }
        return BoundedPacked(Map(("id", id), ("scope", scope)));
    }

    private byte[] Edit(string work, object data, Identity caller, CancellationToken token)
    {
        var title = String(data, "title");
        var content = String(data, "content");
        if (!ValidScope(data, "active")) return Status(2, "Invalid request");
        var error = ValidateSignature(data, caller, content);
        if (error is not null) return error;
        if (TooLarge(title, content)) return Status(2, "Content limit exceeded");
        if (title.Length == 0 && content.Length == 0) return Status(2, "No changes specified");
        var raw = Get(data, "doc_id");
        if (!Truthy(raw)) return Status(2, "No document ID specified");
        if (!TryId(raw, out var id)) return Status(2, "Invalid document ID");
        var (scope, dir) = Locate(work, id, Scopes);
        if (dir is null) return Status(3, "Not found");
        var root = Path.Combine(dir, "root");
        if (!File.Exists(root)) return Status(3, "Document not found");
        var doc = Load(root);
        if (doc is null) return Status(255, "Error loading document");
        if (!IsAuthor(doc, caller)) return Status(1, "No access, not author");
        var meta = (Dictionary<object, object?>)Get(doc, "meta")!;
        if (title.Length != 0) meta["title"] = title.Trim();
        if (content.Length != 0) doc["content"] = content.Trim();
        meta["edited"] = Clock();
        meta["signature"] = Get(data, "signature");
        meta["identity"] = caller.ExportPublicKey();
        if (!Save(root, doc, token)) return Status(255, "Error saving document");
        return BoundedPacked(Map(("id", id), ("scope", scope)));
    }

    private byte[] Comment(string work, object data, Identity caller, CancellationToken token)
    {
        var content = String(data, "content").Trim();
        if (!ValidScope(data, "active")) return Status(2, "Invalid request");
        if (TooLarge(content)) return Status(2, "Content limit exceeded");
        var raw = Get(data, "doc_id");
        if (raw is null) return Status(2, "No document ID specified");
        if (!TryId(raw, out var id)) return Status(2, "Invalid document ID");
        if (content.Length == 0) return Status(2, "Content is required");
        var (_, dir) = Locate(work, id, Scopes);
        // Pinned delete/comment dereference a missing scope, yielding Remote error (not Not found).
        if (dir is null) return Status(255, "Remote error");
        if (!File.Exists(Path.Combine(dir, "root"))) return Status(3, "Document not found");
        var commentId = NextId(dir, token);
        var now = Clock();
        var comment = Map(("content", content), ("meta", Map(("format", Format(data)), ("title", null),
            ("created", now), ("edited", now), ("signature", Get(data, "signature")), ("author", caller.Hash.ToArray()))));
        if (!Save(Path.Combine(dir, Id(commentId)), comment, token)) return Status(255, "Error saving comment");
        return BoundedPacked(Map(("id", commentId)));
    }
}
