using System.Globalization;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rncp;

/// <summary>Command line front end of <c>rncp</c> (options mirror upstream <c>rncp</c>).</summary>
public static class RncpApp
{
    public const string Usage = """
        DotReticulum rncp - Reticulum file transfer utility (experimental, wire-compatible with upstream rncp)

        Usage:
          rncp <file> <destination>        Send a file to a listener
          rncp -f <file> <destination>     Fetch a file from a listener that allows it
          rncp -l [-s dir] [-a hash ...]   Listen for incoming transfers
          rncp -p                          Print this identity and listening destination, then exit

        Options:
          --config <path>      Reticulum config directory or file (default ~/.dotreticulum)
          -l, --listen         Listen for incoming transfer requests
          -f, --fetch          Fetch <file> from the remote <destination> instead of sending
          -F, --allow-fetch    Listener: allow authenticated clients to fetch files
          -j, --jail <path>    Listener: restrict fetch requests to this directory
          -s, --save <path>    Save received files in this directory (must exist)
          -O, --overwrite      Overwrite existing received files instead of adding a postfix
          -a <hash>            Allow this identity hash (repeatable; also read from ~/.rncp/allowed_identities)
          -n, --no-auth        Accept requests from anyone
          -b <seconds>         Listener: announce interval, 0 to announce only at startup (default: no announce)
          -C, --no-compress    Disable automatic compression
          -P, --phy-rates      Display physical layer transfer rate
          -i <identity>        Path to the identity file to use (created when missing)
          -w <seconds>         Sender timeout before giving up (default 15)
          -S, --silent         Do not print transfer progress
          -v / -q              Increase / decrease logging verbosity
          -p, --print-identity Print identity and destination and exit
          -h, --help           Show this help
          --version            Show implementation and pinned reference versions
        """;

    private sealed class Options
    {
        public string? Config;
        public string? Identity;
        public string? Save;
        public string? Jail;
        public bool Listen, Fetch, AllowFetch, Overwrite, NoAuth, NoCompress, Silent, PrintIdentity, PhyRates;
        public int Verbosity;
        public int Announce = -1;
        public double Timeout = 15;
        public List<string> Allowed = [];
        public List<string> Positional = [];
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        args = TransferArguments.Expand(args, "hlfFOnCPSpvq", "cjsaibw",
            new HashSet<string> { "--config", "--jail", "--save" }, out var attachedValues);
        var o = new Options();
        var positionalOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            string NextValue(bool numeric = false) => TransferArguments.Next(args, ref i, attachedValues, numeric);
            if (positionalOnly) { o.Positional.Add(args[i]); continue; }

            try
            {
                switch (args[i])
                {
                    case "-h" or "--help" or "-?": output.WriteLine(Usage); return 0;
                    case "--": positionalOnly = true; break;
                    case "--version":
                        output.WriteLine("rncp DotReticulum (experimental; reference RNS 1.5.7)"); return 0;
                    case "--config" or "-c": o.Config = NextValue(); break;
                    case "-l" or "--listen": o.Listen = true; break;
                    case "-f" or "--fetch": o.Fetch = true; break;
                    case "-F" or "--allow-fetch": o.AllowFetch = true; break;
                    case "-j" or "--jail": o.Jail = NextValue(); break;
                    case "-s" or "--save": o.Save = NextValue(); break;
                    case "-O" or "--overwrite": o.Overwrite = true; break;
                    case "-a": o.Allowed.Add(NextValue()); break;
                    case "-n" or "--no-auth": o.NoAuth = true; break;
                    case "-C" or "--no-compress": o.NoCompress = true; break;
                    case "-P" or "--phy-rates": o.PhyRates = true; break;
                    case "-S" or "--silent": o.Silent = true; break;
                    case "-p" or "--print-identity": o.PrintIdentity = true; break;
                    case "-i": o.Identity = NextValue(); break;
                    case "-b": o.Announce = int.Parse(NextValue(true), CultureInfo.InvariantCulture); break;
                    case "-w": o.Timeout = double.Parse(NextValue(true), CultureInfo.InvariantCulture); break;
                    case "-v" or "--verbose": o.Verbosity++; break;
                    case "-vv": o.Verbosity += 2; break;
                    case "-q" or "--quiet": o.Verbosity--; break;
                    default:
                        if (args[i].StartsWith("--config=", StringComparison.Ordinal)) { o.Config = args[i][9..]; break; }
                        if (args[i].Length > 2 && args[i][0] == '-' && args[i][1..].All(c => c is 'v' or 'q'))
                        { o.Verbosity += args[i][1..].Sum(c => c == 'v' ? 1 : -1); break; }
                        if (args[i].StartsWith('-') && args[i].Length > 1) throw new ArgumentException($"unknown argument '{args[i]}'.");
                        o.Positional.Add(args[i]);
                        break;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                error.WriteLine($"rncp: {ex.Message}");
                return 2;
            }
        }

        try
        {
            if (!double.IsFinite(o.Timeout)) { error.WriteLine("rncp: timeout must be finite."); return 2; }
            if (o.Listen || o.PrintIdentity) return await ListenAsync(o, output, error, stop).ConfigureAwait(false);
            if (o.Positional.Count == 2) return await TransferAsync(o, output, error, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or InvalidDataException)
        {
            error.WriteLine($"rncp: {ex.Message}");
            return 1;
        }

        output.WriteLine();
        output.WriteLine(Usage);
        return o.Positional.Count <= 2 ? 0 : 2;
    }

    private static async Task<int> ListenAsync(Options o, TextWriter output, TextWriter error, CancellationToken stop)
    {
        var allowed = new List<byte[]>();
        if (!o.NoAuth && !o.PrintIdentity)
        {
            foreach (var text in o.Allowed)
            {
                if (!CliSupport.TryParseHash(text, out var hash))
                {
                    error.WriteLine($"rncp: Allowed destination length is invalid, must be {CliSupport.HashHexLength} hexadecimal characters ({Identity.HashSize} bytes).");
                    return 1;
                }

                allowed.Add(hash);
            }

            allowed.AddRange(ReadAllowedFiles(CliSupport.DefaultAllowedFiles("rncp")));
        }

        string? saveDir = null;
        if (o.Save is not null)
        {
            saveDir = Path.GetFullPath(CliSupport.ExpandHome(o.Save));
            var status = CheckOutputDirectory(saveDir, error);
            if (status != 0) return status;
        }

        var log = CliSupport.Logger(output, 3 + o.Verbosity);
        await using var node = ReticulumNode.Open(o.Config, log);
        var identity = CliSupport.LoadOrCreateIdentity(node, o.Identity, "rncp");
        var listener = new RncpListener(node.Router, identity, new RncpListenerOptions
        {
            AllowedIdentities = allowed,
            AllowAll = o.NoAuth,
            AllowFetch = o.AllowFetch,
            FetchJail = o.Jail is null ? null : CliSupport.ExpandHome(o.Jail),
            SaveDirectory = saveDir,
            AllowOverwrite = o.Overwrite,
            AutoCompress = !o.NoCompress,
        });
        if (o.PrintIdentity)
        {
            output.WriteLine("Identity     : <" + identity.HexHash + ">");
            output.WriteLine("Listening on : <" + listener.DestinationHashHex + ">");
            return 0;
        }

        listener.Log = message => log(5, message);
        listener.FileReceived = path => log(3, "Received " + path);
        await node.StartAsync(stop).ConfigureAwait(false);
        if (allowed.Count < 1 && !o.NoAuth) log(2, "No allowed identities configured, rncp will not accept any files!");
        if (o.NoAuth && o.AllowFetch) log(2, "Allowing unauthenticated fetch requests");
        log(4, "rncp listening on <" + listener.DestinationHashHex + ">");
        output.Flush();

        if (o.Announce >= 0)
        {
            await listener.AnnounceAsync(stop).ConfigureAwait(false);
            while (o.Announce > 0 && !stop.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(o.Announce), stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                await listener.AnnounceAsync(stop).ConfigureAwait(false);
            }
        }

        try { await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        return 0;
    }

    private static async Task<int> TransferAsync(Options o, TextWriter output, TextWriter error, CancellationToken stop)
    {
        var (file, destination) = (o.Positional[0], o.Positional[1]);
        if (!o.Fetch) file = CliSupport.ExpandHome(file);
        if (!CliSupport.TryParseHash(destination, out var destinationHash))
        {
            error.WriteLine($"rncp: Allowed destination length is invalid, must be {CliSupport.HashHexLength} hexadecimal characters ({Identity.HashSize} bytes).");
            return 1;
        }

        string? saveDir = null;
        if (o.Fetch)
        {
            saveDir = o.Save is null ? Directory.GetCurrentDirectory() : Path.GetFullPath(CliSupport.ExpandHome(o.Save));
            var status = CheckOutputDirectory(saveDir, error);
            if (status != 0) return status;
        }
        else if (!File.Exists(file))
        {
            output.WriteLine("File not found");
            return 1;
        }

        var log = CliSupport.Logger(output, 3 + o.Verbosity);
        await using var node = ReticulumNode.Open(o.Config, log);
        var identity = CliSupport.LoadOrCreateIdentity(node, o.Identity, "rncp");
        await node.StartAsync(stop).ConfigureAwait(false);
        var timeout = TimeSpan.FromSeconds(o.Timeout);
        var lastPercent = -1;
        void Progress(double fraction)
        {
            var percent = (int)(fraction * 100);
            if (o.Silent || percent / 10 == lastPercent / 10) return;
            lastPercent = percent;
            output.WriteLine($"Transferring file {percent}%");
        }

        output.WriteLine((o.Fetch ? "Fetching " : "Sending ") + file + (o.Fetch ? " from " : " to ") + CliSupport.PrettyHex(destinationHash));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = o.Fetch
            ? await RncpClient.FetchAsync(node, identity, destinationHash, file, saveDir!, timeout, o.Overwrite, Progress, stop).ConfigureAwait(false)
            : await RncpClient.SendAsync(node, identity, destinationHash, file, timeout, !o.NoCompress, Progress, stop).ConfigureAwait(false);
        output.WriteLine(result.Message);
        if (o.PhyRates && !o.Silent && result.Success)
            output.WriteLine("Physical layer rate: " + CliSupport.SizeString(result.PhysicalBytes / Math.Max(started.Elapsed.TotalSeconds, 0.001), "b") + "ps");
        // Upstream exits 0 after reporting a refused or missing fetch; mirror that.
        return result.Success || result.Message.StartsWith("Fetch request failed", StringComparison.Ordinal) ? 0 : 1;
    }

    internal static List<byte[]> ReadAllowedFile(string file)
    {
        var result = new List<byte[]>();
        foreach (var line in File.ReadLines(file))
        {
            var text = line.Replace("\r", "", StringComparison.Ordinal);
            if (text.Length == CliSupport.HashHexLength) result.Add(Convert.FromHexString(text));
        }
        return result;
    }

    internal static List<byte[]> ReadAllowedFiles(IEnumerable<string> candidates) =>
        candidates.FirstOrDefault(File.Exists) is { } file ? ReadAllowedFile(file) : [];

    internal static int CheckOutputDirectory(string path, TextWriter error)
    {
        if (!Directory.Exists(path)) { error.WriteLine("rncp: Output directory not found"); return 3; }
        var probe = Path.Combine(path, ".rncp-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { error.WriteLine("rncp: Output directory not writable"); return 4; }
    }
}
