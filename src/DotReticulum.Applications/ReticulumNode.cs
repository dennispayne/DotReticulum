using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications;

/// <summary>
/// A running client-side Reticulum node: the interfaces and <see cref="TransportRouter"/> configured like <c>rnsd</c> (same config file),
/// with the router loop already running in the background. Applications (LXMF, rncp, rnx, telemetry) register destinations on
/// <see cref="Router"/> and use <see cref="TransportRouter.Links"/> for links.
/// </summary>
public sealed class ReticulumNode : IAsyncDisposable
{
    private readonly RnsdHost _host;
    private readonly CancellationTokenSource _cts = new();
    private Task _run = Task.CompletedTask;
    private int _started;

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

    /// <summary>Creates (without starting) a node from an already parsed config.</summary>
    public static ReticulumNode Create(RnsdConfig config, string configDir, Action<int, string>? log = null)
    {
        var host = RnsdHost.Create(config, Path.Combine(configDir, "storage", "transport_identity"), log ?? ((_, _) => { }));
        return new ReticulumNode(host, configDir, config);
    }

    /// <summary>Loads the config like <c>rnsd</c> does (directory or file, default <c>~/.dotreticulum</c>) and creates the node.</summary>
    public static ReticulumNode Open(string? configArg, Action<int, string>? log = null)
    {
        var (dir, file) = RnsdApp.ResolveConfig(configArg);
        if (!File.Exists(file)) throw new FileNotFoundException($"Configuration file '{file}' does not exist. Create it (see 'rnsd --exampleconfig').", file);
        return Create(RnsdConfigParser.Parse(File.ReadAllText(file)), dir, log);
    }

    /// <summary>Starts the interfaces and the router loop. The loop stops when the node is disposed or <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The node is already started.");
        await _host.StartAsync(cancellationToken).ConfigureAwait(false);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _run = Task.Run(async () =>
        {
            try { await _host.RunAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { linked.Dispose(); }
        }, CancellationToken.None);
    }

    /// <summary>Loads the identity stored under <c>storage/&lt;name&gt;</c>, creating it on first use.</summary>
    public Identity LoadOrCreateIdentity(string name)
    {
        var path = Path.Combine(StorageDir, name);
        if (File.Exists(path)) return IdentityFile.Load(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var identity = Identity.Generate();
        IdentityFile.SaveNew(identity, path);
        return identity;
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

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _run.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        await _host.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
