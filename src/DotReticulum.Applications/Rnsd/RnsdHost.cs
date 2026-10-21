using System.Net;
using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using DotReticulum.Applications.Runtime;

namespace DotReticulum.Applications.Rnsd;

/// <summary>Builds the interface set, the manager and the <see cref="TransportRouter"/> for one <c>rnsd</c> instance.</summary>
public sealed partial class RnsdHost : IAsyncDisposable
{
    private readonly PacketInterfaceManager _manager;
    private readonly List<(string Name, IPacketInterface Interface)> _interfaces;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly RnsdConfig _configuration;
    private readonly InterfaceFactoryRegistry? _extensions;
    private readonly Action<int, string> _log;
    private readonly Dictionary<ISpawningPacketInterface, Action<IPacketInterface>> _modeHandlers = [];
    private readonly Identity _internalIdentity;
    private readonly Identity? _networkIdentity;
    private readonly SharedInstanceSession _shared;
    private readonly BlackholeStore _blackholes;
    private readonly RuntimeStateStore _stateStore;
    private readonly string _storageDirectory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lifecycleGate = new();
    private Task? _startTask;
    private Task? _disposeTask;
    private Task? _runTask;
    private ManagementRpcServer? _rpcServer;
    private ManagementRpcClient? _rpcClient;
    private DiscoveryService? _discovery;
    private DiscoveryAnnouncer? _announcer;
    private IAnnounceHandler? _discoveryHandler;
    private BlackholeService? _blackholeService;
    private DateTimeOffset _startedAt;
    private byte[] _rpcKey = [];
    private readonly RuntimeExecutionPolicy _executionPolicy;

    private RnsdHost(PacketInterfaceManager manager, TransportRouter router, List<(string Name, IPacketInterface Interface)> interfaces,
        RnsdConfig configuration, InterfaceFactoryRegistry? extensions, Action<int, string> log,
        Identity internalIdentity, Identity? networkIdentity, SharedInstanceSession shared, BlackholeStore blackholes, string storageDirectory,
        RuntimeExecutionPolicy executionPolicy)
    {
        _manager = manager;
        Router = router;
        _interfaces = interfaces;
        _configuration = configuration;
        _extensions = extensions;
        _log = log;
        _internalIdentity = internalIdentity;
        _networkIdentity = networkIdentity;
        _shared = shared;
        _blackholes = blackholes;
        _storageDirectory = storageDirectory;
        _stateStore = new RuntimeStateStore(storageDirectory);
        _executionPolicy = executionPolicy;
        manager.Diagnostic += (iface, error) => log(1, $"{InterfaceName(iface)}: {error.Message}");
        manager.InterfaceRemoved += router.DetachInterface;
    }

    public TransportRouter Router { get; }

    public IReadOnlyList<(string Name, IPacketInterface Interface)> Interfaces
    {
        get { lock (_gate) return _interfaces.ToArray(); }
    }

    public bool IsConnectedToSharedInstance => _shared.Role == SharedInstanceRole.Client;
    public SharedInstanceRole SharedRole => _shared.Role;
    public BlackholeStore Blackholes => _blackholes;
    public DiscoveryService? Discovery => _discovery;

    public Dictionary<object, object?> GetStatistics(bool profiling = false)
    {
        var stats = RuntimeStatistics.Collect(_manager, Interfaces, Router.TransportEnabled ? Router.TransportId : null, profiling);
        if (Router.TransportEnabled)
        {
            stats["transport_uptime"] = _startedAt == default ? 0d : (DateTimeOffset.UtcNow - _startedAt).TotalSeconds;
            stats["network_id"] = _networkIdentity?.Hash;
            stats["probe_responder"] = null;
        }
        return stats;
    }

    /// <summary>Creates (but does not start) the host. The transport identity is loaded from, or created at, <paramref name="transportIdentityPath"/>.</summary>
    public static RnsdHost Create(RnsdConfig config, string transportIdentityPath, Action<int, string> log,
        InterfaceFactoryRegistry? extensions = null, RuntimeExecutionPolicy? executionPolicy = null)
        => CreateAsync(config, transportIdentityPath, log, extensions, executionPolicy).GetAwaiter().GetResult();

    public static async Task<RnsdHost> CreateAsync(RnsdConfig config, string transportIdentityPath, Action<int, string> log,
        InterfaceFactoryRegistry? extensions = null, RuntimeExecutionPolicy? executionPolicy = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);
        var storageDirectory = Path.GetDirectoryName(Path.GetFullPath(transportIdentityPath))!;
        var internalIdentity = RuntimeStateStore.LoadOrCreateIdentity(transportIdentityPath);
        Identity? identity = null;
        Identity? networkIdentity = null;
        SharedInstanceSession? session = null;
        RnsdHost? constructed = null;
        var interfaces = new List<(string Name, IPacketInterface Interface)>();
        try
        {
            identity = config.EnableTransport || config.StaticTransportIdentity ? internalIdentity : Identity.Generate();
            if (config.NetworkIdentityPath is { } networkPath)
            {
                if (networkPath.StartsWith("~/", StringComparison.Ordinal) || networkPath.StartsWith("~\\", StringComparison.Ordinal))
                    networkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), networkPath[2..]);
                networkIdentity = RuntimeStateStore.LoadOrCreateIdentity(networkPath);
            }
            session = await SharedInstanceSession.OpenAsync(config.SharedInstance, cancellationToken,
                error => log(2, $"Shared-instance election: {error.Message}")).ConfigureAwait(false);
            var modes = new Dictionary<IPacketInterface, InterfaceMode>();
            if (session.Bearer is { } sharedBearer) interfaces.Add(("Local shared instance", sharedBearer));
            if (session.Role != SharedInstanceRole.Client)
                foreach (var cfg in config.Interfaces.Where(i => i.Enabled))
                {
                    var iface = CreateInterface(cfg, extensions);
                    interfaces.Add((cfg.Name, iface));
                    modes[iface] = cfg.Mode;
                }
            var blackholes = new BlackholeStore(Path.Combine(storageDirectory, "blackhole"), identity.Hash, config.Blackholes.Sources)
            {
                Diagnostic = (message, error) => log(1, $"{message}: {error.Message}"),
            };
            var manager = new PacketInterfaceManager(interfaces.Select(i => i.Interface), keepAlive: true);
            var router = new TransportRouter(manager, new TransportRouterOptions
            {
                TransportEnabled = config.EnableTransport && session.Role != SharedInstanceRole.Client,
                TransportIdentity = identity,
                IsIdentityBlocked = blackholes.IsBlackholed,
            })
            {
                Log = message => log(5, message),
            };
            var host = new RnsdHost(manager, router, interfaces, config, extensions, log,
                internalIdentity, networkIdentity, session, blackholes, storageDirectory, executionPolicy ?? new());
            constructed = host;
            foreach (var (iface, mode) in modes) host.ConfigureMode(iface, mode);
            host.ConfigureSharedRouting();
            host.InitializeServices();
            return host;
        }
        catch (Exception original)
        {
            var cleanupErrors = new List<Exception>();
            async Task Cleanup(Func<ValueTask> action)
            {
                try { await action().ConfigureAwait(false); }
                catch (Exception error) { log(1, $"Runtime construction cleanup: {error.Message}"); cleanupErrors.Add(error); }
            }
            if (constructed is not null) await Cleanup(constructed.DisposeAsync).ConfigureAwait(false);
            else
            {
                foreach (var (_, iface) in interfaces) await Cleanup(iface.DisposeAsync).ConfigureAwait(false);
                if (session is not null) await Cleanup(session.DisposeAsync).ConfigureAwait(false);
                if (!ReferenceEquals(identity, internalIdentity)) identity?.Dispose();
                networkIdentity?.Dispose();
                internalIdentity.Dispose();
            }
            if (cleanupErrors.Count != 0) throw new AggregateException("Runtime construction and cleanup failed.", new[] { original }.Concat(cleanupErrors));
            throw;
        }
    }

    /// <summary>Starts every interface; throws (after disposing everything) if one cannot be started.</summary>
    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_startTask is not null) throw new InvalidOperationException("The host is already started; construct a new host to restart.");
            _startTask = StartCoreAsync(cancellationToken);
            return new ValueTask(_startTask);
        }
    }

    /// <summary>Routes packets until cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_startTask is null || !_startTask.IsCompletedSuccessfully) throw new InvalidOperationException("Start the host before running it.");
            if (_runTask is not null) throw new InvalidOperationException("The host has already run; construct a new host to restart.");
            _runTask = RunCoreAsync(cancellationToken);
            return _runTask;
        }
    }

    public static IPacketInterface CreateInterface(RnsdInterfaceConfig cfg, InterfaceFactoryRegistry? extensions = null)
    {
        if (extensions?.Contains(cfg.Get("type") ?? "") == true) return extensions.Create(cfg);
        switch (cfg.Type)
        {
            case RnsdInterfaceType.TcpServer:
                return new TcpServerPacketInterface(new IPEndPoint(Address(cfg, "listen_ip", "0.0.0.0"), cfg.GetInt("listen_port", cfg.GetInt("port", 4242), 0, 65535)),
                    perClientInterfaces: true);
            case RnsdInterfaceType.TcpClient:
                return new TcpClientPacketInterface(cfg.Get("target_host") ?? throw new FormatException($"Interface '{cfg.Name}': target_host is required."),
                    cfg.GetInt("target_port", cfg.GetInt("port", 4242), 1, 65535));
            case RnsdInterfaceType.Udp:
            {
                var port = cfg.GetInt("port", 4242, 0, 65535);
                return new UdpPacketInterface(
                    new IPEndPoint(Address(cfg, "listen_ip", "0.0.0.0"), cfg.GetInt("listen_port", port, 0, 65535)),
                    new IPEndPoint(Address(cfg, "forward_ip", "255.255.255.255"), cfg.GetInt("forward_port", port, 1, 65535)));
            }
            case RnsdInterfaceType.Serial:
                return new SerialPacketInterface(cfg.Get("port") ?? throw new FormatException($"Interface '{cfg.Name}': port is required."),
                    cfg.GetInt("speed", 9600, 1));
            default:
                throw new FormatException($"Interface '{cfg.Name}': unsupported type.");
        }
    }

    private string InterfaceName(IPacketInterface iface)
    {
        lock (_gate)
        {
            if (_interfaces.FirstOrDefault(i => ReferenceEquals(i.Interface, iface)).Name is { } name) return name;
        }
        var peer = (iface as IPacketInterfaceDiagnostics)?.GetDiagnostics().GetValueOrDefault("peer");
        return peer is null ? iface.GetType().Name : $"{iface.GetType().Name}[{peer}]";
    }

    private void ConfigureMode(IPacketInterface iface, InterfaceMode mode)
    {
        Router.SetInterfaceMode(iface, mode);
        if (iface is not ISpawningPacketInterface spawner) return;
        Action<IPacketInterface> handler = child => Router.SetInterfaceMode(child, mode);
        _modeHandlers.Add(spawner, handler);
        spawner.ChildConnected += handler;
    }

    private static IPAddress Address(RnsdInterfaceConfig cfg, string key, string fallback)
    {
        var text = cfg.Get(key) ?? fallback;
        return IPAddress.TryParse(text, out var address) ? address : throw new FormatException($"Interface '{cfg.Name}': '{key}' must be an IP address, got '{text}'.");
    }
}
