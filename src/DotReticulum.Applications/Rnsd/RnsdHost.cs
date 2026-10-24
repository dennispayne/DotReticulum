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
    private readonly string _resourceStorageDirectory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lifecycleGate = new();
    private Task? _startTask;
    private Task? _disposeTask;
    private Task? _runTask;
    private ManagementRpcServer? _rpcServer;
    private ManagementRpcClient? _rpcClient;
    private DiscoveryService? _discovery;
    private volatile DiscoveryAnnouncer? _announcer;
    private IAnnounceHandler? _discoveryHandler;
    private BlackholeService? _blackholeService;
    private DateTimeOffset _startedAt;
    private byte[] _rpcKey = [];
    private readonly RuntimeExecutionPolicy _executionPolicy;
    private readonly TimeProvider _timeProvider;

    private RnsdHost(PacketInterfaceManager manager, TransportRouter router, List<(string Name, IPacketInterface Interface)> interfaces,
        RnsdConfig configuration, InterfaceFactoryRegistry? extensions, Action<int, string> log,
        Identity internalIdentity, Identity? networkIdentity, SharedInstanceSession shared, BlackholeStore blackholes, string storageDirectory,
        RuntimeExecutionPolicy executionPolicy, TimeProvider timeProvider)
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
        _resourceStorageDirectory = Path.Combine(storageDirectory, "resources");
        Router.Links.DefaultResourcePolicy = Router.Links.DefaultResourcePolicy with
        {
            StorageDirectory = _resourceStorageDirectory,
        };
        _stateStore = new RuntimeStateStore(storageDirectory);
        _executionPolicy = executionPolicy;
        _timeProvider = timeProvider;
        manager.Diagnostic += (iface, error) => log(1, $"{InterfaceName(iface)}: {error.Message}");
        manager.InterfaceRemoved += TransportInterfaceRemoved;
        manager.InterfaceConnected += TransportInterfaceConnected;
        manager.InterfaceDisconnected += TransportInterfaceDisconnected;
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
        InterfaceFactoryRegistry? extensions = null, RuntimeExecutionPolicy? executionPolicy = null, TimeProvider? timeProvider = null)
        => CreateAsync(config, transportIdentityPath, log, extensions, executionPolicy, timeProvider: timeProvider).GetAwaiter().GetResult();

    public static async Task<RnsdHost> CreateAsync(RnsdConfig config, string transportIdentityPath, Action<int, string> log,
        InterfaceFactoryRegistry? extensions = null, RuntimeExecutionPolicy? executionPolicy = null, CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
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
                    var iface = CreateInterface(cfg, extensions, storageDirectory, identity.Hash);
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
                InboundDataCapacity = config.Routing.InboundDataCapacity,
                InboundAnnounceCapacity = config.Routing.InboundAnnounceCapacity,
                InboundPathRequestCapacity = config.Routing.InboundPathRequestCapacity,
                InboundIngressLimitedCapacity = config.Routing.InboundIngressLimitedCapacity,
                TimeProvider = timeProvider,
            })
            {
                Log = message => log(5, message),
            };
            var host = new RnsdHost(manager, router, interfaces, config, extensions, log,
                internalIdentity, networkIdentity, session, blackholes, storageDirectory, executionPolicy ?? new(), timeProvider ?? TimeProvider.System);
            constructed = host;
            foreach (var cfg in config.Interfaces.Where(i => i.Enabled))
            {
                if (interfaces.FirstOrDefault(i => i.Name == cfg.Name && modes.ContainsKey(i.Interface)).Interface is { } iface)
                {
                    manager.SetInterfaceAccessCode(iface, cfg.CreateAccessCode());
                    manager.SetInterfaceOutgoing(iface, cfg.Outgoing);
                    host.ConfigureMode(iface, cfg.Mode, cfg.Routing.ToAnnounceOptions);
                }
            }
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

    public static IPacketInterface CreateInterface(RnsdInterfaceConfig cfg, InterfaceFactoryRegistry? extensions = null,
        string? storageDirectory = null, ReadOnlyMemory<byte> transportIdentityHash = default)
    {
        if (extensions?.Contains(cfg.Get("type") ?? "") == true) return extensions.Create(cfg);
        switch (cfg.Type)
        {
            case RnsdInterfaceType.TcpServer:
                return new TcpServerPacketInterface(cfg.Get("listen_ip"), cfg.GetInt("listen_port", cfg.GetInt("port", 4242), 0, 65535),
                    device: cfg.Get("device"), preferIpv6: cfg.GetBool("prefer_ipv6"),
                    perClientInterfaces: true, nominalBitrate: cfg.Bitrate, fixedMtu: cfg.FixedMtu,
                    i2pTunneled: cfg.GetBool("i2p_tunneled"));
            case RnsdInterfaceType.TcpClient:
                return new TcpClientPacketInterface(cfg.Get("target_host") ?? throw new FormatException($"Interface '{cfg.Name}': target_host is required."),
                    cfg.GetInt("target_port", cfg.GetInt("port", 4242), 1, 65535),
                    nominalBitrate: cfg.Bitrate, reconnect: true, maxReconnectAttempts: cfg.MaxReconnectAttempts,
                    fixedMtu: cfg.FixedMtu, connectTimeout: cfg.ConnectTimeout,
                    kissFraming: cfg.GetBool("kiss_framing"), i2pTunneled: cfg.GetBool("i2p_tunneled"));
            case RnsdInterfaceType.Udp:
            {
                int? OptionalPort(string key) => cfg.Get(key) is null ? null : cfg.GetInt(key, 0, 0, 65535);
                return new UdpPacketInterface(new UdpPacketInterfaceOptions
                {
                    ListenAddress = cfg.Get("listen_ip"), ListenPort = OptionalPort("listen_port"),
                    ForwardAddress = cfg.Get("forward_ip"), ForwardPort = OptionalPort("forward_port"),
                    Port = OptionalPort("port"), Device = cfg.Get("device"), NominalBitrate = cfg.Bitrate,
                });
            }
            case RnsdInterfaceType.Serial:
                return new SerialPacketInterface(cfg.Get("port") ?? throw new FormatException($"Interface '{cfg.Name}': port is required."),
                    cfg.GetInt("speed", 9600, 1), parity: SerialParity(cfg), dataBits: cfg.GetInt("databits", 8, 5, 8),
                    stopBits: cfg.GetInt("stopbits", 1, 1, 2) == 2 ? System.IO.Ports.StopBits.Two : System.IO.Ports.StopBits.One,
                    nominalBitrate: cfg.Bitrate, reconnect: true);
            case RnsdInterfaceType.Auto:
                return new AutoPacketInterface(new AutoPacketInterfaceOptions
                {
                    GroupId = cfg.Get("group_id") ?? "reticulum",
                    DiscoveryScope = cfg.Get("discovery_scope") ?? "link",
                    MulticastAddressType = cfg.Get("multicast_address_type") ?? "temporary",
                    DiscoveryPort = cfg.GetInt("discovery_port", 29716, 1, 65534),
                    DataPort = cfg.GetInt("data_port", 42671, 1, 65535),
                    Devices = cfg.GetList("devices"), IgnoredDevices = cfg.GetList("ignored_devices"),
                    ConfiguredBitrate = cfg.Bitrate,
                });
            case RnsdInterfaceType.Pipe:
            {
                var command = CliSupport.ShellSplit(cfg.Get("command") ?? throw new FormatException($"Interface '{cfg.Name}': command is required."));
                if (command.Count == 0 || string.IsNullOrWhiteSpace(command[0]))
                    throw new FormatException($"Interface '{cfg.Name}': command requires an executable.");
                return new PipePacketInterface(command[0], command.Skip(1), new PipePacketInterfaceOptions
                {
                    RespawnDelay = TimeSpan.FromSeconds(cfg.GetDouble("respawn_delay", 5, double.Epsilon)),
                    ConfiguredBitrate = cfg.ConfiguredBitrate,
                });
            }
            case RnsdInterfaceType.Kiss:
                return new KissPacketInterface(cfg.Get("port")!, CreateKissOptions(cfg));
            case RnsdInterfaceType.Ax25Kiss:
                return new Ax25KissPacketInterface(cfg.Get("port")!, CreateKissOptions(cfg, ax25: true) as Ax25KissOptions
                    ?? throw new InvalidOperationException("AX.25 settings were not constructed."));
            case RnsdInterfaceType.Weave:
                return new WeavePacketInterface(cfg.Get("port")!, new WeaveOptions { ConfiguredBitrate = cfg.Bitrate });
            case RnsdInterfaceType.I2p:
                return new I2pPacketInterface(new I2pPacketInterfaceOptions
                {
                    Name = cfg.Name,
                    StoragePath = storageDirectory ?? throw new FormatException($"Interface '{cfg.Name}': I2P requires its runtime storage directory."),
                    TransportIdentityHash = transportIdentityHash,
                    Connectable = cfg.GetBool("connectable"),
                    Peers = cfg.GetList("peers"),
                    ConfiguredBitrate = cfg.ConfiguredBitrate,
                });
            case RnsdInterfaceType.RNode:
                return new RNodePacketInterface(cfg.Get("port")!, new RNodeOptions
                {
                    Radio = CreateRadioOptions(cfg),
                    ConfiguredBitrate = cfg.ConfiguredBitrate,
                    IdCallsign = cfg.Get("id_callsign"),
                    IdInterval = RadioIdInterval(cfg),
                });
            case RnsdInterfaceType.RNodeMulti:
                return new RNodeMultiPacketInterface(cfg.Get("port")!, new RNodeMultiOptions
                {
                    EnableAllChildren = cfg.GetBool("enabled"),
                    ConfiguredBitrate = cfg.ConfiguredBitrate,
                    IdCallsign = cfg.Get("id_callsign"),
                    IdInterval = RadioIdInterval(cfg),
                    Subinterfaces = cfg.Subinterfaces.Select(p => CreateRadioSubinterface(cfg, p.Key, p.Value)).ToArray(),
                });
            case RnsdInterfaceType.Backbone:
            {
                var options = new BackbonePacketInterfaceOptions
                {
                    NominalBitrate = cfg.Bitrate, InterfaceAccessCodeSize = cfg.IfacSize,
                    Device = cfg.Get("device"), PreferIPv6 = cfg.GetBool("prefer_ipv6"),
                    MaximumReconnectAttempts = cfg.MaxReconnectAttempts, ConnectTimeout = cfg.ConnectTimeout,
                    BlockFastFlapping = cfg.GetBool("block_fast_flapping", true),
                    FastFlapThreshold = TimeSpan.FromSeconds(cfg.GetDouble("fast_flapping_threshold", 20, double.Epsilon)),
                    FastFlapGrace = cfg.GetInt("fast_flapping_grace", 5),
                    FastFlapBlockTime = TimeSpan.FromMinutes(cfg.GetDouble("fast_flapping_block_time", 720, double.Epsilon)),
                };
                return cfg.Get("target_host") is { } host
                    ? new BackboneClientPacketInterface(host, cfg.GetInt("target_port", 0, 1, 65535), options)
                    : new BackbonePacketInterface(cfg.Get("listen_ip"), cfg.GetInt("listen_port", 0, 0, 65535), options);
            }
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

    private void ConfigureMode(IPacketInterface iface, InterfaceMode mode, Func<InterfaceAnnounceOptions>? optionsFactory = null)
    {
        var options = optionsFactory?.Invoke();
        void Configure(IPacketInterface target, InterfaceAnnounceOptions? controls)
        {
            var targetMode = mode;
            if (target is I2pPacketPeer { IsInitiator: true })
            {
                targetMode = InterfaceMode.Full;
                controls = _configuration.Routing.CreateInterfaceOptions(_configuration.EnableTransport);
            }
            Router.SetInterfaceMode(target, targetMode);
            if (controls is not null) Router.SetInterfaceAnnounceOptions(target, controls);
            var captured = controls;
            lock (_gate) _interfaceReconfigure[target] = () => Configure(target, captured);
        }
        Configure(iface, options);
        if (iface is not ISpawningPacketInterface spawner) return;
        Action<IPacketInterface> handler = child => Configure(child, optionsFactory?.Invoke());
        _modeHandlers.Add(spawner, handler);
        spawner.ChildConnected += handler;
    }

    private static IPAddress Address(RnsdInterfaceConfig cfg, string key, string fallback)
    {
        var text = cfg.Get(key) ?? fallback;
        return IPAddress.TryParse(text, out var address) ? address : throw new FormatException($"Interface '{cfg.Name}': '{key}' must be an IP address, got '{text}'.");
    }
}
