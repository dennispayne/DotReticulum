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
        _saveDirectory = Path.GetFullPath(options.SaveDirectory ?? Directory.GetCurrentDirectory());
        _jail = options.FetchJail is null ? null : Path.GetFullPath(options.FetchJail);
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
    }

    private object? OnFetch(RequestContext context)
    {
        if (context.Data is not string requested) return false;
        string path;
        if (_jail is not null)
        {
            var relative = requested;
            var prefix = _jail + Path.DirectorySeparatorChar;
            if (relative.StartsWith(prefix, StringComparison.Ordinal)) relative = relative[prefix.Length..];
            path = Path.GetFullPath(Path.Combine(_jail, relative.TrimStart('/', '\\')));
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                Log?.Invoke($"Disallowing fetch request for {path} outside of fetch jail {_jail}");
                return FetchNotAllowed;
            }
        }
        else
        {
            path = Path.GetFullPath(requested);
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
            link.SendResource(File.ReadAllBytes(path), FileSaver.Metadata(Path.GetFileName(path)), _options.AutoCompress);
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
    public static Dictionary<object, object?> Metadata(string name) => new() { ["name"] = System.Text.Encoding.UTF8.GetBytes(name) };

    /// <summary>Writes a received file resource into <paramref name="directory"/> using its metadata name; null when the resource carries none.</summary>
    public static string? Save(Resource resource, string directory, bool overwrite)
    {
        if (resource.Data is not { } data) return null;
        if (resource.Metadata is not Dictionary<object, object?> metadata || !metadata.TryGetValue("name", out var rawName) || rawName is not byte[] nameBytes) return null;
        var name = Path.GetFileName(System.Text.Encoding.UTF8.GetString(nameBytes).Replace('\\', '/'));
        if (name.Length == 0 || name is "." or "..") return null;
        var target = Path.GetFullPath(Path.Combine(directory, name));
        if (!target.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        if (overwrite && File.Exists(target)) File.Delete(target);
        var candidate = target;
        for (var counter = 1; File.Exists(candidate); counter++) candidate = target + "." + counter;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(candidate, data);
        return candidate;
    }
}

public sealed record RncpResult(bool Success, string Message, string? SavedPath = null, long Bytes = 0);

/// <summary>Client side of rncp: send a file to a listener, or fetch one from it.</summary>
public static class RncpClient
{
    public static async Task<RncpResult> SendAsync(ReticulumNode node, Identity identity, byte[] destinationHash, string file, TimeSpan timeout,
        bool autoCompress = true, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) return new RncpResult(false, "File not found");
        var connected = await ClientLinks.ConnectAsync(node, destinationHash, timeout, cancellationToken).ConfigureAwait(false);
        if (connected.Link is not { } link) return new RncpResult(false, connected.Error!);

        try
        {
            link.Identify(identity);
            var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var done = new TaskCompletionSource<ResourceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            link.SendResource(data, FileSaver.Metadata(Path.GetFileName(path)), autoCompress, r => done.TrySetResult(r.Status),
                r => progress?.Invoke(r.Progress));
            var status = await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return status switch
            {
                ResourceStatus.Complete => new RncpResult(true, path + " copied to " + CliSupport.PrettyHex(destinationHash), Bytes: data.Length),
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
        try
        {
            link.ResourceStrategy = ResourceStrategy.AcceptAll;
            link.ResourceStarted = _ => progress?.Invoke(0);
            link.ResourceReceived = resource =>
            {
                if (resource.Status != ResourceStatus.Complete)
                {
                    concluded.TrySetResult(new RncpResult(false, "Resource failed"));
                    return;
                }

                try
                {
                    var saved = FileSaver.Save(resource, saveDirectory, allowOverwrite);
                    concluded.TrySetResult(saved is null
                        ? new RncpResult(false, "Invalid data received, ignoring resource")
                        : new RncpResult(true, file + " fetched from " + CliSupport.PrettyHex(destinationHash), saved, resource.Data?.Length ?? 0));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    concluded.TrySetResult(new RncpResult(false, "An error occurred while saving received resource: " + ex.Message));
                }
            };
            link.Identify(identity);
            var receipt = link.Request(RncpListener.FetchPath, file, timeout: timeout.TotalSeconds);
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

            return await concluded.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            link.Teardown();
        }
    }
}
