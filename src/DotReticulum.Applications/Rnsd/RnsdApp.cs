using System.Net;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnsd;

/// <summary>Builds the interface set, the manager and the <see cref="TransportRouter"/> for one <c>rnsd</c> instance.</summary>
public sealed class RnsdHost : IAsyncDisposable
{
    private readonly PacketInterfaceManager _manager;

    private RnsdHost(PacketInterfaceManager manager, TransportRouter router, IReadOnlyList<(string Name, IPacketInterface Interface)> interfaces)
    {
        _manager = manager;
        Router = router;
        Interfaces = interfaces;
    }

    public TransportRouter Router { get; }

    public IReadOnlyList<(string Name, IPacketInterface Interface)> Interfaces { get; }

    /// <summary>Creates (but does not start) the host. The transport identity is loaded from, or created at, <paramref name="transportIdentityPath"/>.</summary>
    public static RnsdHost Create(RnsdConfig config, string transportIdentityPath, Action<int, string> log)
    {
        Identity identity;
        if (File.Exists(transportIdentityPath))
        {
            identity = IdentityFile.Load(transportIdentityPath);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(transportIdentityPath))!);
            identity = Identity.Generate();
            IdentityFile.SaveNew(identity, transportIdentityPath);
            log(4, $"Created transport identity {identity.HexHash}");
        }

        var interfaces = new List<(string Name, IPacketInterface Interface)>();
        var modes = new Dictionary<IPacketInterface, InterfaceMode>();
        foreach (var cfg in config.Interfaces.Where(i => i.Enabled))
        {
            var iface = CreateInterface(cfg);
            interfaces.Add((cfg.Name, iface));
            modes[iface] = cfg.Mode;
        }

        var manager = new PacketInterfaceManager(interfaces.Select(i => i.Interface));
        var router = new TransportRouter(manager, new TransportRouterOptions { TransportEnabled = config.EnableTransport, TransportIdentity = identity })
        {
            Log = message => log(5, message),
        };

        foreach (var (iface, mode) in modes)
        {
            router.SetInterfaceMode(iface, mode);
            if (iface is ISpawningPacketInterface spawner)
            {
                var spawnedMode = mode;
                spawner.ChildConnected += child =>
                {
                    router.SetInterfaceMode(child, spawnedMode);
                    log(5, $"Client connected to a {spawner.GetType().Name}");
                };
            }
        }

        return new RnsdHost(manager, router, interfaces);
    }

    /// <summary>Starts every interface; throws (after disposing everything) if one cannot be started.</summary>
    public ValueTask StartAsync(CancellationToken cancellationToken) => _manager.StartAsync(cancellationToken);

    /// <summary>Routes packets until cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken) => Router.RunAsync(cancellationToken);

    public ValueTask DisposeAsync() => _manager.DisposeAsync();

    public static IPacketInterface CreateInterface(RnsdInterfaceConfig cfg)
    {
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

    private static IPAddress Address(RnsdInterfaceConfig cfg, string key, string fallback)
    {
        var text = cfg.Get(key) ?? fallback;
        return IPAddress.TryParse(text, out var address) ? address : throw new FormatException($"Interface '{cfg.Name}': '{key}' must be an IP address, got '{text}'.");
    }
}

/// <summary>Command line front end of <c>rnsd</c>.</summary>
public static class RnsdApp
{
    public const string Usage = """
        DotReticulum rnsd - Reticulum transport daemon (experimental)

        Usage:
          rnsd [--config <dir|file>] [-v|-vv] [-q]
          rnsd --exampleconfig       Print an example configuration
          rnsd --help

        Options:
          -c, --config <path>   Configuration directory (containing 'config') or a config file.
                                Default: ~/.dotreticulum. State (transport identity) is kept in
                                <dir>/storage/transport_identity.
          -v, --verbose         Raise the log level (repeatable); -q lowers it. The default level
                                comes from [logging] loglevel (0 critical .. 7 extreme; default 4).
          --exampleconfig       Print an example configuration to stdout and exit.
          -h, --help            Show this help.

        The config file is a subset of Reticulum's ini format; see docs/rnsd.md.
        Stop with Ctrl+C or SIGTERM.
        """;

    public const string ExampleConfig = """
        # DotReticulum rnsd example configuration (subset of the Reticulum ini format)

        [reticulum]
          enable_transport = yes

        [logging]
          loglevel = 4

        [interfaces]

          [[TCP Server]]
            type = TCPServerInterface
            enabled = yes
            listen_ip = 0.0.0.0
            listen_port = 4242

          # [[TCP Client]]
          #   type = TCPClientInterface
          #   enabled = yes
          #   target_host = 127.0.0.1
          #   target_port = 4243

          # [[UDP]]
          #   type = UDPInterface
          #   enabled = yes
          #   listen_ip = 0.0.0.0
          #   listen_port = 4244
          #   forward_ip = 255.255.255.255
          #   forward_port = 4244

          # [[Serial]]
          #   type = SerialInterface
          #   enabled = yes
          #   port = COM3
          #   speed = 115200
        """;

    private static readonly string[] LevelNames = ["CRITICAL", "ERROR", "WARNING", "NOTICE", "INFO", "VERBOSE", "DEBUG", "EXTREME"];

    /// <summary>Runs the daemon. Returns the process exit code; completes after <paramref name="stop"/> is cancelled and everything is disposed.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        string? configArg = null;
        var verbosity = 0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "-?":
                    output.WriteLine(Usage);
                    return 0;
                case "--exampleconfig":
                    output.WriteLine(ExampleConfig);
                    return 0;
                case "-c" or "--config":
                    if (i + 1 >= args.Length) return Fail(error, $"{args[i]} requires a path.");
                    configArg = args[++i];
                    break;
                case "-v" or "--verbose":
                    verbosity++;
                    break;
                case "-vv":
                    verbosity += 2;
                    break;
                case "-q" or "--quiet":
                    verbosity--;
                    break;
                default:
                    error.WriteLine(Usage);
                    return Fail(error, $"unknown argument '{args[i]}'.");
            }
        }

        var (configDir, configFile) = ResolveConfig(configArg);
        if (!File.Exists(configFile))
        {
            return Fail(error, $"configuration file '{configFile}' does not exist. Create it (see --exampleconfig).");
        }

        RnsdConfig config;
        try { config = RnsdConfigParser.Parse(await File.ReadAllTextAsync(configFile, CancellationToken.None).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException) { return Fail(error, $"{configFile}: {ex.Message}"); }

        var level = Math.Clamp(config.LogLevel + verbosity, 0, 7);
        var gate = new object();
        void Log(int messageLevel, string message)
        {
            if (messageLevel > level) return;
            lock (gate) output.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{LevelNames[messageLevel]}] {message}");
        }

        RnsdHost host;
        try { host = RnsdHost.Create(config, Path.Combine(configDir, "storage", "transport_identity"), Log); }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return Fail(error, ex.Message);
        }

        await using (host.ConfigureAwait(false))
        {
            try { await host.StartAsync(stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return 0; }
            catch (Exception ex) { return Fail(error, $"could not start interfaces: {ex.Message}"); }

            Log(4, $"Transport instance {Convert.ToHexStringLower(host.Router.TransportId)} started; transport {(config.EnableTransport ? "enabled" : "disabled")}");
            foreach (var (name, iface) in host.Interfaces) Log(4, $"Interface '{name}' ({iface.GetType().Name}) is up");
            Log(4, "rnsd ready");

            try { await host.RunAsync(stop).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Fail(error, $"router failed: {ex.Message}"); }
            Log(4, "Shutting down");
        }

        return 0;
    }

    public static (string Dir, string File) ResolveConfig(string? configArg)
    {
        if (configArg is null)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotreticulum");
            return (dir, Path.Combine(dir, "config"));
        }

        var full = Path.GetFullPath(configArg);
        return Directory.Exists(full) ? (full, Path.Combine(full, "config")) : (Path.GetDirectoryName(full)!, full);
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine($"rnsd: {message}");
        return 1;
    }
}
