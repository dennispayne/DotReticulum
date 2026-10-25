using System.Globalization;
using System.Text.RegularExpressions;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnsh;

public static class RnshApp
{
    public const string Usage = """
        Reticulum Remote Shell Utility (experimental)
        rnsh [-c STATE] [--rnsconfig RNS] [-i IDENTITY] [-v|-q] [-p] [--version]
        rnsh -l [-s SERVICE] [-b PERIOD] [-a HASH ...|-n] [-A|-C] -- [COMMAND ARGS...]
        rnsh [-N] [-m] [-w SECONDS] DESTINATION -- [COMMAND ARGS...]
        --config/-c is shell state; --rnsconfig is the Reticulum configuration.
        -l/--listen explicitly authorizes remote process execution.
        -n/--no-auth accepts anyone: dangerous. Default authorization is deny.
        -A/--remote-command-as-args appends remote argv to the default command.
        -C/--no-remote-command rejects supplied remote commands.
        -N/--no-id suppresses identification; -m/--mirror mirrors the process exit.
        -p/--print-identity prints identity (and destination in listener mode).
        Native terminal support uses Linux PTY or Windows ConPTY; mixed Windows pipe/PTY endpoints are unsupported.
        """;

    private sealed class Options
    {
        public string? Config, RnsConfig, Identity, Service, Destination;
        public bool Listen, Print, NoAuth, NoId, Mirror, Append, NoCommand;
        public int? Announce;
        public double Timeout = 30;
        public int Verbosity;
        public List<string> Allowed = [];
        public string[] Command = [];
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        var o = new Options();
        try
        {
            var split = System.Array.IndexOf(args, "--");
            if (split >= 0) { o.Command = args[(split + 1)..]; args = args[..split]; }
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
                switch (args[i])
                {
                    case "-h" or "--help": await output.WriteLineAsync(Usage); return 0;
                    case "--version": await output.WriteLineAsync("rnsh 1.5.7 (protocol 0.3.0)"); return 0;
                    case "-c" or "--config": o.Config = Next(); break;
                    case "--rnsconfig": o.RnsConfig = Next(); break;
                    case "-i" or "--identity": o.Identity = Next(); break;
                    case "-s" or "--service": o.Service = Next(); break;
                    case "-l" or "--listen": o.Listen = true; break;
                    case "-p" or "--print-identity": o.Print = true; break;
                    case "-n" or "--no-auth": o.NoAuth = true; break;
                    case "-N" or "--no-id": o.NoId = true; break;
                    case "-m" or "--mirror": o.Mirror = true; break;
                    case "-A" or "--remote-command-as-args": o.Append = true; break;
                    case "-C" or "--no-remote-command": o.NoCommand = true; break;
                    case "-a" or "--allowed": o.Allowed.Add(Next()); break;
                    case "-b" or "--announce": o.Announce = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "-w" or "--timeout": o.Timeout = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "-v" or "--verbose": o.Verbosity++; break;
                    case "-q" or "--quiet": o.Verbosity--; break;
                    default:
                        if (args[i].StartsWith('-') || o.Destination is not null) throw new ArgumentException($"Unknown argument {args[i]}.");
                        o.Destination = args[i]; break;
                }
            }
            if (!double.IsFinite(o.Timeout) || o.Timeout <= 0) throw new ArgumentException("Timeout must be positive and finite.");
            if (!o.Listen && !o.Print && o.Destination is null) { await output.WriteLineAsync(Usage); return 1; }
            if (o.Destination is not null && !CliSupport.TryParseHash(o.Destination, out _)) throw new ArgumentException("Invalid destination hash.");
            foreach (var allowed in o.Allowed)
                if (!CliSupport.TryParseHash(allowed, out _)) throw new ArgumentException("Invalid allowed identity hash.");
            var state = StateDirectory(o.Config);
            Directory.CreateDirectory(state);
            var service = o.Listen ? o.Service ?? "default" : o.Service;
            var safeService = Regex.Replace(service ?? "", @"\W+", "");
            var identityFile = o.Identity is null ? Path.Combine(state, "identity" + (safeService.Length == 0 ? "" : "." + safeService)) : ExpandHome(o.Identity);
            using var identity = RuntimeStateStore.LoadOrCreateIdentity(identityFile);
            var destination = Destination.Create(DestinationType.Single, "rnsh", [], identity);
            if (o.Print)
            {
                if (service is not null) await output.WriteLineAsync($"Using service name \"{service}\"");
                await output.WriteLineAsync($"Identity     : <{identity.HexHash}>");
                if (o.Listen) await output.WriteLineAsync($"Listening on : <{Convert.ToHexStringLower(destination.Hash)}>");
                return 0;
            }
            await using var node = ReticulumNode.Open(o.RnsConfig, (_, message) =>
            {
                if (o.Verbosity > 0) error.WriteLine(message);
            });
            if (o.Listen) return await ListenAsync(node, identity, destination, state, o, output, error, stop).ConfigureAwait(false);
            await node.StartAsync(stop).ConfigureAwait(false);
            var connected = await ClientLinks.ConnectAsync(node, Convert.FromHexString(o.Destination!), TimeSpan.FromSeconds(o.Timeout), stop).ConfigureAwait(false);
            if (connected.Link is not { } link) throw new IOException(connected.Error);
            await using var session = new RnshSession(link);
            if (!o.NoId) link.Identify(identity);
            using var terminal = new RnshTerminal();
            var command = new RnshExecute
            {
                Command = o.Command, PipeStdin = Console.IsInputRedirected,
                PipeStdout = Console.IsOutputRedirected, PipeStderr = Console.IsErrorRedirected,
                Terminal = Environment.GetEnvironmentVariable("TERM"),
                Window = terminal.Window(), TerminalFlags = terminal.Flags,
            };
            terminal.Raw();
            using var input = terminal.OpenInput();
            var result = await session.InitiateAsync(command, input, Console.OpenStandardOutput(),
                Console.OpenStandardError(), TimeSpan.FromSeconds(o.Timeout), stop, terminal.Interactive, terminal.Window).ConfigureAwait(false);
            return o.Mirror ? result : 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 130; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException
            or OverflowException or InvalidOperationException or System.Threading.Channels.ChannelClosedException or OperationCanceledException)
        {
            await error.WriteLineAsync($"rnsh: {ex.Message}");
            return ex is ArgumentException or FormatException or OverflowException ? 2 : 1;
        }
    }

    private static async Task<int> ListenAsync(ReticulumNode node, Identity identity, Destination destination, string state,
        Options o, TextWriter output, TextWriter error, CancellationToken stop)
    {
        var options = new RnshListenerOptions
        {
            AllowAll = o.NoAuth, AllowRemoteCommand = !o.NoCommand, RemoteCommandAsArguments = o.Append,
            DefaultCommand = o.Command.Length == 0 ? [OperatingSystem.IsWindows() ? "cmd.exe" : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh"] : o.Command,
            IsAllowed = hash => o.Allowed.Any(text => Convert.FromHexString(text).AsSpan().SequenceEqual(hash)) ||
                ReadAllowed(state, error).Any(value => value.AsSpan().SequenceEqual(hash)),
            Log = message => error.WriteLine(message),
        };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var sessions = new HashSet<Task>();
        var gate = new object();
        var local = new LocalDestination(identity, destination) { AcceptLinks = true };
        local.LinkEstablished = link =>
        {
            lock (gate)
            {
                if (sessions.Count >= 16 || lifetime.IsCancellationRequested) { link.Teardown(); return; }
                var session = new RnshSession(link);
                var task = Task.Run(async () =>
                {
                    await using (session) await session.ServeAsync(options, lifetime.Token).ConfigureAwait(false);
                }, CancellationToken.None);
                sessions.Add(task);
                _ = task.ContinueWith(done =>
                {
                    lock (gate) sessions.Remove(done);
                    if (done.IsFaulted) error.WriteLine($"rnsh session failed: {done.Exception!.GetBaseException().Message}");
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        };
        node.Router.RegisterLocalDestination(local);
        await node.StartAsync(stop).ConfigureAwait(false);
        await output.WriteLineAsync($"rnsh listening on <{Convert.ToHexStringLower(destination.Hash)}>");
        await output.FlushAsync().ConfigureAwait(false);
        try
        {
            if (o.Announce is not null) await node.Router.AnnounceAsync(local, cancellationToken: stop).ConfigureAwait(false);
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(o.Announce is > 0 ? TimeSpan.FromSeconds(o.Announce.Value) : Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false);
                if (o.Announce is > 0) await node.Router.AnnounceAsync(local, cancellationToken: stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            Task[] pending;
            lock (gate) pending = sessions.ToArray();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        return 0;
    }

    private static IEnumerable<byte[]> ReadAllowed(string state, TextWriter error)
    {
        var path = Path.Combine(state, "allowed_identities");
        if (!File.Exists(path)) return [];
        try { return File.ReadAllLines(path).Where(v => v.Length == 32 && CliSupport.TryParseHash(v, out _)).Select(Convert.FromHexString).ToArray(); }
        catch (IOException ex) { error.WriteLine($"rnsh: allowed identities reload failed: {ex.Message}"); return []; }
    }
    private static string ExpandHome(string value) => value == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : value.StartsWith("~/") || value.StartsWith("~\\") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]) : value;
    private static string StateDirectory(string? value)
    {
        if (value is not null) return ExpandHome(value);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Path.Combine(home, ".config", "rnsh");
        return Directory.Exists(config) ? config : Path.Combine(home, ".rnsh");
    }
}
