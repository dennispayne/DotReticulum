using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Transport;
using DotReticulum.Applications.Runtime;

namespace DotReticulum.Applications;

/// <summary>
/// A running client-side Reticulum node: the interfaces and <see cref="TransportRouter"/> configured like <c>rnsd</c> (same config file),
/// with the router loop already running in the background. Applications (rncp, rnx, and external ones such as DotLXMF and DotTelemetry) register destinations on
/// <see cref="Router"/> and use <see cref="TransportRouter.Links"/> for links.
/// </summary>
public sealed class ReticulumNode : IAsyncDisposable
{
    private readonly RnsdHost _host;
    private readonly CancellationTokenSource _cts = new();
    private Task _run = Task.CompletedTask;
    private readonly object _gate = new();
    private Task? _startTask;
    private Task? _disposeTask;

    private ReticulumNode(RnsdHost host, string configDir, RnsdConfig config)
    {
        _host = host;
        ConfigDir = configDir;
        Config = config;
    }

    public TransportRouter Router => _host.Router;

    public RnsdConfig Config { get; }

    public string ConfigDir { get; }

    /// <summary>Directory for identities and other state: <c>&lt;config dir&gt;/storage</c>.</summary>
    public string StorageDir => Path.Combine(ConfigDir, "storage");

    public IReadOnlyList<(string Name, IPacketInterface Interface)> Interfaces => _host.Interfaces;

    /// <summary>Completes when processing stops; unexpected router failures remain observable.</summary>
    public Task Completion => _run;
    public SharedInstanceRole SharedRole => _host.SharedRole;
    public bool IsConnectedToSharedInstance => _host.IsConnectedToSharedInstance;

    /// <summary>Returns native runtime measurements; profiling adds .NET process counters without inventing RNS profiler data.</summary>
    public Dictionary<object, object?> GetStatistics(bool profiling = false) => _host.GetStatistics(profiling);

    public ValueTask<object?> CallManagementAsync(Dictionary<object, object?> call, CancellationToken cancellationToken = default) =>
        _host.CallManagementAsync(call, cancellationToken);

    public Task PersistAsync(CancellationToken cancellationToken = default) => _host.PersistAsync(cancellationToken);

    /// <summary>Creates (without starting) a node from an already parsed config.</summary>
    public static ReticulumNode Create(RnsdConfig config, string configDir, Action<int, string>? log = null,
        InterfaceFactoryRegistry? extensions = null, RuntimeExecutionPolicy? executionPolicy = null)
    {
        var host = RnsdHost.Create(config, Path.Combine(configDir, "storage", "transport_identity"),
            log ?? ((level, message) => System.Diagnostics.Trace.WriteLine($"Reticulum[{level}]: {message}")), extensions, executionPolicy);
        return new ReticulumNode(host, configDir, config);
    }

    public static async Task<ReticulumNode> CreateAsync(RnsdConfig config, string configDir, Action<int, string>? log = null,
        InterfaceFactoryRegistry? extensions = null, RuntimeExecutionPolicy? executionPolicy = null, CancellationToken cancellationToken = default)
    {
        var host = await RnsdHost.CreateAsync(config, Path.Combine(configDir, "storage", "transport_identity"),
            log ?? ((level, message) => System.Diagnostics.Trace.WriteLine($"Reticulum[{level}]: {message}")),
            extensions, executionPolicy, cancellationToken).ConfigureAwait(false);
        return new ReticulumNode(host, configDir, config);
    }

    /// <summary>Discovers or creates configuration using the same directory/file precedence as <c>rnsd</c>, then creates the node.</summary>
    public static ReticulumNode Open(string? configArg, Action<int, string>? log = null)
    {
        var loaded = RnsdApp.LoadConfiguration(configArg);
        return Create(loaded.Config, loaded.Directory, log);
    }

    /// <summary>Starts the interfaces and the router loop. The loop stops when the node is disposed or <paramref name="cancellationToken"/> is cancelled.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_startTask is not null) throw new InvalidOperationException("The node is already started; create a new node to restart it.");
            _startTask = StartCoreAsync(cancellationToken);
            return _startTask;
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        try { await _host.StartAsync(linked.Token).ConfigureAwait(false); }
        catch
        {
            linked.Dispose();
            await _host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _run = Task.Run(async () =>
        {
            try { await _host.RunAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally
            {
                try { await _host.DisposeAsync().ConfigureAwait(false); }
                finally { linked.Dispose(); }
            }
        }, CancellationToken.None);
    }

    /// <summary>Loads the identity stored under <c>storage/&lt;name&gt;</c>, creating it on first use.</summary>
    public Identity LoadOrCreateIdentity(string name)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Path.IsPathRooted(name) ||
            name.Split(['\\', '/']).Any(part => part.Length == 0 || part is "." or ".." ||
                part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.')))
            throw new ArgumentException("Identity names must be relative paths of ASCII file names without traversal segments.", nameof(name));
        return RuntimeStateStore.LoadOrCreateIdentity(Path.Combine(StorageDir, name));
    }

    /// <summary>Returns once a path to <paramref name="destinationHash"/> is known, requesting one periodically. False on timeout.</summary>
    public async Task<bool> WaitForPathAsync(byte[] destinationHash, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var nextRequest = DateTime.MinValue;
        while (!Router.HasPath(destinationHash))
        {
            if (DateTime.UtcNow >= deadline) return false;
            if (DateTime.UtcNow >= nextRequest)
            {
                await Router.RequestPathAsync(destinationHash, cancellationToken: cancellationToken).ConfigureAwait(false);
                nextRequest = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Recalls the identity behind <paramref name="destinationHash"/>, waiting for its announce (via a path request) when it is unknown.</summary>
    public async Task<Identity?> ResolveIdentityAsync(byte[] destinationHash, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Router.Engine.Known.Recall(destinationHash) is { } known) return known;
        if (!await WaitForPathAsync(destinationHash, timeout, cancellationToken).ConfigureAwait(false)) return null;
        return Router.Engine.Known.Recall(destinationHash);
    }

    public ValueTask<bool> UseDestinationAsync(byte[] destinationHash, CancellationToken cancellationToken = default) =>
        DestinationDataAsync("used", destinationHash, cancellationToken);
    public ValueTask<bool> RetainDestinationAsync(byte[] destinationHash, CancellationToken cancellationToken = default) =>
        DestinationDataAsync("retain", destinationHash, cancellationToken);
    public ValueTask<bool> UnretainDestinationAsync(byte[] destinationHash, CancellationToken cancellationToken = default) =>
        DestinationDataAsync("unretain", destinationHash, cancellationToken);

    private async ValueTask<bool> DestinationDataAsync(string operation, byte[] destinationHash, CancellationToken cancellationToken)
    {
        var response = await CallManagementAsync(new Dictionary<object, object?>
        {
            ["destination_data"] = operation, ["destination_hash"] = destinationHash,
        }, cancellationToken).ConfigureAwait(false);
        return response is bool result ? result : throw new InvalidDataException("Destination-data management returned an invalid response.");
    }

    public async ValueTask<bool> RetainIdentityAsync(byte[] identityHash, CancellationToken cancellationToken = default)
    {
        var response = await CallManagementAsync(new Dictionary<object, object?>
        {
            ["identity_data"] = "retain", ["identity_hash"] = identityHash,
        }, cancellationToken).ConfigureAwait(false);
        return response is bool result ? result : throw new InvalidDataException("Identity-data management returned an invalid response.");
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_startTask is not null) await _startTask.ConfigureAwait(false);
            await _run.ConfigureAwait(false);
        }
        finally
        {
            try { await _host.DisposeAsync().ConfigureAwait(false); }
            finally { _cts.Dispose(); }
        }
    }
}
