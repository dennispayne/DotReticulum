using System.Globalization;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public sealed partial class RngitRepositoryStore
{
    /// <summary>Native RNS clone/fetch adapter; clone destinations do not yet exist.</summary>
    public Func<string, string, bool, CancellationToken, Task>? TransferRepositoryAsync { get; set; }

    /// <summary>
    /// Explicit operator policy for non-RNS URLs. Default denies all external protocols.
    /// file URLs are an opt-in local testing extension, absent in the pinned upstream.
    /// </summary>
    public Func<string, bool>? AuthorizeExternalSource { get; set; }

    public TimeSpan MirrorInterval { get; set; } = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim periodicSync = new(1, 1);

    public Task<object?> HandleCreateAsync(object? data, Identity? caller, CancellationToken cancellationToken) =>
        CreateAsync(data, caller, null, cancellationToken);
    public Task<object?> HandleForkAsync(object? data, Identity? caller, CancellationToken cancellationToken) =>
        CreateAsync(data, caller, "fork", cancellationToken);
    public Task<object?> HandleMirrorAsync(object? data, Identity? caller, CancellationToken cancellationToken) =>
        CreateAsync(data, caller, "mirror", cancellationToken);

    private bool SourceAllowed(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || source.Any(char.IsControl)) return false;
        if (uri.Scheme == "rns") return true;
        bool authorized;
        try { authorized = uri.Scheme is "http" or "https" or "ssh" or "file" && AuthorizeExternalSource?.Invoke(source) == true; }
        catch (Exception e)
        {
            Report($"External repository authorization callback failed ({e.GetType().Name}).");
            return false;
        }
        if (!authorized) Report("External repository source rejected: explicit operator authorization required.");
        return authorized;
    }

    private async Task<object?> CreateAsync(object? data, Identity? caller, string? type, CancellationToken token)
    {
        if (caller is null) return Status(1, "Not identified");
        if (!IsMap(data)) return Status(2, "Invalid request");
        if (Get(data, 0) is null) return Status(2, "No repository specified");
        if (type is not null)
        {
            var raw = Get(data, "source");
            if (raw is null or "" || raw is false || raw is 0) return Status(2, "No source specified");
            if (raw is not string source) return Status(2, "Invalid source URL");
            if (!SourceAllowed(source)) return Status(1, "Prohibited source URL");
        }
        if (Get(data, 0) is not string repository || !TryRepository(repository, out var group, out var name))
            return Status(2, "Invalid request");
        await mutation.WaitAsync(token).ConfigureAwait(false);
        string? staging = null;
        string? target = null;
        var published = false;
        var allowedPublished = false;
        try
        {
            lock (gate)
            {
                if (!groups.TryGetValue(group, out var groupPath) || !Directory.Exists(groupPath)) return Status(3, "Not found");
                if (!ResolveGroupPermission(caller, group, "create"))
                    return Status(ResolveGroupPermission(caller, group, "read") ? (byte)1 : (byte)3,
                        ResolveGroupPermission(caller, group, "read") ? "Not allowed" : "Not found");
                EnsureUnlinked(groupPath);
                target = System.IO.Path.Combine(groupPath, name);
                if (Find(repository) is not null || Directory.Exists(target) || File.Exists(target))
                    return ResolvePermission(caller, repository, "read") ? Status(1, "Repository already exists") : Status(3, "Not found");
                // Never overwrite an operator-owned sibling permission file.
                if (File.Exists(target + ".allowed")) return Status(255, "Could not initialize repository");
                staging = System.IO.Path.Combine(groupPath, ".rngit-store-" + Guid.NewGuid().ToString("N"));
            }
            if (type is null)
            {
                Directory.CreateDirectory(staging);
                await GitAsync(staging, ["init", "--bare", "--object-format=sha1", "."], token).ConfigureAwait(false);
            }
            else
            {
                var source = (string)Get(data, "source")!;
                await TransferAsync(source, staging, false, token).ConfigureAwait(false);
                await GitAsync(staging, ["config", "repository.rngit.type", type], token).ConfigureAwait(false);
                await GitAsync(staging, ["config", "repository.rngit.upstream.source", source], token).ConfigureAwait(false);
                await SetSyncTimeAsync(staging, token).ConfigureAwait(false);
            }
            if (!await IsBareAsync(staging, token).ConfigureAwait(false)) return Status(255, "Failed to register repository");
            token.ThrowIfCancellationRequested();
            PublishAllowed(target + ".allowed", "adm:" + caller.HexHash);
            allowedPublished = true;
            await Runtime.FileSystemSharingRetry.ExecuteAsync(() => Directory.Move(staging, target), token).ConfigureAwait(false);
            published = true;
            lock (gate) repositories.Add(repository, new(group, name, target));
            return Status(0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Report($"Repository {type ?? "create"} failed ({FailureDescription(e)}).");
            return Status(255, type is null ? "Could not initialize repository" : "Remote error");
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging))
                try { DeleteOwnedDirectory(staging); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Report($"Staging cleanup failed ({FailureDescription(e)})."); }
            if (!published && allowedPublished && target is not null)
                try { File.Delete(target + ".allowed"); } catch (IOException e) { Report($"Permission cleanup failed ({FailureDescription(e)})."); }
            mutation.Release();
        }
    }

    private async Task TransferAsync(string source, string path, bool fetch, CancellationToken token)
    {
        if (!SourceAllowed(source)) throw new InvalidOperationException("Prohibited source URL");
        var uri = new Uri(source);
        if (uri.Scheme == "rns")
        {
            var transfer = TransferRepositoryAsync ?? throw new InvalidOperationException("Native RNS repository transfer adapter is not configured.");
            string? head = null;
            var fork = fetch && await ConfigAsync(path, "repository.rngit.type", token).ConfigureAwait(false) == "fork";
            if (fork)
                try { head = (await GitAsync(path, ["symbolic-ref", "HEAD"], token).ConfigureAwait(false)).Trim(); }
                catch (IOException) { }
            try { await transfer(source, path, fetch, token).ConfigureAwait(false); }
            finally
            {
                if (fork && head is not null)
                    await GitAsync(path, ["symbolic-ref", "HEAD", head], CancellationToken.None).ConfigureAwait(false);
            }
            return;
        }
        if (!fetch)
        {
            Directory.CreateDirectory(path);
            await GitAsync(path, ["init", "--bare", "--object-format=sha1", "."], token).ConfigureAwait(false);
        }
        await ExternalGitAsync(path, uri.Scheme, ["fetch", "--", source, "+refs/*:refs/*"], token).ConfigureAwait(false);
        if (!fetch || await ConfigAsync(path, "repository.rngit.type", token).ConfigureAwait(false) == "mirror")
            await UpdateSourceHeadAsync(path, source, uri.Scheme, token).ConfigureAwait(false);
    }

    private static async Task<string> ExternalGitAsync(string path, string protocol, string[] args, CancellationToken token)
    {
        EnsureUnlinked(path);
        // protocol.allow=never disables helper/ext/file redirects; only this approved protocol
        // is opened. HTTP redirects and SSH ProxyCommand config require separate operator trust.
        var arguments = new[] { "--no-pager", "--git-dir=" + path, "-c", "core.hooksPath=", "-c", "protocol.allow=never",
            "-c", "protocol." + protocol + ".allow=always", "-c", "http.followRedirects=false" }.Concat(args).ToArray();
        var result = await RngitProcess.RunAsync("git", arguments, path, timeout: TimeSpan.FromMinutes(2),
            maxOutputBytes: 1024 * 1024, stop: token, environment: GitEnvironment()).ConfigureAwait(false);
        if (result.Code != 0) throw new RepositoryGitFailure(true, result.Code);
        return Utf8.GetString(result.Output);
    }

    private static async Task UpdateSourceHeadAsync(string path, string source, string protocol, CancellationToken token)
    {
        string? branch = null;
        try
        {
            var remote = await ExternalGitAsync(path, protocol, ["ls-remote", "--symref", "--", source, "HEAD"], token).ConfigureAwait(false);
            branch = remote.Split('\n').Select(l => l.TrimEnd('\r').Split('\t'))
                .Where(p => p.Length == 2 && p[1] == "HEAD" && p[0].StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                .Select(p => p[0][5..]).FirstOrDefault();
            if (branch is not null)
                await GitAsync(path, ["show-ref", "--verify", "--quiet", branch], token).ConfigureAwait(false);
        }
        catch (IOException) { branch = null; }
        if (branch is null)
            branch = (await GitAsync(path, ["for-each-ref", "--format=%(refname)", "refs/heads", "--count=1"], token).ConfigureAwait(false)).Trim();
        if (branch.Length != 0) await GitAsync(path, ["symbolic-ref", "HEAD", branch], token).ConfigureAwait(false);
    }

    private static async Task<string> ConfigAsync(string path, string name, CancellationToken token)
    {
        try { return (await GitAsync(path, ["config", "--get", name], token).ConfigureAwait(false)).Trim(); }
        catch (IOException) { return ""; }
    }

    private static Task<string> SetSyncTimeAsync(string path, CancellationToken token) =>
        GitAsync(path, ["config", "repository.rngit.upstream.sync", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)], token);

    public async Task<object?> HandleSyncAsync(object? data, Identity? caller, CancellationToken cancellationToken)
    {
        if (caller is null) return Status(1, "Not identified");
        if (!IsMap(data)) return Status(2, "Invalid request");
        if (Get(data, 0) is null) return Status(2, "No repository specified");
        if (Get(data, 0) is not string repository || !ResolvePermission(caller, repository, "read")) return Status(3, "Not found");
        using var lease = await LockRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
        // Recheck after waiting; permissions may have changed.
        if (!ResolvePermission(caller, repository, "read")) return Status(3, "Not found");
        if (!ResolvePermission(caller, repository, "write")) return Status(1, "Not allowed");
        var found = Find(repository)!;
        var type = await ConfigAsync(found.Path, "repository.rngit.type", cancellationToken).ConfigureAwait(false);
        if (type is not ("mirror" or "fork")) return Status(2, "Repository is neither fork nor mirror");
        return await SyncAsync(found, cancellationToken).ConfigureAwait(false) ? Status(0)
            : Status(255, type == "mirror" ? "Mirror sync failed" : "Fork sync failed");
    }

    private async Task<bool> SyncAsync(RngitHostedRepository found, CancellationToken token)
    {
        try
        {
            var source = await ConfigAsync(found.Path, "repository.rngit.upstream.source", token).ConfigureAwait(false);
            if (source.Length == 0) return false;
            await TransferAsync(source, found.Path, true, token).ConfigureAwait(false);
            try { await SetSyncTimeAsync(found.Path, token).ConfigureAwait(false); }
            catch (IOException e) { Report($"Repository synced but timestamp was not persisted ({FailureDescription(e)})."); }
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Report($"Repository sync failed for {found.Group}/{found.Name} ({FailureDescription(e)}).");
            return false;
        }
    }

    public async Task SyncMirrorsAsync(CancellationToken cancellationToken)
    {
        if (!await periodicSync.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            foreach (var found in Repositories)
            {
                using var lease = await LockRepositoryAsync(found.Group + "/" + found.Name, cancellationToken).ConfigureAwait(false);
                if (await ConfigAsync(found.Path, "repository.rngit.type", cancellationToken).ConfigureAwait(false) != "mirror") continue;
                var last = await ConfigAsync(found.Path, "repository.rngit.upstream.sync", cancellationToken).ConfigureAwait(false);
                _ = long.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out var synced);
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > synced + MirrorInterval.TotalSeconds)
                    await SyncAsync(found, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { periodicSync.Release(); }
    }
}
