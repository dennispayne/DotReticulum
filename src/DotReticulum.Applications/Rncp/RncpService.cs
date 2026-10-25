using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rncp;

public sealed class RncpListenerOptions
{
    /// <summary>Identity hashes (16 bytes) allowed to send files and, with <see cref="AllowFetch"/>, to fetch them.</summary>
    public List<byte[]> AllowedIdentities { get; init; } = [];

    /// <summary>Accept from anyone (upstream <c>--no-auth</c>).</summary>
    public bool AllowAll { get; init; }

    public bool AllowFetch { get; init; }

    /// <summary>Restricts fetch requests to this directory.</summary>
    public string? FetchJail { get; init; }

    /// <summary>Directory received files are saved in; defaults to the current directory.</summary>
    public string? SaveDirectory { get; init; }

    public bool AllowOverwrite { get; init; }

    public bool AutoCompress { get; init; } = true;
}

/// <summary>
/// The <c>rncp.receive</c> destination, wire-compatible with upstream <c>rncp -l</c>: an authenticated peer identifies on the link and sends a
/// file as a Resource whose metadata is <c>{"name": &lt;utf-8 bytes&gt;}</c>; with fetch allowed, the request path <c>fetch_file</c> returns
/// <c>true</c>/<c>false</c>/<c>0xF0</c> and sends the file as a Resource on the same link.
/// </summary>
public sealed class RncpListener
{
    public const string AppName = "rncp";
    public const string Aspect = "receive";
    public const string FetchPath = "fetch_file";
    public const long FetchNotAllowed = 0xF0;

    private readonly TransportRouter _router;
    private readonly RncpListenerOptions _options;
    private readonly LocalDestination _local;
    private readonly string _saveDirectory;
    private readonly string? _jail;

    public RncpListener(TransportRouter router, Identity identity, RncpListenerOptions options)
    {
        _router = router;
        _options = options;
        _saveDirectory = Path.GetFullPath(CliSupport.ExpandHome(options.SaveDirectory ?? Directory.GetCurrentDirectory()));
        _jail = options.FetchJail is null ? null : Path.GetFullPath(CliSupport.ExpandHome(options.FetchJail));
        var destination = Destination.Create(DestinationType.Single, AppName, [Aspect], identity);
        _local = new LocalDestination(identity, destination) { AcceptLinks = true, LinkEstablished = OnLinkEstablished };
        if (options.AllowFetch)
        {
            _local.RegisterRequestHandler(FetchPath, OnFetch, options.AllowAll ? RequestPolicy.All : RequestPolicy.List, options.AllowedIdentities, options.AutoCompress);
        }

        router.RegisterLocalDestination(_local);
    }

    public byte[] DestinationHash => _local.Destination.Hash;

    public string DestinationHashHex => Convert.ToHexStringLower(DestinationHash);

    public Action<string>? Log { get; set; }

    /// <summary>Raised with the saved path after a file was received completely.</summary>
    public Action<string>? FileReceived { get; set; }

    public ValueTask AnnounceAsync(CancellationToken cancellationToken = default) => _router.AnnounceAsync(_local, cancellationToken: cancellationToken);

    private bool IsAllowed(Identity? identity) =>
        _options.AllowAll || (identity is not null && _options.AllowedIdentities.Any(h => h.AsSpan().SequenceEqual(identity.Hash)));

    private void OnLinkEstablished(Link link)
    {
        Log?.Invoke("Incoming link established");
        link.ResourceStrategy = ResourceStrategy.AcceptApp;
        link.ResourceAdvertised = _ => IsAllowed(link.RemoteIdentity);
        link.ResourceStarted = resource => Log?.Invoke("Starting resource transfer " + CliSupport.PrettyHex(resource.Hash)
            + (link.RemoteIdentity is { } id ? " from " + CliSupport.PrettyHex(id.Hash) : string.Empty));
        link.ResourceReceived = OnResourceConcluded;
        link.RemoteIdentified = (l, identity) =>
        {
            if (IsAllowed(identity))
            {
                Log?.Invoke("Authenticated sender");
            }
            else
            {
                Log?.Invoke("Sender not allowed, tearing down link");
                l.Teardown();
            }
        };
    }

    private void OnResourceConcluded(Resource resource)
    {
        if (resource.Status != ResourceStatus.Complete)
        {
            Log?.Invoke("Resource failed");
            resource.Dispose();
            return;
        }

        try
        {
            var saved = FileSaver.Save(resource, _saveDirectory, _options.AllowOverwrite);
            if (saved is null)
            {
                Log?.Invoke("Invalid data received, ignoring resource");
                return;
            }

            Log?.Invoke("Saved received file to " + saved);
            FileReceived?.Invoke(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke("An error occurred while saving received resource: " + ex.Message);
        }
        finally { resource.Dispose(); }
    }

    private object? OnFetch(RequestContext context)
    {
        if (context.Data is not string requested) return false;
        string path;
        if (_jail is not null)
        {
            var relative = requested;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var prefix = Path.TrimEndingDirectorySeparator(_jail) + Path.DirectorySeparatorChar;
            if (relative.StartsWith(prefix, comparison)) relative = relative[prefix.Length..];
            path = Path.GetFullPath(Path.Combine(_jail, relative.TrimStart('/', '\\')));
            if (!path.StartsWith(prefix, comparison))
            {
                Log?.Invoke($"Disallowing fetch request for {path} outside of fetch jail {_jail}");
                return FetchNotAllowed;
            }
        }
        else
        {
            path = Path.GetFullPath(CliSupport.ExpandHome(requested));
        }

        if (!File.Exists(path))
        {
            Log?.Invoke("Client-requested file not found: " + path);
            return false;
        }

        if (_router.Links.Find(context.LinkId) is not { } link) return null;
        try
        {
            Log?.Invoke("Sending file " + path + " to client");
            link.SendResourceFile(path, FileSaver.Metadata(Path.GetFileName(path)), _options.AutoCompress);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log?.Invoke("Could not send file to client: " + ex.Message);
            return false;
        }
    }
}

internal static class FileSaver
{
    private static readonly System.Text.UTF8Encoding FilenameEncoding = new(false, true);
    private static readonly object[] PublicationGates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    public static Dictionary<object, object?> Metadata(string name) => new() { ["name"] = System.Text.Encoding.UTF8.GetBytes(name) };

    /// <summary>Writes a received file resource into <paramref name="directory"/> using its metadata name; null when the resource carries none.</summary>
    public static string? Save(Resource resource, string directory, bool overwrite)
    {
        if (resource.Status != ResourceStatus.Complete) return null;
        if (resource.Metadata is not Dictionary<object, object?> metadata || !metadata.TryGetValue("name", out var rawName) || rawName is not byte[] nameBytes) return null;
        string name;
        try { name = Path.GetFileName(FilenameEncoding.GetString(nameBytes).Replace('\\', '/')); }
        catch (System.Text.DecoderFallbackException) { return null; }
        if (name.Length == 0 || name is "." or "..") return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var target = Path.GetFullPath(Path.Combine(directory, name));
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        Directory.CreateDirectory(directory);
        using var input = resource.OpenRead();
        return SaveStream(input, target, overwrite);
    }

    internal static string SaveStream(Stream input, string target, bool overwrite, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(target)!;
        var candidate = target;
        var counter = 0;
        if (!overwrite)
            while (File.Exists(candidate))
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidate = target + "." + checked(++counter);
            }
        var staged = Path.Combine(directory, ".rncp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var publicationGate = PublicationGates[(int)((uint)comparer.GetHashCode(target) % (uint)PublicationGates.Length)];
            lock (publicationGate)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!overwrite && File.Exists(candidate))
                    {
                        candidate = target + "." + checked(++counter);
                        continue;
                    }
                    try
                    {
                        if (overwrite) File.Move(staged, candidate, overwrite: true);
                        else if (!AtomicFilePublication.TryPublishNew(staged, candidate))
                        {
                            candidate = target + "." + checked(++counter);
                            continue;
                        }
                        break;
                    }
                    catch (Exception ex) when (overwrite && File.Exists(candidate) && ex is IOException or UnauthorizedAccessException)
                    {
                        overwrite = false;
                        candidate = target + "." + checked(++counter);
                    }
                }
            }
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
        return candidate;
    }
}

public sealed record RncpResult(bool Success, string Message, string? SavedPath = null, long Bytes = 0, long PhysicalBytes = 0);

/// <summary>Client side of rncp: send a file to a listener, or fetch one from it.</summary>
public static class RncpClient
{
    public static async Task<RncpResult> SendAsync(ReticulumNode node, Identity identity, byte[] destinationHash, string file, TimeSpan timeout,
        bool autoCompress = true, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(CliSupport.ExpandHome(file));
        if (!File.Exists(path)) return new RncpResult(false, "File not found");
        var connected = await ClientLinks.ConnectAsync(node, destinationHash, timeout, cancellationToken).ConfigureAwait(false);
        if (connected.Link is not { } link) return new RncpResult(false, connected.Error!);

        try
        {
            link.Identify(identity);
            var size = new FileInfo(path).Length;
            var segments = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
            var done = new TaskCompletionSource<ResourceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            link.Closed = _ => done.TrySetResult(ResourceStatus.Failed);
            link.SendResourceFile(path, FileSaver.Metadata(Path.GetFileName(path)), autoCompress, r => done.TrySetResult(r.Status),
                r => { segments[r.SegmentIndex] = r.Size; progress?.Invoke(r.Progress); }, cancellationToken: cancellationToken);
            var status = await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return status switch
            {
                ResourceStatus.Complete => new RncpResult(true, path + " copied to " + CliSupport.PrettyHex(destinationHash), Bytes: size,
                    PhysicalBytes: segments.Values.Sum()),
                ResourceStatus.Rejected => new RncpResult(false, "File was not accepted by " + CliSupport.PrettyHex(destinationHash)),
                _ => new RncpResult(false, "The transfer failed"),
            };
        }
        finally
        {
            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            link.Teardown();
        }
    }

    public static async Task<RncpResult> FetchAsync(ReticulumNode node, Identity identity, byte[] destinationHash, string file, string saveDirectory,
        TimeSpan timeout, bool allowOverwrite = false, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var connected = await ClientLinks.ConnectAsync(node, destinationHash, timeout, cancellationToken).ConfigureAwait(false);
        if (connected.Link is not { } link) return new RncpResult(false, connected.Error!);

        var concluded = new TaskCompletionSource<RncpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var segments = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
        var transferStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            link.ResourceStrategy = ResourceStrategy.AcceptAll;
            link.Closed = _ => concluded.TrySetResult(new RncpResult(false, "The transfer failed"));
            link.ResourceStarted = r => { segments[r.SegmentIndex] = r.Size; transferStarted.TrySetResult(); progress?.Invoke(0); };
            link.ResourceReceived = resource =>
            {
                if (resource.Status != ResourceStatus.Complete)
                {
                    concluded.TrySetResult(new RncpResult(false, "Resource failed"));
                    resource.Dispose();
                    return;
                }

                try
                {
                    var saved = FileSaver.Save(resource, saveDirectory, allowOverwrite);
                    concluded.TrySetResult(saved is null
                        ? new RncpResult(false, "Invalid data received, ignoring resource")
                        : new RncpResult(true, file + " fetched from " + CliSupport.PrettyHex(destinationHash), saved,
                            new FileInfo(saved).Length, segments.Values.Sum()));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    concluded.TrySetResult(new RncpResult(false, "An error occurred while saving received resource: " + ex.Message));
                }
                finally { resource.Dispose(); }
            };
            link.Identify(identity);
            var receipt = link.Request(RncpListener.FetchPath, file, timeout: timeout.TotalSeconds, cancellationToken: cancellationToken);
            if (receipt is null) return new RncpResult(false, "Fetch request failed due to an unknown error (probably not authorised)");
            object? response;
            try { response = await receipt.ResponseTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { return new RncpResult(false, "Fetch request failed due to an unknown error (probably not authorised)"); }

            switch (response)
            {
                case false:
                    return new RncpResult(false, "Fetch request failed, the file " + file + " was not found on the remote");
                case long code when code == RncpListener.FetchNotAllowed:
                    return new RncpResult(false, "Fetch request failed, fetching the file " + file + " was not allowed by the remote");
                case null:
                    return new RncpResult(false, "Fetch request failed due to an error on the remote system");
            }

            try
            {
                await Task.WhenAny(transferStarted.Task, concluded.Task).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) { return new RncpResult(false, "The transfer failed"); }
            return await concluded.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            link.Teardown();
        }
    }
}
