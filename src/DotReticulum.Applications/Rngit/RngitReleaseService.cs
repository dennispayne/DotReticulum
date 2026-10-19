using System.Globalization;
using System.Text;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rngit;

/// <summary>
/// RNS 1.5.7 server.py:3367-3790 release management. HandleAsync owns the shared
/// repository mutation lease. Callers authorize page helpers and hold their lease.
/// Drafts deliberately survive disconnects; publication does not require a manifest upstream.
/// </summary>
public sealed partial class RngitReleaseService(RngitRepositoryStore store, Action<string>? diagnostic = null)
{
    public const int MaximumArtifactBytes = 256 * 1024 * 1024;
    public const int MaximumNotesBytes = 512 * 1024;
    public const int MaximumArtifacts = 1024;
    public const int MaximumReleases = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<object?> HandleAsync(object? data, Identity? caller, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caller is null) return RngitWire.Status(1, "Not identified");
        if (data is not Dictionary<object, object?>) return RngitWire.Status(2, "Invalid request");
        if (RngitWire.Get(data, 0) is null) return RngitWire.Status(2, "No repository specified");
        var operation = RngitWire.Text(data, "operation");
        if (string.IsNullOrEmpty(operation)) return RngitWire.Status(2, "Invalid request");
        var repositoryName = RngitWire.Text(data, 0);
        if (repositoryName is null) return RngitWire.Status(255, "Remote error");
        var repository = store.Find(repositoryName);
        if (repository is null || !store.ResolvePermission(caller, repositoryName, "read"))
            return RngitWire.Status(3, "Not found");
        try
        {
            using var lease = await store.LockRepositoryAsync(repositoryName, cancellationToken).ConfigureAwait(false);
            repository = store.Find(repositoryName);
            if (repository is null || !store.ResolvePermission(caller, repositoryName, "read"))
                return RngitWire.Status(3, "Not found");
            if (operation is not ("list" or "view" or "fetch")
                && (operation is not ("create" or "delete" or "latest")
                    || !store.ResolvePermission(caller, repositoryName, "release")))
                return RngitWire.Status(1, "Not allowed");
            var root = Root(repository);
            if (operation == "list") return RngitWire.Packed(await ReadListAsync(repository, cancellationToken).ConfigureAwait(false));
            var tag = RngitWire.Text(data, "tag");
            var step = RngitWire.Text(data, "step");
            if (operation == "create" && step is not ("init" or "artifact" or "finalize"))
                return RngitWire.Status(2, "Invalid request");
            if (string.IsNullOrEmpty(tag))
                return RngitWire.Status(2, operation is "delete" or "latest" || step == "finalize" ? "No tag specified"
                    : step == "artifact" ? "Missing tag or artifact name" : "Invalid tag specified");
            if (!ValidName(tag)) return RngitWire.Status(2, "Invalid tag specified");
            if (operation is "view" or "fetch" && tag == "latest")
            {
                tag = await LatestAsync(root, cancellationToken).ConfigureAwait(false);
                if (tag is null) return RngitWire.Status(3, "No latest release found");
            }
            var directory = Child(root, tag);
            if (operation == "create" && step == "init")
                return await InitializeAsync(repository, tag, data, caller, cancellationToken).ConfigureAwait(false);
            if (operation == "fetch")
            {
                var artifact = RngitWire.Text(data, "artifact");
                if (!ValidName(artifact)) return RngitWire.Status(2, "Invalid artifact specified");
                if (!Directory.Exists(directory)) return RngitWire.Status(3, "Release not found");
                return (object?)await ReadArtifactAsync(repository, tag, artifact!, cancellationToken).ConfigureAwait(false)
                    ?? RngitWire.Status(3, "Artifact not found");
            }
            if (!Directory.Exists(directory)) return RngitWire.Status(3, "Release not found");
            if (operation == "view")
            {
                var result = await ReadReleaseAsync(repository, tag, cancellationToken).ConfigureAwait(false);
                return result is null ? RngitWire.Status(255, "Error getting release data") : RngitWire.Packed(result);
            }
            if (operation == "delete")
            {
                ValidateTree(directory, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(directory, recursive: true);
                return RngitWire.Status(0);
            }
            if (operation == "latest")
            {
                await WriteAsync(Child(root, "latest"), Utf8.GetBytes(tag), cancellationToken).ConfigureAwait(false);
                return RngitWire.Status(0);
            }
            var metaPath = Child(directory, "META");
            var meta = await ReadMetaAsync(metaPath, cancellationToken).ConfigureAwait(false);
            if (meta.GetValueOrDefault("status") != "draft")
                return RngitWire.Status(1, "Release was finalized and is not writable");
            if (step == "artifact")
            {
                var name = RngitWire.Text(data, "artifact_name");
                if (string.IsNullOrEmpty(name)) return RngitWire.Status(2, "Missing tag or artifact name");
                if (!ValidName(name)) return RngitWire.Status(2, "Invalid artifact specified");
                var value = RngitWire.Get(data, "artifact_data");
                if (value is null) return RngitWire.Status(2, "No artifact data");
                var bytes = value switch { byte[] binary => binary, string text => EncodeNotes(text, MaximumArtifactBytes), _ => null };
                if (bytes is null || bytes.Length > MaximumArtifactBytes) return RngitWire.Status(2, "Invalid artifact data");
                var artifacts = Child(directory, "artifacts");
                Directory.CreateDirectory(artifacts);
                var artifactPath = Child(artifacts, name);
                if (!File.Exists(artifactPath) && Entries(artifacts).Length >= MaximumArtifacts)
                    return RngitWire.Status(2, "Artifact limit exceeded");
                await WriteAsync(artifactPath, bytes, cancellationToken).ConfigureAwait(false);
                return RngitWire.Status(0);
            }
            meta["status"] = "published";
            meta["published_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            await WriteMetaAsync(metaPath, meta, cancellationToken).ConfigureAwait(false);
            try { await WriteAsync(Child(root, "latest"), Utf8.GetBytes(tag), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Report($"Release {tag} published but latest was not updated: {e.Message}"); }
            return RngitWire.Status(0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Report($"Release request {repositoryName}/{operation} failed: {e.Message}");
            return RngitWire.Status(255, "Remote error");
        }
    }

    private async Task<byte[]> InitializeAsync(RngitHostedRepository repository, string tag, object data,
        Identity caller, CancellationToken token)
    {
        var hash = RngitWire.Get(data, "hash");
        if (hash is not null && (hash is not string sha || !RngitWire.ValidSha(sha)))
            return RngitWire.Status(2, "Invalid commit hash");
        var notesValue = RngitWire.Get(data, "notes");
        if (notesValue is not null && notesValue is not string) return RngitWire.Status(2, "Invalid release notes");
        var notes = EncodeNotes(notesValue as string ?? "", MaximumNotesBytes);
        var format = RngitWire.Get(data, "notes_format");
        if (format is not null && format is not string) return RngitWire.Status(2, "Invalid notes format");
        var check = await RngitProcess.RunAsync("git", ["--git-dir=" + repository.Path, "rev-parse", "--verify", $"refs/tags/{tag}"],
            repository.Path, timeout: TimeSpan.FromSeconds(30), maxOutputBytes: 65536, stop: token).ConfigureAwait(false);
        if (check.Code < 0) throw new IOException($"Cannot check release tag: {check.Error}");
        if (check.Code != 0) return RngitWire.Status(2, $"Tag '{tag}' does not exist in repository");
        var root = Root(repository);
        Directory.CreateDirectory(root);
        var directory = Child(root, tag);
        if (Directory.Exists(directory)) return RngitWire.Status(1, "Release already exists");
        if (Entries(root).Count(Directory.Exists) >= MaximumReleases) return RngitWire.Status(2, "Release limit exceeded");
        // Publish a complete draft atomically, rather than exposing half-written initial metadata.
        var staging = Child(root, ".draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            Directory.CreateDirectory(Child(staging, "artifacts"));
            var meta = new Dictionary<string, string> { ["tag"] = tag, ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ["status"] = "draft", ["created_by"] = caller.HexHash };
            if (hash is string commit) meta["hash"] = commit;
            await WriteMetaAsync(Child(staging, "META"), meta, token).ConfigureAwait(false);
            if (notes.Length != 0) await WriteAsync(Child(staging, format as string == "micron" ? "RELEASE.mu" : "RELEASE.md"), notes, token).ConfigureAwait(false);
            await WriteAsync(Child(staging, "THANKS"), MsgPack.Pack(new Dictionary<object, object?> { ["count"] = 0 }), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, directory);
            return RngitWire.Status(0);
        }
        finally
        {
            if (Directory.Exists(staging))
                try { Directory.Delete(staging, true); }
                catch (Exception e) { Report($"Cannot clean release draft staging: {e.Message}"); }
        }
    }
}
