using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DotReticulum.Applications.Runtime;

/// <summary>A validated upstream local RPC operation, not an arbitrary method name.</summary>
public sealed record ManagementRequest(string Operation, string Path, IReadOnlyDictionary<object, object?> Arguments)
{
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["get"] = ["path_table", "interface_stats", "rate_table", "next_hop_if_name", "next_hop", "first_hop_timeout",
            "lowest_interface_bitrate", "medium_path_timeout", "link_count", "active_link_count", "packet_rssi",
            "packet_snr", "packet_q", "profiling_results", "blackholed_identities", "is_blackholed"],
        ["drop"] = ["path", "all_via", "announce_queues"],
        ["manage"] = ["attach_interface", "detach_interface", "reload_interface"],
        ["destination_data"] = ["used", "retain", "unretain"],
        ["identity_data"] = ["retain"],
    };

    public static ManagementRequest Parse(object? value)
    {
        if (value is not Dictionary<object, object?> map || map.Count is < 1 or > 8)
            throw new InvalidDataException("An RPC call must be a bounded operation map.");
        var operations = map.Keys.OfType<string>().Where(k => Allowed.ContainsKey(k) || k is "blackhole_identity" or "unblackhole_identity").ToArray();
        if (operations.Length != 1) throw new InvalidDataException("Exactly one RPC operation is required.");
        var operation = operations[0];
        string path;
        if (operation is "blackhole_identity" or "unblackhole_identity")
        {
            Hash(map, operation);
            path = operation;
            if (operation == "blackhole_identity")
            {
                if (!map.TryGetValue("until", out var until) || until is not (null or long or double)
                    || until is double d && !double.IsFinite(d)) throw new InvalidDataException("Invalid blackhole expiry.");
                if (!map.TryGetValue("reason", out var reason) || reason is not (null or string)) throw new InvalidDataException("Invalid blackhole reason.");
            }
        }
        else
        {
            path = map[operation] as string ?? throw new InvalidDataException("RPC operation path must be a string.");
            if (!Allowed[operation].Contains(path, StringComparer.Ordinal)) throw new InvalidDataException("Unsupported RPC operation: " + operation + "/" + path);
        }

        var hashKey = operation switch
        {
            "destination_data" => "destination_hash",
            "identity_data" => "identity_hash",
            "drop" when path is "path" or "all_via" => "destination_hash",
            "get" when path is "next_hop" or "next_hop_if_name" or "first_hop_timeout" => "destination_hash",
            "get" when path is "packet_rssi" or "packet_snr" or "packet_q" => "packet_hash",
            "get" when path == "is_blackholed" => "identity_hash",
            _ => null,
        };
        if (hashKey is not null) Hash(map, hashKey, hashKey == "packet_hash" ? 32 : 16);
        if (operation == "manage" && (map.GetValueOrDefault("name") is not string name || string.IsNullOrWhiteSpace(name)))
            throw new InvalidDataException("Interface name is required.");
        if (operation == "get" && path == "path_table" &&
            (!map.TryGetValue("max_hops", out var hops) || hops is not (null or long) || hops is long h && h < 0))
            throw new InvalidDataException("max_hops must be null or a nonnegative integer.");
        return new ManagementRequest(operation, path, map);
    }

    private static void Hash(Dictionary<object, object?> map, string key, int length = 16)
    {
        if (map.GetValueOrDefault(key) is not byte[] hash || hash.Length != length)
            throw new InvalidDataException($"{key} must be a {length}-byte hash.");
    }
}

/// <summary>One authenticated byte-framed MessagePack operation per connection, as in Reticulum.rpc_loop.</summary>
public sealed class ManagementRpcServer : IAsyncDisposable
{
    private readonly EndPoint endpoint;
    private readonly byte[] key;
    private readonly Func<ManagementRequest, CancellationToken, ValueTask<object?>> provider;
    private readonly Action<Exception>? diagnostic;
    private readonly TimeSpan timeout;
    private readonly int maxClients;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly HashSet<Task> clients = [];
    private Socket? listener;
    private Task? accept;
    private Task? disposal;

    public ManagementRpcServer(EndPoint endpoint, ReadOnlySpan<byte> key,
        Func<ManagementRequest, CancellationToken, ValueTask<object?>> provider, TimeSpan? timeout = null,
        int maxClients = 32, Action<Exception>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(provider);
        if (key.IsEmpty) throw new ArgumentException("RPC key is required.", nameof(key));
        if (maxClients < 1) throw new ArgumentOutOfRangeException(nameof(maxClients));
        this.endpoint = endpoint;
        this.key = key.ToArray();
        this.provider = provider;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.maxClients = maxClients;
        this.diagnostic = diagnostic;
    }

    public EndPoint? LocalEndpoint { get; private set; }
    public ValueTask StartAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (listener is not null) throw new InvalidOperationException("RPC server already started.");
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, endpoint.AddressFamily == AddressFamily.Unix ? ProtocolType.Unspecified : ProtocolType.Tcp);
            try
            {
                if (endpoint.AddressFamily != AddressFamily.Unix) socket.ExclusiveAddressUse = true;
                socket.Bind(endpoint);
                socket.Listen(maxClients);
                listener = socket;
                LocalEndpoint = socket.LocalEndPoint;
                accept = AcceptAsync(socket);
            }
            catch { socket.Dispose(); throw; }
        }
        return ValueTask.CompletedTask;
    }

    private async Task AcceptAsync(Socket socket)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await socket.AcceptAsync(stop.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (stop.IsCancellationRequested || clients.Count >= maxClients) { client.Dispose(); continue; }
                    var task = ServeAsync(client);
                    clients.Add(task);
                    _ = task.ContinueWith(t => { lock (gate) clients.Remove(t); }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception ex) { Report(ex); }
    }

    private async Task ServeAsync(Socket socket)
    {
        using (socket)
        using (var stream = new NetworkStream(socket, ownsSocket: false))
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            deadline.CancelAfter(timeout);
            try
            {
                await ManagementConnection.AuthenticateAsync(stream, key, server: true, deadline.Token).ConfigureAwait(false);
                var bytes = await ManagementConnection.ReadAsync(stream, deadline.Token).ConfigureAwait(false);
                var request = ManagementRequest.Parse(ManagementMessagePack.Unpack(bytes));
                // A misbehaving provider cannot hold up socket shutdown. The token remains its cooperative cancellation boundary.
                var response = await provider(request, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                var packed = ManagementMessagePack.Pack(response);
                await ManagementConnection.WriteAsync(stream, packed, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or IOException or SocketException) { }
            catch (Exception ex) { Report(ex); }
        }
    }

    private void Report(Exception error)
    {
        try
        {
            if (diagnostic is null) System.Diagnostics.Trace.TraceError("Management RPC failure: {0}", error);
            else diagnostic(error);
        }
        catch (Exception observer) { System.Diagnostics.Trace.TraceError("RPC diagnostic callback failed: {0}; original: {1}", observer, error); }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new ValueTask(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        listener?.Dispose();
        if (accept is not null) await accept.ConfigureAwait(false);
        Task[] active;
        lock (gate) active = [.. clients];
        await Task.WhenAll(active).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(key);
    }
}

public sealed class ManagementRpcClient : IDisposable
{
    private readonly EndPoint endpoint;
    private readonly byte[] key;
    private readonly TimeSpan timeout;
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private bool disposed;

    public ManagementRpcClient(EndPoint endpoint, ReadOnlySpan<byte> key, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (key.IsEmpty) throw new ArgumentException("RPC key required.", nameof(key));
        this.endpoint = endpoint;
        this.key = key.ToArray();
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async ValueTask<object?> CallAsync(Dictionary<object, object?> call, CancellationToken token = default)
    {
        var packed = ManagementMessagePack.Pack(call);
        _ = ManagementRequest.Parse(ManagementMessagePack.Unpack(packed));
        byte[] operationKey;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            operationKey = key.ToArray();
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
            deadline.CancelAfter(timeout);
            using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, endpoint.AddressFamily == AddressFamily.Unix ? ProtocolType.Unspecified : ProtocolType.Tcp);
            await socket.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            await ManagementConnection.AuthenticateAsync(stream, operationKey, server: false, deadline.Token).ConfigureAwait(false);
            await ManagementConnection.WriteAsync(stream, packed, deadline.Token).ConfigureAwait(false);
            return ManagementMessagePack.Unpack(await ManagementConnection.ReadAsync(stream, deadline.Token).ConfigureAwait(false));
        }
        finally { CryptographicOperations.ZeroMemory(operationKey); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            stop.Cancel();
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
