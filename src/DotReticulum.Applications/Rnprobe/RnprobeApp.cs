using System.Globalization;
using System.Security.Cryptography;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnprobe;

public static class RnprobeApp
{
    public const int DefaultSize = 16;
    public const double DefaultTimeoutSeconds = 12;
    public const string Usage = """
        DotReticulum rnprobe - probe a Reticulum destination (experimental)

        Usage:
          rnprobe [--config <dir>] [-s <bytes>] [-n <count>] [-t <seconds>] [-w <seconds>] [-v] <full-name> <destination-hash>
          rnprobe -h, --help
          rnprobe --version

        Options:
          --config <dir>       Alternative Reticulum configuration directory
          -s, --size <bytes>   Probe payload size (default: 16)
          -n, --probes <count> Number of probes (default: 1)
          -t, --timeout <sec>  Timeout for path discovery and each probe (0 uses the default)
          -w, --wait <sec>     Delay between probes (default: 0)
          -v, --verbose        Show next-hop information
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            Options options;
            try { options = Parse(args); }
            catch (FormatException ex) { error.WriteLine($"rnprobe: {ex.Message}"); return 2; }
            catch (OverflowException ex) { error.WriteLine($"rnprobe: {ex.Message}"); return 2; }
            catch (ArgumentException ex) when (ex.Message.StartsWith("Unknown option", StringComparison.Ordinal)
                || ex.Message.StartsWith("Option '", StringComparison.Ordinal)
                || ex.Message.StartsWith("Too many positional", StringComparison.Ordinal))
            {
                error.WriteLine($"rnprobe: {ex.Message}");
                return 2;
            }
            if (options.Help) { output.WriteLine(Usage); return 0; }
            if (options.Version) { output.WriteLine("rnprobe 1.5.7"); return 0; }
            if (options.Hash is null)
            {
                output.WriteLine();
                output.WriteLine(Usage);
                output.WriteLine();
                return 0;
            }
            if (options.Name is null)
            {
                output.WriteLine("The full destination name including application name aspects must be specified for the destination");
                return 0;
            }

            var destinationHash = ParseHash(options.Hash);
            var (app, aspects) = Destination.SplitFullName(options.Name);
            _ = Destination.Create(DestinationType.Single, app, aspects, new byte[Destination.HashLength]);
            if (options.Size < 0) throw new ArgumentException("Probe size must be nonnegative.");
            if (options.Probes < 1) throw new ArgumentException("Probe count must be positive.");
            if (!double.IsFinite(options.Wait) || options.Wait < 0) throw new ArgumentException("Wait interval must be a finite, nonnegative number.");
            if (options.Timeout is { } supplied && !double.IsFinite(supplied))
                throw new ArgumentException("Timeout must be a finite number.");

            var loaded = RnsdApp.LoadConfiguration(options.Config);
            var config = loaded.Config with { SharedInstance = loaded.Config.SharedInstance with { Required = false } };
            var logLevel = Math.Clamp(2 + options.Verbosity, 0, 8);
            await using var node = await ReticulumNode.CreateAsync(config, loaded.Directory,
                CliSupport.Logger(error, logLevel), cancellationToken: stop).ConfigureAwait(false);
            await node.StartAsync(stop).ConfigureAwait(false);

            var pathTimeout = options.Timeout is { } requestedTimeout && requestedTimeout != 0
                ? requestedTimeout : await ProbeTimeoutAsync(node, destinationHash, stop).ConfigureAwait(false);
            if (!await WaitForPathAsync(node, destinationHash, pathTimeout, output, stop).ConfigureAwait(false))
            {
                output.WriteLine();
                output.WriteLine("Path request timed out");
                return 1;
            }

            using var destinationIdentity = node.Router.Engine.Known.Recall(destinationHash);
            if (destinationIdentity is null)
            {
                error.WriteLine($"rnprobe: no announced identity is known for {PrettyHash(destinationHash)}");
                return 1;
            }

            var destination = Destination.Create(DestinationType.Single, app, aspects, destinationIdentity);
            var sent = 0;
            var replies = 0;
            for (var index = 0; index < options.Probes; index++)
            {
                stop.ThrowIfCancellationRequested();
                if (index != 0 && options.Wait > 0)
                    await Task.Delay(TimeSpan.FromSeconds(options.Wait), stop).ConfigureAwait(false);

                Packet packet;
                try
                {
                    var encrypted = destinationIdentity.Encrypt(RandomNumberGenerator.GetBytes(options.Size));
                    packet = Packet.Create(PacketType.Data, DestinationType.Single, destination.Hash, encrypted);
                }
                catch (ArgumentOutOfRangeException)
                {
                    error.WriteLine($"rnprobe: probe packet exceeds the {Packet.Mtu}-byte MTU");
                    return 3;
                }
                catch (ArgumentException ex) when (ex.Message.Contains("MTU", StringComparison.OrdinalIgnoreCase))
                {
                    error.WriteLine($"rnprobe: probe packet exceeds the {Packet.Mtu}-byte MTU");
                    return 3;
                }

                PacketReceipt? receipt;
                try
                {
                    receipt = await node.Router.SendWithReceiptAsync(packet, destinationIdentity,
                        TimeSpan.FromSeconds(pathTimeout), stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
                {
                    error.WriteLine($"rnprobe: could not send probe: {ex.Message}");
                    return 1;
                }

                sent++;
                var hops = node.Router.HopsTo(destinationHash);
                var route = options.Verbosity > 0
                    ? $" via {PrettyHash(node.Router.Engine.GetNextHop(destinationHash) ?? destinationHash)}"
                      + (await NextHopInterfaceAsync(node, destinationHash, stop).ConfigureAwait(false) is { } name
                          && name.Length > 0 && name != "None" ? $" on {name}" : "")
                    : "";
                output.WriteLine($"Sent probe {sent} ({options.Size} bytes) to {PrettyHash(destinationHash)}{route}");
                if (receipt is null) { output.WriteLine("Probe timed out"); continue; }

                bool delivered;
                try { delivered = await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(pathTimeout), stop).ConfigureAwait(false); }
                catch (TimeoutException) { delivered = false; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }

                if (!delivered || receipt.Status != PacketReceiptStatus.Delivered)
                {
                    output.WriteLine("Probe timed out");
                    continue;
                }

                replies++;
                var rtt = receipt.Rtt ?? 0;
                var rttText = rtt >= 1
                    ? $"{Math.Round(rtt, 3).ToString("0.###", CultureInfo.InvariantCulture)} seconds"
                    : $"{Math.Round(rtt * 1000, 3).ToString("0.###", CultureInfo.InvariantCulture)} milliseconds";
                var hopCount = hops ?? 0;
                output.WriteLine($"Valid reply from {PrettyHash(destination.Hash)}");
                output.WriteLine($"Round-trip time is {rttText} over {hopCount} hop{(hopCount == 1 ? "" : "s")}");
                output.WriteLine();
            }

            var loss = Math.Round((1d - (double)replies / sent) * 100d, 2);
            output.WriteLine($"Sent {sent}, received {replies}, packet loss {loss.ToString("0.##", CultureInfo.InvariantCulture)}%");
            return replies == sent ? 0 : 2;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            error.WriteLine($"rnprobe: {ex.Message}");
            return 1;
        }
    }

    internal static byte[] ParseHash(string value)
    {
        if (value.Length != Destination.HashLength * 2)
            throw new ArgumentException($"Destination length is invalid, must be {Destination.HashLength * 2} hexadecimal characters ({Destination.HashLength} bytes).");
        try { return Convert.FromHexString(value); }
        catch (FormatException) { throw new ArgumentException("Invalid destination entered. Check your input."); }
    }

    private static async Task<bool> WaitForPathAsync(ReticulumNode node, byte[] destinationHash, double timeout,
        TextWriter output, CancellationToken token)
    {
        if (node.Router.HasPath(destinationHash)) return true;
        await node.Router.RequestPathAsync(destinationHash, cancellationToken: token).ConfigureAwait(false);
        output.Write($"Path to {PrettyHash(destinationHash)} requested  ");
        await output.FlushAsync(token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            while (!node.Router.HasPath(destinationHash))
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private static async Task<double> ProbeTimeoutAsync(ReticulumNode node, byte[] destinationHash, CancellationToken token)
    {
        var firstHop = await NumberManagementAsync(node,
            new Dictionary<object, object?> { ["get"] = "first_hop_timeout", ["destination_hash"] = destinationHash }, token).ConfigureAwait(false);
        var medium = await NumberManagementAsync(node,
            new Dictionary<object, object?> { ["get"] = "medium_path_timeout" }, token).ConfigureAwait(false);
        return Math.Max(DefaultTimeoutSeconds + firstHop, medium);
    }

    private static async Task<double> NumberManagementAsync(ReticulumNode node, Dictionary<object, object?> request, CancellationToken token)
    {
        var result = await node.CallManagementAsync(request, token).ConfigureAwait(false);
        return result is null ? 0 : MsgPack.ToDouble(result);
    }

    private static async Task<string?> NextHopInterfaceAsync(ReticulumNode node, byte[] destinationHash, CancellationToken token)
    {
        var result = await node.CallManagementAsync(new Dictionary<object, object?>
        {
            ["get"] = "next_hop_if_name", ["destination_hash"] = destinationHash,
        }, token).ConfigureAwait(false);
        return result as string;
    }

    private static string PrettyHash(byte[] hash) => "<" + Convert.ToHexStringLower(hash) + ">";

    internal sealed record Options(string? Config, int Size, int Probes, double? Timeout, double Wait,
        int Verbosity, string? Name, string? Hash, bool Help, bool Version);

    internal static Options Parse(string[] args)
    {
        string? config = null, name = null, hash = null;
        var size = DefaultSize;
        var probes = 1;
        double? timeout = null;
        var wait = 0d;
        var verbosity = 0;
        var help = false;
        var version = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value(string option)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Option '{option}' requires a value.");
                return args[++i];
            }
            switch (args[i])
            {
                case "--help" or "-h" or "-?": help = true; break;
                case "--version": version = true; break;
                case "--config": config = Value(args[i]); break;
                case "-s" or "--size": size = int.Parse(Value(args[i]), CultureInfo.InvariantCulture); break;
                case "-n" or "--probes": probes = int.Parse(Value(args[i]), CultureInfo.InvariantCulture); break;
                case "-t" or "--timeout": timeout = double.Parse(Value(args[i]), CultureInfo.InvariantCulture); break;
                case "-w" or "--wait": wait = double.Parse(Value(args[i]), CultureInfo.InvariantCulture); break;
                case "-v" or "--verbose": verbosity++; break;
                default:
                    if (args[i].Length > 1 && args[i][0] == '-' && args[i][1..].All(c => c == 'v'))
                    {
                        verbosity += args[i].Length - 1;
                        break;
                    }
                    if (args[i].StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option '{args[i]}'.");
                    if (name is null) name = args[i];
                    else if (hash is null) hash = args[i];
                    else throw new ArgumentException("Too many positional arguments.");
                    break;
            }
        }
        return new Options(config, size, probes, timeout, wait, verbosity, name, hash, help, version);
    }
}
