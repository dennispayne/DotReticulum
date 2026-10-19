using System.Security.Cryptography;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public sealed record RngitReleaseCreateResult(string ManifestPath, string Tag, string? Commit, IReadOnlyList<string> UploadedArtifacts);

/// <summary>
/// Reusable .NET release packaging, identified management requests, and offline verification.
/// Noninteractive callers supply notes instead of opening upstream's interactive editor.
/// Cancellation after init leaves a resumable remote draft; failed uploads never publish.
/// </summary>
public static class RngitReleaseClient
{
    public static async Task<RngitReleaseCreateResult> CreateAsync(RngitManagementClient? client,
        Identity auth, Identity signer, string remote, string target, string? packageName, bool local,
        CancellationToken cancellationToken, string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var address = RngitAddress.Parse(remote);
        var (tag, artifactsPath) = ParseTarget(target);
        if (!RngitReleaseService.ValidName(tag)) throw new ArgumentException("Invalid release tag.");
        if (string.IsNullOrWhiteSpace(notes)) throw new ArgumentException("Nonempty release notes must be supplied.");
        if (!local && client is null) throw new ArgumentException("An identified management client is required.");
        var directory = Path.GetFullPath(artifactsPath);
        RngitReleaseService.EnsureUnlinked(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Specified artifacts directory does not exist");
        var names = Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>()
            .Where(name => !name.EndsWith(".rsg", StringComparison.Ordinal) && !name.EndsWith(".rsm", StringComparison.Ordinal))
            .Take(RngitReleaseService.MaximumArtifacts + 1).ToArray();
        if (names.Length is 0 or > RngitReleaseService.MaximumArtifacts) throw new InvalidDataException("Invalid number of release artifacts.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var name in names)
        {
            var bytes = await RngitReleaseService.ReadBytesAsync(RngitReleaseService.Child(directory, name),
                RngitReleaseService.MaximumArtifactBytes, cancellationToken).ConfigureAwait(false);
            total += bytes.Length;
            if (total > RngitReleaseService.MaximumArtifactBytes) throw new InvalidDataException("Release packaging exceeds the 256 MiB aggregate limit.");
            files.Add(name, bytes);
        }
        // Missing local tags are allowed upstream: the remote init is authoritative.
        var git = await RngitProcess.RunAsync("git", ["rev-parse", "--verify", $"refs/tags/{tag}^{{commit}}"],
            Directory.GetCurrentDirectory(), timeout: TimeSpan.FromSeconds(30), maxOutputBytes: 65536, stop: cancellationToken).ConfigureAwait(false);
        if (git.Code < 0) throw new IOException($"Cannot resolve local release tag: {git.Error}");
        var commit = git.Code == 0 ? System.Text.Encoding.UTF8.GetString(git.Output).Trim() : null;
        var manifest = RngitReleaseManifests.Create(signer, address.DestinationHash, address.Repository,
            packageName ?? address.Repository.Split('/')[1], tag, notes, commit, files, DateTimeOffset.UtcNow);
        var parsed = RngitReleaseManifests.Validate(manifest, signer.Hash.ToArray());
        var upload = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        foreach (var artifact in parsed.Artifacts) upload.Add(artifact.Name + ".rsg", artifact.Signature);
        upload.Add("manifest.rsm", manifest);
        if (upload.Count > RngitReleaseService.MaximumArtifacts) throw new InvalidDataException("Release artifacts plus signatures exceed the 1024 file limit.");
        foreach (var (name, bytes) in upload.Where(pair => !files.ContainsKey(pair.Key)))
            await AtomicWriteAsync(RngitReleaseService.Child(directory, name), bytes, cancellationToken).ConfigureAwait(false);
        if (!local)
        {
            await RequestAsync(client!, new() { [0] = address.Repository, ["operation"] = "create", ["step"] = "init",
                ["tag"] = tag, ["hash"] = commit, ["notes"] = notes, ["notes_format"] = "markdown" }, cancellationToken).ConfigureAwait(false);
            foreach (var (name, bytes) in upload)
                await RequestAsync(client!, new() { [0] = address.Repository, ["operation"] = "create", ["step"] = "artifact",
                    ["tag"] = tag, ["artifact_name"] = name, ["artifact_data"] = bytes }, cancellationToken).ConfigureAwait(false);
            await RequestAsync(client!, new() { [0] = address.Repository, ["operation"] = "create", ["step"] = "finalize", ["tag"] = tag },
                cancellationToken).ConfigureAwait(false);
        }
        return new(Path.Combine(directory, "manifest.rsm"), tag, commit, upload.Keys.ToArray());
    }

    public static async Task<RngitReleaseVerification> VerifyAsync(string manifestPath, byte[]? requiredSigner = null,
        string artifactPattern = "all", CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(manifestPath);
        var bytes = await RngitReleaseService.ReadBytesAsync(path, RngitSignatures.MaximumSignatureBytes, cancellationToken).ConfigureAwait(false);
        var manifest = RngitReleaseManifests.Validate(bytes, requiredSigner);
        var selected = Select(manifest, artifactPattern);
        var results = new List<RngitReleaseArtifactValidation>();
        foreach (var artifact in selected)
        {
            try
            {
                var artifactPath = RngitReleaseService.Child(Path.GetDirectoryName(path)!, artifact.Name);
                var hash = await HashArtifactAsync(artifactPath, cancellationToken).ConfigureAwait(false);
                var valid = RngitSignatures.ValidateRsgHash(artifact.Signature, hash, requiredSigner ?? []).IsValid;
                results.Add(new(artifact.Name, valid, valid ? null : "Artifact signature, contents or required signer does not match"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                results.Add(new(artifact.Name, false, e.Message));
            }
        }
        return new(manifest, results.AsReadOnly());
    }

    public static async Task<RngitReleaseVerification> FetchAsync(RngitManagementClient client, string remote,
        string target, string outputDirectory, byte[]? requiredSigner = null, CancellationToken cancellationToken = default)
    {
        var address = RngitAddress.Parse(remote);
        var (tag, pattern) = ParseTarget(target);
        if (!RngitReleaseService.ValidName(tag)) throw new ArgumentException("Invalid release tag.");
        var directory = Path.GetFullPath(outputDirectory);
        RngitReleaseService.EnsureUnlinked(directory);
        Directory.CreateDirectory(directory);
        async Task<byte[]> Fetch(string name)
        {
            var response = await client.RequestAsync("/mgmt/release", new Dictionary<object, object?> { [0] = address.Repository,
                ["operation"] = "fetch", ["tag"] = tag, ["artifact"] = name }, cancellationToken).ConfigureAwait(false);
            if (response.Metadata is null) { RngitManagementClient.Decode(response.Data); throw new InvalidDataException("Missing artifact metadata"); }
            if (RngitWire.Get(response.Metadata, "name") is not byte[] receivedName
                || !receivedName.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(name)))
                throw new InvalidDataException("Invalid result metadata on fetch response");
            return response.Data;
        }
        var bytes = await Fetch("manifest.rsm").ConfigureAwait(false);
        var manifest = RngitReleaseManifests.Validate(bytes, requiredSigner);
        // Origin is a signed locator, not an independently authenticated publisher identity.
        if (!manifest.Origin.AsSpan().SequenceEqual(address.DestinationHash) || manifest.Repository != address.Repository
            || (tag != "latest" && manifest.Version != tag))
            throw new InvalidDataException("Release manifest does not describe the requested origin or tag.");
        var selected = Select(manifest, pattern);
        var results = new List<RngitReleaseArtifactValidation>();
        foreach (var artifact in selected)
        {
            var path = RngitReleaseService.Child(directory, artifact.Name);
            var existingValid = false;
            if (File.Exists(path))
            {
                var hash = await HashArtifactAsync(path, cancellationToken).ConfigureAwait(false);
                existingValid = RngitSignatures.ValidateRsgHash(artifact.Signature, hash, requiredSigner ?? []).IsValid;
            }
            if (!existingValid)
            {
                var contents = await Fetch(artifact.Name).ConfigureAwait(false);
                if (!RngitSignatures.ValidateRsgHash(artifact.Signature, SHA256.HashData(contents), requiredSigner ?? []).IsValid)
                    throw new InvalidDataException($"Fetched file {artifact.Name} does not match manifest.");
                await AtomicWriteAsync(path, contents, cancellationToken).ConfigureAwait(false);
            }
            results.Add(new(artifact.Name, true, null));
        }
        await AtomicWriteAsync(RngitReleaseService.Child(directory, manifest.Name + "_" + manifest.Version + ".rsm"), bytes, cancellationToken).ConfigureAwait(false);
        return new(manifest, results.AsReadOnly());
    }

    private static RngitReleaseArtifact[] Select(RngitReleaseManifest manifest, string pattern)
    {
        if (pattern.Length is 0 or > 1024 || pattern.Contains('/') || pattern.Contains('\\'))
            throw new ArgumentException("Invalid artifact pattern.");
        var selected = manifest.Artifacts.Where(artifact => pattern == "all"
            || MatchArtifact(pattern, artifact.Name)).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("No available artifacts specified for fetch");
        return selected;
    }

    // Python fnmatch includes bracket classes; FileSystemName's simple wildcard matcher does not.
    private static bool MatchArtifact(string pattern, string name)
    {
        if (OperatingSystem.IsWindows()) { pattern = pattern.ToUpperInvariant(); name = name.ToUpperInvariant(); }
        var memo = new bool?[pattern.Length + 1, name.Length + 1];
        bool Match(int p, int n)
        {
            if (memo[p, n] is bool cached) return cached;
            bool result;
            if (p == pattern.Length) result = n == name.Length;
            else if (pattern[p] == '*') result = Match(p + 1, n) || (n < name.Length && Match(p, n + 1));
            else if (n == name.Length) result = false;
            else if (pattern[p] == '?') result = Match(p + 1, n + 1);
            else if (pattern[p] == '[')
            {
                var start = p + 1;
                var negate = start < pattern.Length && pattern[start] == '!';
                if (negate) start++;
                var search = start;
                if (search < pattern.Length && pattern[search] == ']') search++;
                var end = pattern.IndexOf(']', search);
                if (end < 0) result = name[n] == '[' && Match(p + 1, n + 1);
                else
                {
                    var contains = false;
                    for (var i = start; i < end; i++)
                    {
                        if (i + 2 < end && pattern[i + 1] == '-')
                        {
                            contains |= name[n] >= pattern[i] && name[n] <= pattern[i + 2];
                            i += 2;
                        }
                        else contains |= name[n] == pattern[i];
                    }
                    result = (contains != negate) && Match(end + 1, n + 1);
                }
            }
            else result = pattern[p] == name[n] && Match(p + 1, n + 1);
            memo[p, n] = result;
            return result;
        }
        return Match(0, 0);
    }

    private static (string Tag, string Artifact) ParseTarget(string target)
    {
        var separator = target.IndexOf(':');
        if (separator < 1 || separator == target.Length - 1) throw new ArgumentException("Expected tag:artifacts target.");
        return (target[..separator], target[(separator + 1)..]);
    }

    private static async Task RequestAsync(RngitManagementClient client, Dictionary<object, object?> data, CancellationToken token)
    {
        var result = await client.RequestAsync("/mgmt/release", data, token).ConfigureAwait(false);
        if (result.Metadata is not null) throw new InvalidDataException("Unexpected metadata in release mutation response.");
        RngitManagementClient.Decode(result.Data);
    }

    private static async Task<byte[]> HashArtifactAsync(string path, CancellationToken token)
    {
        RngitReleaseService.EnsureUnlinked(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length > RngitReleaseService.MaximumArtifactBytes) throw new InvalidDataException("Artifact exceeds verification size limit.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > RngitReleaseService.MaximumArtifactBytes) throw new InvalidDataException("Artifact grew beyond verification size limit.");
            hash.AppendData(buffer.AsSpan(0, count));
        }
        return hash.GetHashAndReset();
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        RngitReleaseService.EnsureUnlinked(path);
        var staging = RngitReleaseService.Child(Path.GetDirectoryName(path)!, ".release-client-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(staging, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            RngitReleaseService.EnsureUnlinked(path);
            File.Move(staging, path, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
