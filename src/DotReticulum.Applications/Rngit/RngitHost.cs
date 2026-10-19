using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rngit;

/// <summary>Owns the git.repositories endpoint and bounded asynchronous request processing.</summary>
public sealed class RngitHost : IAsyncDisposable
{
    private readonly ReticulumNode node;
    private readonly Identity identity;
    private readonly LocalDestination destination;
    private readonly LocalDestination? pageDestination;
    private readonly Channel<RequestContext> requests;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task[] workers;
    private readonly ConcurrentDictionary<string, Link> links = new(StringComparer.Ordinal);
    private readonly Action<string> diagnostic;
    private int disposed;

    public RngitHost(ReticulumNode node, RngitHostConfiguration configuration, Action<string>? diagnostic = null,
        int queueCapacity = 256, int concurrency = 4, bool allowExecutable = false, bool allowExternalSources = false)
    {
        if (queueCapacity is < 1 or > 65536 || concurrency is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        this.node = node;
        Configuration = configuration;
        this.diagnostic = message =>
        {
            try
            {
                if (diagnostic is null) Console.Error.WriteLine("rngit: " + message);
                else diagnostic(message);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("rngit diagnostic observer failed: " + ex.Message + "; event: " + message);
            }
        };
        identity = configuration.LoadOrCreateIdentity();
        try
        {
            Func<string, string>? executor = allowExecutable ? path =>
            {
                var result = RngitProcess.RunAsync(path, [], Path.GetDirectoryName(path),
                    timeout: TimeSpan.FromSeconds(8), maxOutputBytes: RngitRepositoryStore.MaxPermissionBytes,
                    stop: shutdown.Token).GetAwaiter().GetResult();
                if (result.Code != 0) throw new IOException($"Permission resolver failed ({result.Code}): {result.Error}");
                return new UTF8Encoding(false, true).GetString(result.Output);
            }
            : null;
            Store = new RngitRepositoryStore(configuration, this.diagnostic, executor);
            if (allowExternalSources)
                Store.AuthorizeExternalSource = source => Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh";
            Statistics = new RngitHostStatistics(configuration, this.diagnostic);
            Releases = new RngitReleaseService(Store, this.diagnostic);
            Work = new RngitWorkService(Store, this.diagnostic);
        }
        catch { identity.Dispose(); shutdown.Dispose(); throw; }
        Store.TransferRepositoryAsync = async (url, directory, fetch, stop) =>
        {
            var client = new RngitClient(node, identity, this.diagnostic);
            if (fetch)
                await client.FetchAsync(url, directory, TimeSpan.FromHours(2), cancellationToken: stop).ConfigureAwait(false);
            else
                await client.CloneAsync(url, directory, TimeSpan.FromHours(2), cancellationToken: stop).ConfigureAwait(false);
        };
        var registered = false;
        var pagesRegistered = false;
        try
        {
            destination = new LocalDestination(identity, Destination.Create(DestinationType.Single, "git", ["repositories"], identity))
            {
                AcceptLinks = true,
                MaxRequestSize = 256 * 1024 * 1024,
                LinkEstablished = Connected,
            };
            requests = System.Threading.Channels.Channel.CreateBounded<RequestContext>(new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = concurrency == 1,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            foreach (var path in new[] { "/git/list", "/git/fetch", "/git/push", "/git/delete",
            "/git/create", "/git/fork", "/git/mirror", "/git/sync", "/mgmt/perms", "/mgmt/release", "/mgmt/work" })
                destination.RegisterRequestHandler(path, Queue, RequestPolicy.All);
            if (configuration.ServeNomadnet)
            {
                Pages = new RngitPageService(Store, configuration, this.diagnostic)
                {
                    Statistics = Statistics,
                    KnownDestinations = node.Router.Engine.Known,
                };
                if (allowExecutable)
                    Pages.Templates = new RngitTemplates(Path.Combine(configuration.DirectoryPath, "templates"),
                        allowExecutable: true, diagnostic: this.diagnostic);
                pageDestination = new LocalDestination(identity, Destination.Create(DestinationType.Single, "nomadnetwork", ["node"], identity))
                {
                    AcceptLinks = true,
                    AppData = Encoding.UTF8.GetBytes(configuration.NodeName),
                    MaxRequestSize = 1024 * 1024,
                    LinkEstablished = Connected,
                };
                foreach (var path in RngitPageService.Paths)
                    pageDestination.RegisterRequestHandler(path, Queue, RequestPolicy.All, autoCompress: path != "/media");
            }
            node.Router.RegisterLocalDestination(destination);
            registered = true;
            if (pageDestination is not null)
            {
                node.Router.RegisterLocalDestination(pageDestination);
                pagesRegistered = true;
            }
            workers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(WorkAsync)).ToArray();
        }
        catch
        {
            if (registered && destination is not null) node.Router.DeregisterLocalDestination(destination);
            if (pagesRegistered && pageDestination is not null) node.Router.DeregisterLocalDestination(pageDestination);
            requests?.Writer.TryComplete();
            identity.Dispose();
            shutdown.Dispose();
            throw;
        }
    }

    public RngitHostConfiguration Configuration { get; }
    public RngitRepositoryStore Store { get; }
    public RngitHostStatistics Statistics { get; }
    public RngitReleaseService Releases { get; }
    public RngitWorkService Work { get; }
    public RngitPageService? Pages { get; }
    public byte[] DestinationHash => destination.Destination.Hash;
    public string DestinationHashHex => Convert.ToHexStringLower(DestinationHash);
    public string IdentityHashHex => identity.HexHash;
    public long MaxTransferBytes { get; init; } = 256L * 1024 * 1024;
    internal int PendingRequests => requests.Reader.Count;

    public async ValueTask AnnounceAsync(CancellationToken stop = default)
    {
        await node.Router.AnnounceAsync(destination, cancellationToken: stop).ConfigureAwait(false);
        if (pageDestination is not null)
            await node.Router.AnnounceAsync(pageDestination, cancellationToken: stop).ConfigureAwait(false);
    }

    private void Connected(Link link)
    {
        if (links.Count >= 256 || Volatile.Read(ref disposed) != 0)
        {
            diagnostic("Git host connection capacity reached or host stopping.");
            link.Teardown();
            return;
        }
        var key = Convert.ToHexStringLower(link.LinkId);
        links.TryAdd(key, link);
        link.Closed = closed => links.TryRemove(key, out _);
        link.RemoteIdentified = (_, remote) =>
        {
            if (Configuration.BlockedIdentities.Contains(remote.HexHash))
            {
                diagnostic("Blocked Git peer " + remote.HexHash);
                link.Teardown();
            }
        };
    }

    private object? Queue(RequestContext context)
    {
        if (Volatile.Read(ref disposed) != 0) return RngitWire.Status(255, "Server stopping");
        if (!requests.Writer.TryWrite(context))
        {
            diagnostic("Git request queue is full: " + context.Path);
            return RngitWire.Status(255, "Server busy");
        }
        return null;
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var context in requests.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
            {
                var link = node.Router.Links.Find(context.LinkId);
                if (link?.Status != LinkStatus.Active) continue;
                try
                {
                    var result = await HandleAsync(context.Path, context.Data, context.RemoteIdentity, shutdown.Token).ConfigureAwait(false);
                    if (result is not null && !link.Respond(context.RequestId, result, context.Path != "/media"))
                        diagnostic("Git peer disconnected before response: " + context.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                    or ArgumentException or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception)
                {
                    diagnostic($"Git peer request {context.Path} failed: {ex.Message}");
                    link.Teardown();
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    public async Task<object?> HandleAsync(string path, object? data, Identity? caller, CancellationToken stop = default)
    {
        if (RngitPageService.Paths.Contains(path))
            return Pages is null ? Encoding.UTF8.GetBytes("Not found") : await Pages.HandleAsync(path, data, caller, stop).ConfigureAwait(false);
        if (caller is null) return RngitWire.Status(1, "Not identified");
        if (data is not Dictionary<object, object?>) return RngitWire.Status(2, "Invalid request");
        var repository = RngitWire.Text(data, 0);
        if (repository is null && path != "/mgmt/perms") return RngitWire.Status(2, "No repository specified");
        // Unknown request paths must not allocate an attacker-controlled lock registry.
        using var gate = repository is not null && Store.Find(repository) is not null
            && path is "/git/list" or "/git/fetch" or "/git/push" or "/git/delete"
            ? await Store.LockRepositoryAsync(repository, stop).ConfigureAwait(false) : null;
        try
        {
            return path switch
            {
                "/git/list" => await ListAsync(repository!, data, caller, stop).ConfigureAwait(false),
                "/git/fetch" => await FetchAsync(repository!, data, caller, stop).ConfigureAwait(false),
                "/git/push" => await PushAsync(repository!, data, caller, stop).ConfigureAwait(false),
                "/git/delete" => await DeleteAsync(repository!, data, caller, stop).ConfigureAwait(false),
                "/git/create" => await Store.HandleCreateAsync(data, caller, stop).ConfigureAwait(false),
                "/git/fork" => await Store.HandleForkAsync(data, caller, stop).ConfigureAwait(false),
                "/git/mirror" => await Store.HandleMirrorAsync(data, caller, stop).ConfigureAwait(false),
                "/git/sync" => await Store.HandleSyncAsync(data, caller, stop).ConfigureAwait(false),
                "/mgmt/perms" => await Store.HandlePermissionsAsync(data, caller, stop).ConfigureAwait(false),
                "/mgmt/release" => await Releases.HandleAsync(data, caller, stop).ConfigureAwait(false),
                "/mgmt/work" => await Work.HandleAsync(data, caller, stop).ConfigureAwait(false),
                _ => RngitWire.Status(2, "Invalid request"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            diagnostic($"Git request {path} failed: {ex.Message}");
            return RngitWire.Status(255, "Remote error");
        }
    }

    private byte[]? Denied(string repository, Identity caller, string permission)
    {
        if (Store.ResolvePermission(caller, repository, permission)) return null;
        return Store.ResolvePermission(caller, repository, "read")
            ? RngitWire.Status(1, "Not allowed") : RngitWire.Status(3, "Not found");
    }

    private async Task<object> ListAsync(string repository, object data, Identity caller, CancellationToken stop)
    {
        if (Denied(repository, caller, RngitWire.Flag(data, "for_push") ? "write" : "read") is { } denied)
            return RngitWire.Status(3, Encoding.UTF8.GetString(denied.AsSpan(1)));
        var path = Store.Find(repository)!.Path;
        var listing = await RngitClient.GitAsync(path, ["for-each-ref", "--format=%(objectname) %(refname)"], stop, isolated: true).ConfigureAwait(false);
        var headText = await File.ReadAllTextAsync(Path.Combine(path, "HEAD"), stop).ConfigureAwait(false);
        var head = headText.StartsWith("ref: ", StringComparison.Ordinal) ? headText[5..].Trim() : "master";
        return RngitWire.Status(0, listing.TrimEnd('\r', '\n') + (listing.Length > 0 ? "\n" : "") + "@" + head + " HEAD\n");
    }

    private async Task<object> FetchAsync(string repository, object data, Identity caller, CancellationToken stop)
    {
        if (Denied(repository, caller, "read") is { } denied) return denied;
        var path = Store.Find(repository)!.Path;
        if (RngitWire.Get(data, "refs") is not object?[] { Length: > 0 } refs)
            return RngitWire.Status(2, "No refs specified");
        if (refs.Length > 1024) return RngitWire.Status(2, "Invalid request");
        var revisions = new List<string>();
        foreach (var item in refs)
        {
            var name = RngitWire.Text(item, "ref");
            if (name is null || !RngitSession.ValidRef(name)) return RngitWire.Status(2, "Invalid request");
            revisions.Add(name);
            if (RngitWire.Text(item, "have") is { Length: > 0 } have)
            {
                if (!RngitWire.ValidSha(have)) return RngitWire.Status(2, "Invalid SHA");
                if (await ObjectExistsAsync(path, have, stop).ConfigureAwait(false)) revisions.Add("^" + have);
            }
        }
        if (RngitWire.Get(data, "have") is object?[] haveObjects)
        {
            if (haveObjects.Length > 65536) return RngitWire.Status(2, "Invalid request");
            foreach (var value in haveObjects)
            {
                if (value is not string sha || !RngitWire.ValidSha(sha)) return RngitWire.Status(2, "Invalid SHA");
                if (await ObjectExistsAsync(path, sha, stop).ConfigureAwait(false)) revisions.Add("^" + sha);
            }
        }
        var bundle = Path.Combine(Path.GetTempPath(), "dotreticulum-fetch-" + Guid.NewGuid().ToString("N") + ".bundle");
        try
        {
            try
            {
                await RngitClient.GitAsync(path, ["bundle", "create", "--no-progress", bundle, "--stdin"], stop,
                    string.Join('\n', revisions) + "\n", isolated: true).ConfigureAwait(false);
            }
            catch (IOException ex) when (ex.Message.Contains("empty bundle", StringComparison.OrdinalIgnoreCase))
            {
                return RngitWire.Status(0);
            }
            if (new FileInfo(bundle).Length > MaxTransferBytes) throw new IOException("Fetch bundle exceeds configured limit.");
            var hosted = Store.Find(repository)!;
            Statistics.RecordFetch(hosted.Group, hosted.Name, caller.Hash);
            return new FileResponse(await File.ReadAllBytesAsync(bundle, stop).ConfigureAwait(false),
                new Dictionary<object, object?> { [1] = 0 });
        }
        finally { File.Delete(bundle); }
    }

    private async Task<object> PushAsync(string repository, object data, Identity caller, CancellationToken stop)
    {
        if (Denied(repository, caller, "write") is { } denied) return denied;
        var path = Store.Find(repository)!.Path;
        if (RngitWire.Get(data, "bundle") is byte[] { Length: > 0 } bundleData)
        {
            var local = RngitWire.Text(data, "local_ref");
            var remote = RngitWire.Text(data, "remote_ref");
            if (local is null || remote is null || !RngitSession.ValidRef(local) || !RngitSession.ValidRef(remote))
                return RngitWire.Status(2, "Missing ref specification");
            if (bundleData.LongLength > MaxTransferBytes) return RngitWire.Status(2, "Bundle exceeds configured limit");
            var bundle = Path.Combine(Path.GetTempPath(), "dotreticulum-push-" + Guid.NewGuid().ToString("N") + ".bundle");
            try
            {
                await File.WriteAllBytesAsync(bundle, bundleData, stop).ConfigureAwait(false);
                try
                {
                    await RngitClient.GitAsync(path, ["bundle", "verify", bundle], stop, isolated: true).ConfigureAwait(false);
                    await RngitClient.GitAsync(path, ["-c", "protocol.file.allow=always", "fetch", bundle,
                        (RngitWire.Flag(data, "force") ? "+" : "") + local + ":" + remote], stop, isolated: true).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    diagnostic("Push bundle rejected: " + ex.Message);
                    return RngitWire.Status(255, "Could not verify bundle");
                }
                RecordPush(repository, caller);
                return RngitWire.Status(0);
            }
            finally { File.Delete(bundle); }
        }
        if (RngitWire.Get(data, "operations") is not object?[] { Length: > 0 } operations)
            return RngitWire.Status(2, "Invalid request data");
        if (operations.Length > 1024) return RngitWire.Status(2, "Invalid data for operations");
        foreach (var operation in operations)
        {
            var action = RngitWire.Text(operation, "action") ?? "";
            var name = RngitWire.Text(operation, "ref");
            var sha = RngitWire.Text(operation, "sha");
            if (action != "update_ref") return RngitWire.Status(2, "Unknown operation: " + action);
            if (name is null || !RngitSession.ValidRef(name)) return RngitWire.Status(2, "Invalid request");
            if (!RngitWire.ValidSha(sha)) return RngitWire.Status(2, "Invalid SHA");
            if (!await ObjectExistsAsync(path, sha!, stop).ConfigureAwait(false))
                return RngitWire.Status(255, $"Object {sha} does not exist in repository");
            string? old = null;
            try { old = (await RngitClient.GitAsync(path, ["rev-parse", "--verify", name], stop, isolated: true).ConfigureAwait(false)).Trim(); }
            catch (IOException ex) { diagnostic("Push target lookup: " + ex.Message); }
            if (old is not null && old != sha && !RngitWire.Flag(operation, "force"))
                return RngitWire.Status(1, $"Ref {name} already exists at different SHA (force required)");
            await RngitClient.GitAsync(path, ["update-ref", name, sha!], stop, isolated: true).ConfigureAwait(false);
        }
        RecordPush(repository, caller);
        return RngitWire.Status(0);
    }

    private async Task<object> DeleteAsync(string repository, object data, Identity caller, CancellationToken stop)
    {
        if (Denied(repository, caller, "write") is { } denied) return denied;
        var name = RngitWire.Text(data, "ref");
        if (name is null || !RngitSession.ValidRef(name)) return RngitWire.Status(2, "Invalid request");
        await RngitClient.GitAsync(Store.Find(repository)!.Path, ["update-ref", "-d", name], stop, isolated: true).ConfigureAwait(false);
        RecordPush(repository, caller);
        return RngitWire.Status(0);
    }

    private void RecordPush(string repository, Identity caller)
    {
        var hosted = Store.Find(repository)!;
        Statistics.RecordPush(hosted.Group, hosted.Name, caller.Hash);
    }

    private async Task<bool> ObjectExistsAsync(string path, string sha, CancellationToken stop)
    {
        try { await RngitClient.GitAsync(path, ["cat-file", "-e", sha], stop, isolated: true).ConfigureAwait(false); return true; }
        catch (IOException ex) { diagnostic("Object lookup: " + ex.Message); return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        destination.AcceptLinks = false;
        node.Router.DeregisterLocalDestination(destination);
        if (pageDestination is not null)
        {
            pageDestination.AcceptLinks = false;
            node.Router.DeregisterLocalDestination(pageDestination);
        }
        try
        {
            requests.Writer.TryComplete();
            await shutdown.CancelAsync().ConfigureAwait(false);
            foreach (var link in links.Values) link.Teardown();
            await Task.WhenAll(workers).ConfigureAwait(false);
            Statistics.Flush();
        }
        finally
        {
            identity.Dispose();
            shutdown.Dispose();
        }
    }
}
