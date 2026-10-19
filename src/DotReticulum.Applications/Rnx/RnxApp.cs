using System.Globalization;
using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rnx;

/// <summary>Command line front end of <c>rnx</c> (options mirror upstream <c>rnx</c>).</summary>
public static class RnxApp
{
    public const string Usage = """
        DotReticulum rnx - Reticulum remote command execution (experimental, wire-compatible with upstream rnx)

        Usage:
          rnx -l [-a hash ...]                 Listen for commands
          rnx <destination> <command>          Run a command on a remote listener
          rnx <destination> -x                 Interactive remote shell loop
          rnx -p                               Print this identity and listening destination, then exit

        Options:
          --config <path>      Reticulum config directory or file (default ~/.dotreticulum)
          -l, --listen         Listen for incoming commands
          -p, --print-identity Print identity and destination and exit
          -a <hash>            Allow this identity hash (repeatable; also read from ~/.rnx/allowed_identities)
          -n, --noauth         Listener: accept commands from anyone (dangerous)
          -N, --noid           Client: do not identify to the listener
          -b, --no-announce    Listener: do not announce at startup
          -i <identity>        Path to the identity file to use
          -w <seconds>         Connect and execution timeout (default 15)
          -W <seconds>         Max result download time
          --stdin <text>       Pass this text on the remote command's stdin
          --stdout <bytes>     Max size of returned stdout
          --stderr <bytes>     Max size of returned stderr
          -m                   Mirror the remote exit code
          -d, --detailed       Show detailed result output
          -x, --interactive    Interactive mode
          -v / -q              Increase / decrease logging verbosity
          -h, --help           Show this help
        """;

    private sealed class Options
    {
        public string? Config, Identity, Stdin;
        public bool Listen, PrintIdentity, NoAuth, NoId, NoAnnounce, Mirror, Detailed, Interactive;
        public int Verbosity;
        public double Timeout = 15;
        public double? ResultTimeout;
        public long? StdoutLimit, StderrLimit;
        public List<string> Allowed = [];
        public List<string> Positional = [];
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string NextValue()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} requires a value.");
                return args[++i];
            }

            try
            {
                switch (args[i])
                {
                    case "-h" or "--help" or "-?": output.WriteLine(Usage); return 0;
                    case "--config" or "-c": o.Config = NextValue(); break;
                    case "-l" or "--listen": o.Listen = true; break;
                    case "-p" or "--print-identity": o.PrintIdentity = true; break;
                    case "-a": o.Allowed.Add(NextValue()); break;
                    case "-n" or "--noauth": o.NoAuth = true; break;
                    case "-N" or "--noid": o.NoId = true; break;
                    case "-b" or "--no-announce": o.NoAnnounce = true; break;
                    case "-i": o.Identity = NextValue(); break;
                    case "-w": o.Timeout = double.Parse(NextValue(), CultureInfo.InvariantCulture); break;
                    case "-W": o.ResultTimeout = double.Parse(NextValue(), CultureInfo.InvariantCulture); break;
                    case "--stdin": o.Stdin = NextValue(); break;
                    case "--stdout": o.StdoutLimit = long.Parse(NextValue(), CultureInfo.InvariantCulture); break;
                    case "--stderr": o.StderrLimit = long.Parse(NextValue(), CultureInfo.InvariantCulture); break;
                    case "-m": o.Mirror = true; break;
                    case "-d" or "--detailed": o.Detailed = true; break;
                    case "-x" or "--interactive": o.Interactive = true; break;
                    case "-v" or "--verbose": o.Verbosity++; break;
                    case "-q" or "--quiet": o.Verbosity--; break;
                    default:
                        // After the destination everything is the command, including dashes.
                        if (o.Positional.Count >= 1 && !o.Listen) { o.Positional.Add(args[i]); break; }
                        if (args[i].StartsWith('-') && args[i].Length > 1) throw new ArgumentException($"unknown argument '{args[i]}'.");
                        o.Positional.Add(args[i]);
                        break;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                error.WriteLine($"rnx: {ex.Message}");
                return 2;
            }
        }

        try
        {
            if (o.Listen || o.PrintIdentity) return await ListenAsync(o, output, error, stop).ConfigureAwait(false);
            if (o.Positional.Count >= 1 && (o.Interactive || o.Positional.Count >= 2)) return await ClientAsync(o, output, error, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            error.WriteLine($"rnx: {ex.Message}");
            return 1;
        }

        output.WriteLine(Usage);
        return 2;
    }

    private static async Task<int> ListenAsync(Options o, TextWriter output, TextWriter error, CancellationToken stop)
    {
        var allowed = new List<byte[]>();
        if (!o.NoAuth)
        {
            foreach (var text in o.Allowed)
            {
                if (!CliSupport.TryParseHash(text, out var hash))
                {
                    error.WriteLine($"rnx: Allowed destination length is invalid, must be {CliSupport.HashHexLength} hexadecimal characters ({Identity.HashSize} bytes).");
                    return 1;
                }

                allowed.Add(hash);
            }

            foreach (var file in CliSupport.DefaultAllowedFiles("rnx").Where(File.Exists)) allowed.AddRange(CliSupport.ReadAllowedFile(file));
        }

        var log = CliSupport.Logger(output, 3 + o.Verbosity);
        await using var node = ReticulumNode.Open(o.Config, log);
        var identity = CliSupport.LoadOrCreateIdentity(node, o.Identity, "rnx");
        var listener = new RnxListener(node.Router, identity, new RnxListenerOptions { AllowedIdentities = allowed, AllowAll = o.NoAuth });
        if (o.PrintIdentity)
        {
            output.WriteLine("Identity     : <" + identity.HexHash + ">");
            output.WriteLine("Listening on : <" + listener.DestinationHashHex + ">");
            return 0;
        }

        listener.Log = message => log(5, message);
        await node.StartAsync(stop).ConfigureAwait(false);
        if (allowed.Count < 1 && !o.NoAuth) log(2, "No allowed identities configured, rnx will not accept any commands!");
        log(4, "rnx listening on <" + listener.DestinationHashHex + ">");
        output.Flush();
        if (!o.NoAnnounce) await listener.AnnounceAsync(stop).ConfigureAwait(false);
        try { await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        return 0;
    }

    private static async Task<int> ClientAsync(Options o, TextWriter output, TextWriter error, CancellationToken stop)
    {
        if (!CliSupport.TryParseHash(o.Positional[0], out var destinationHash))
        {
            error.WriteLine($"rnx: Allowed destination length is invalid, must be {CliSupport.HashHexLength} hexadecimal characters ({Identity.HashSize} bytes).");
            return 241;
        }

        var log = CliSupport.Logger(output, 3 + o.Verbosity);
        await using var node = ReticulumNode.Open(o.Config, log);
        var identity = CliSupport.LoadOrCreateIdentity(node, o.Identity, "rnx");
        await node.StartAsync(stop).ConfigureAwait(false);
        var client = new RnxClient(node, identity, destinationHash, !o.NoId);
        var timeout = TimeSpan.FromSeconds(o.Timeout);
        TimeSpan? resultTimeout = o.ResultTimeout is { } w ? TimeSpan.FromSeconds(w) : null;
        var stdin = o.Stdin is null ? null : Encoding.UTF8.GetBytes(o.Stdin);

        async Task<int> RunOne(string command, byte[]? input)
        {
            var result = await client.ExecuteAsync(command, timeout, o.StdoutLimit, o.StderrLimit, input, resultTimeout, stop).ConfigureAwait(false);
            if (!result.Ok)
            {
                error.WriteLine(result.Error);
                return result.ErrorCode;
            }

            if (o.Detailed)
            {
                output.WriteLine("Transfer complete, received result:");
                output.WriteLine($"  Command executed: {result.Executed}; return code: {result.ReturnCode}");
                output.WriteLine($"  Remote stdout: {result.StdoutLength} bytes, stderr: {result.StderrLength} bytes");
                output.WriteLine("--- Remote output follows ---");
            }

            output.Write(Encoding.UTF8.GetString(result.Stdout));
            error.Write(Encoding.UTF8.GetString(result.Stderr));
            if (o.Detailed) output.WriteLine("--- End of remote output, rnx done ---");
            output.Flush();
            return result.ReturnCode is null ? 240 : (int)result.ReturnCode.Value;
        }

        try
        {
            if (!o.Interactive)
            {
                var code = await RunOne(string.Join(' ', o.Positional.Skip(1)), stdin).ConfigureAwait(false);
                return o.Mirror ? code : (code is >= 240 and <= 249 ? code : 0);
            }

            var last = 0;
            while (!stop.IsCancellationRequested)
            {
                output.Write("> ");
                output.Flush();
                var line = Console.In.ReadLine();
                if (line is null || line is "exit" or "quit") break;
                if (line.Length == 0) continue;
                last = await RunOne(line, null).ConfigureAwait(false);
            }

            return o.Mirror ? last : 0;
        }
        finally
        {
            await client.CloseAsync().ConfigureAwait(false);
        }
    }
}
