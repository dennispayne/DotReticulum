using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Runtime;

/// <summary>The separately authenticated RNS rnstransport.remote.management /status and /path API.</summary>
public sealed class ManagementRemoteService
{
    private readonly Func<ManagementRequest, object?> provider;
    private readonly byte[][] allowed;
    private readonly Action<Exception>? diagnostic;
    public ManagementRemoteService(Func<ManagementRequest, object?> provider, IEnumerable<byte[]> allowedIdentityHashes,
        Action<Exception>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(allowedIdentityHashes);
        this.provider = provider;
        allowed = allowedIdentityHashes.Select(hash => hash.Length == 16 ? hash.ToArray() : throw new ArgumentException("Management ACL identities must be 16-byte hashes.")).ToArray();
        this.diagnostic = diagnostic;
    }
    public static byte[] DestinationHash(byte[] identityHash) => Destination.ComputeHash(identityHash, "rnstransport", ["remote", "management"]);
    public LocalDestination Register(Identity identity, Action<LocalDestination> register)
    {
        ArgumentNullException.ThrowIfNull(register);
        var destination = new LocalDestination(identity, Destination.Create(DestinationType.Single, "rnstransport", ["remote", "management"], identity))
        {
            AcceptLinks = true,
            MaxRequestSize = 4096,
        };
        destination.RegisterRequestHandler("/status", Handle, RequestPolicy.List, allowed);
        destination.RegisterRequestHandler("/path", Handle, RequestPolicy.List, allowed);
        register(destination);
        return destination;
    }
    public object? Handle(RequestContext context)
    {
        if (context.RemoteIdentity is null || !allowed.Any(hash => hash.AsSpan().SequenceEqual(context.RemoteIdentity.Hash))) return null;
        if (context.Data is not object?[] { Length: > 0 } data) return null;
        try
        {
            if (context.Path == "/status")
            {
                var result = new List<object?> { Call("interface_stats") };
                if (data[0] is true) result.Add(Call("link_count"));
                if (data.Length >= 2 && data[1] is true) result.Add(Call("profiling_results"));
                return result.ToArray();
            }
            if (context.Path == "/path" && data[0] is string command && command is "table" or "rates")
            {
                var filter = data.Length > 1 ? data[1] : null;
                if (filter is not null && filter is not byte[] { Length: 16 }) return null;
                var hops = data.Length > 2 ? data[2] : null;
                var call = new Dictionary<object, object?> { ["get"] = command == "table" ? "path_table" : "rate_table" };
                if (command == "table") call["max_hops"] = hops;
                var rows = provider(ManagementRequest.Parse(call)) as object?[] ?? throw new InvalidDataException("Management table provider must return a MessagePack array.");
                return rows.Where(row => filter is null || row is Dictionary<object, object?> map &&
                    map.GetValueOrDefault("hash") is byte[] hash && ((byte[])filter).AsSpan().SequenceEqual(hash)).ToArray();
            }
            return null;
        }
        catch (Exception ex)
        {
            try { diagnostic?.Invoke(ex); }
            catch (Exception observer) { System.Diagnostics.Trace.TraceError("Remote management diagnostic failed: {0}; original: {1}", observer, ex); }
            return null;
        }
    }
    private object? Call(string path) => provider(ManagementRequest.Parse(new Dictionary<object, object?> { ["get"] = path }));
}

/// <summary>Identified native RNS management link, distinct from the local multiprocessing RPC socket.</summary>
public sealed class ManagementRemoteClient : IDisposable
{
    private readonly Link link;
    private readonly TimeSpan timeout;
    private ManagementRemoteClient(Link link, TimeSpan timeout) { this.link = link; this.timeout = timeout; }
    public static async Task<ManagementRemoteClient> ConnectAsync(ReticulumNode node, Identity identity,
        byte[] destination, TimeSpan timeout, CancellationToken token = default)
    {
        var connection = await ClientLinks.ConnectAsync(node, destination, timeout, token).ConfigureAwait(false);
        var link = connection.Link ?? throw new IOException(connection.Error);
        try { link.Identify(identity); return new ManagementRemoteClient(link, timeout); }
        catch { link.Teardown(); throw; }
    }
    public async Task<object?> RequestAsync(string path, object?[] data, CancellationToken token = default)
    {
        if (path is not ("/status" or "/path")) throw new ArgumentException("Unsupported remote management path.", nameof(path));
        token.ThrowIfCancellationRequested();
        var receipt = link.Request(path, data, timeout: timeout.TotalSeconds, maxResponseSize: ManagementConnection.MaximumMessageBytes, cancellationToken: token)
            ?? throw new IOException("Remote management link is closed.");
        return await receipt.ResponseTask.WaitAsync(timeout, token).ConfigureAwait(false);
    }
    public void Dispose() => link.Teardown();
}
