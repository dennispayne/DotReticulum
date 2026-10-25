namespace DotReticulum.Applications.Rnsd;

/// <summary>Command line front end of <c>rnsd</c>.</summary>
public static class RnsdApp
{
    public const string Usage = """
        DotReticulum rnsd - Reticulum transport daemon (experimental)

        Usage:
          rnsd [--config <dir|file>] [-v|-vv] [-q] [--service] [--interactive]
          rnsd --exampleconfig       Print an example configuration
          rnsd --help

        Options:
          -c, --config <path>   Configuration directory (containing 'config') or a config file.
                                Search: system /etc/reticulum, ~/.config/reticulum, ~/.reticulum.
                                Missing configs are created. State (transport identity) is kept in
                                <dir>/storage/transport_identity.
          -v, --verbose         Raise the log level (repeatable); -q lowers it. The default level
                                comes from [logging] loglevel (0 critical .. 8 extreme).
          -s, --service         Log to <config dir>/logfile; ignore verbosity adjustments.
          -i, --interactive     Native management shell: help, status, interfaces, persist, quit.
                                A deliberate replacement for the upstream Python REPL; no code execution.
          --allow-discovery-commands
                                Explicitly authorize configured discovery metadata executables.
                                No shell or arguments; bounded execution. Disabled by default.
          --version             Show DotReticulum and pinned reference versions.
          --exampleconfig       Print an example configuration to stdout and exit.
          -h, --help            Show this help.

        Unsupported configuration controls are errors; see docs/rnsd.md.
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

    /// <summary>Runs the daemon. Returns the process exit code; completes after <paramref name="stop"/> is cancelled and everything is disposed.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop, TextReader? input = null)
    {
        try { return await RunCoreAsync(args, output, error, stop, input).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException or AggregateException)
        { return Fail(error, ex.Message); }
    }

    private static async Task<int> RunCoreAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop, TextReader? input)
    {
        string? configArg = null;
        var verbosity = 0;
        var service = false;
        var interactive = false;
        var example = false;
        var help = false;
        var version = false;
        var allowDiscoveryCommands = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "-?":
                    help = true;
                    break;
                case "--exampleconfig":
                    example = true;
                    break;
                case "--version":
                    version = true;
                    break;
                case "-s" or "--service":
                    service = true;
                    break;
                case "-i" or "--interactive":
                    interactive = true;
                    break;
                case "--allow-discovery-commands":
                    allowDiscoveryCommands = true;
                    break;
                case "-c" or "--config":
                    if (i + 1 >= args.Length) return ParseFail(error, $"{args[i]} requires a path.");
                    if (args[i + 1].StartsWith('-')) return ParseFail(error, $"{args[i]} requires a path.");
                    configArg = args[++i];
                    break;
                case "-v" or "--verbose":
                    verbosity++;
                    break;
                case "-q" or "--quiet":
                    verbosity--;
                    break;
                default:
                    if (args[i].StartsWith("--config=", StringComparison.Ordinal))
                    {
                        configArg = args[i]["--config=".Length..];
                        if (configArg.Length == 0) return ParseFail(error, "--config requires a path.");
                        break;
                    }
                    if (args[i].Length > 2 && args[i][0] == '-' && args[i][1..].All(c => c is 'v' or 'q'))
                    {
                        verbosity += args[i][1..].Sum(c => c == 'v' ? 1 : -1);
                        break;
                    }
                    error.WriteLine(Usage);
                    return ParseFail(error, $"unknown argument '{args[i]}'.");
            }
        }

        if (help) { output.WriteLine(Usage); return 0; }
        if (version)
        {
            output.WriteLine($"rnsd DotReticulum {typeof(RnsdApp).Assembly.GetName().Version} (experimental; reference RNS 1.5.7)");
            return 0;
        }
        if (example) { output.WriteLine(ExampleConfig); return 0; }

        RnsdConfig config;
        string configDir, configFile;
        bool created;
        try
        {
            (configDir, configFile) = ResolveConfig(configArg);
            created = RnsdConfigurationFiles.EnsureCreated(configDir, configFile);
            config = RnsdConfigParser.Parse(await File.ReadAllTextAsync(configFile, stop).ConfigureAwait(false)) with { SourcePath = configFile };
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        { return Fail(error, ex.Message); }

        var level = RnsdLogger.LevelFor(config, verbosity, service);
        var logger = new RnsdLogger(level, config.LogTimestamps, output, error, service ? Path.Combine(configDir, "logfile") : null);
        void Log(int messageLevel, string message) => logger.Log(messageLevel, message);
        if (created)
            Log(2, $"Default config file created at '{configFile}'. AutoInterface is disabled by default; no external interface is enabled. Explicitly enable network discovery or configure another interface and restart.");

        RnsdHost host;
        try
        {
            host = await RnsdHost.CreateAsync(config, Path.Combine(configDir, "storage", "transport_identity"), Log,
                executionPolicy: new Runtime.RuntimeExecutionPolicy { AllowDiscoveryCommands = allowDiscoveryCommands },
                cancellationToken: stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return Fail(error, ex.Message);
        }

        await using (host.ConfigureAwait(false))
        {
            try { await host.StartAsync(stop).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException)
            { return Fail(error, $"could not start interfaces: {ex.Message}"); }

            Log(4, $"Transport instance {Convert.ToHexStringLower(host.Router.TransportId)} started; transport {(config.EnableTransport ? "enabled" : "disabled")}");
            foreach (var (name, iface) in host.Interfaces) Log(4, $"Interface '{name}' ({iface.GetType().Name}) is up");
            Log(4, "rnsd ready");

            try
            {
                if (interactive)
                {
                    using var interactiveStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    var run = host.RunAsync(interactiveStop.Token);
                    var shell = RnsdInteractiveShell.RunAsync(host, input ?? Console.In, output, error, interactiveStop.Token);
                    try { await Task.WhenAny(run, shell).ConfigureAwait(false); }
                    finally { await interactiveStop.CancelAsync().ConfigureAwait(false); }
                    try { await run.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (interactiveStop.IsCancellationRequested) { }
                    try { await shell.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (interactiveStop.IsCancellationRequested) { }
                }
                else await host.RunAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            { return Fail(error, $"daemon failed: {ex.Message}"); }
            Log(4, "Shutting down");
            try
            {
                using var persistTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await host.PersistAsync(persistTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
            { return Fail(error, $"shutdown persistence failed: {ex.Message}"); }
        }

        return 0;
    }

    public static (string Dir, string File) ResolveConfig(string? configArg) => RnsdConfigurationFiles.Resolve(configArg);

    public static (string Directory, string File, RnsdConfig Config) LoadConfiguration(string? configArg, Runtime.InterfaceFactoryRegistry? extensions = null)
    {
        var (directory, file) = ResolveConfig(configArg);
        RnsdConfigurationFiles.EnsureCreated(directory, file);
        return (directory, file, RnsdConfigParser.Parse(System.IO.File.ReadAllText(file), extensions) with { SourcePath = file });
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine($"rnsd: {message}");
        return 1;
    }

    private static int ParseFail(TextWriter error, string message)
    {
        error.WriteLine($"rnsd: {message}");
        return 2;
    }
}
