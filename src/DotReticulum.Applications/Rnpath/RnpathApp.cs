using System.Globalization;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rnpath;

public static class RnpathApp
{
    public const string Usage = """
        DotReticulum rnpath - Reticulum path management (experimental)

        Usage:
          rnpath [options] [destination-hash] [blackhole-list-filter]
          rnpath -h, --help
          rnpath --version

        Options:
          --config <dir>           Reticulum configuration directory
          -t, --table              Show known paths
          -m, --max <hops>         Filter table by maximum hops
          -r, --rates              Show announce rate information
          -d, --drop               Remove the path to a destination
          -D, --drop-announces     Drop queued announces
          -x, --drop-via           Drop paths via a transport identity
          -w <seconds>             Path-request timeout
          -R <hash>                Manage a remote transport instance
          -i <identity-file>       Identity for authenticated remote management
          -W <seconds>             Remote query timeout
          -b, --blackholed         List blackholed identities
          -B, --blackhole          Blackhole an identity
          -U, --unblackhole        Remove a blackhole
          --duration <hours>       Blackhole duration
          --reason <text>          Blackhole reason
          -p, --blackholed-list    Read a transport's published blackhole list
          -j, --json               Emit JSON for tables/lists
          -v, --verbose            Increase verbosity
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
            catch (FormatException ex) { error.WriteLine($"rnpath: {ex.Message}"); return 2; }
            catch (OverflowException ex) { error.WriteLine($"rnpath: {ex.Message}"); return 2; }
            catch (ArgumentException ex) when (ex.Message.StartsWith("Unknown option", StringComparison.Ordinal)
                || ex.Message.StartsWith("Option '", StringComparison.Ordinal)
                || ex.Message.StartsWith("Too many positional", StringComparison.Ordinal))
            {
                error.WriteLine($"rnpath: {ex.Message}");
                return 2;
            }
            if (options.Help) { output.WriteLine(Usage); return 0; }
            if (options.Version) { output.WriteLine("rnpath 1.5.7"); return 0; }
            if (!options.DropAnnounces && !options.Table && !options.Rates && options.Destination is null
                && !options.DropVia && !options.Blackholed)
            {
                output.WriteLine();
                output.WriteLine(Usage);
                output.WriteLine();
                return 0;
            }
            if (!double.IsFinite(options.Timeout) || options.Timeout < 0)
                throw new ArgumentException("Path timeout must be finite and nonnegative.");
            if (!double.IsFinite(options.RemoteTimeout) || options.RemoteTimeout <= 0)
                throw new ArgumentException("Remote timeout must be finite and positive.");
            if (options.MaxHops is < 0) throw new ArgumentException("Maximum hops must be nonnegative.");
            if (options.Duration is { } duration && (!double.IsFinite(duration) || duration < 0))
                throw new ArgumentException("Blackhole duration must be finite and nonnegative.");

            var loaded = RnsdApp.LoadConfiguration(options.Config);
            var config = loaded.Config with
            {
                SharedInstance = loaded.Config.SharedInstance with { Required = options.Remote is null },
            };
            await using var node = await ReticulumNode.CreateAsync(config, loaded.Directory,
                CliSupport.Logger(error, Math.Clamp(3 + options.Verbosity, 0, 8)), cancellationToken: stop).ConfigureAwait(false);
            await node.StartAsync(stop).ConfigureAwait(false);

            if (options.Remote is not null)
            {
                byte[] identityHash;
                try { identityHash = ParseHash(options.Remote); }
                catch (ArgumentException ex) { error.WriteLine($"rnpath: {ex.Message}"); return 20; }
                using var identity = LoadIdentity(options.IdentityFile);
                var destination = ManagementRemoteService.DestinationHash(identityHash);
                ManagementRemoteClient remote;
                try
                {
                    remote = await ManagementRemoteClient.ConnectAsync(node, identity, destination,
                        TimeSpan.FromSeconds(options.RemoteTimeout), stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
                {
                    error.WriteLine($"rnpath: {ex.Message}");
                    return 20;
                }
                using (remote)
                    return await RunRemoteAsync(remote, options, output, error, stop).ConfigureAwait(false);
            }

            return await RunLocalAsync(node, options, output, error, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or System.Net.Sockets.SocketException or TimeoutException)
        {
            error.WriteLine($"rnpath: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunLocalAsync(ReticulumNode node, Options options, TextWriter output,
        TextWriter error, CancellationToken token)
    {
        if (options.Blackholed)
        {
            var result = await node.CallManagementAsync(new Dictionary<object, object?> { ["get"] = "blackholed_identities" }, token).ConfigureAwait(false);
            if (result is not IReadOnlyList<KeyValuePair<object, object?>> entries || entries.Count == 0)
            {
                output.WriteLine("No blackholed identity data available");
                return 20;
            }
            WriteBlackholes(entries, options.Destination, output);
            return 0;
        }
        if (options.Blackhole || options.Unblackhole)
        {
            byte[] hash;
            try { hash = ParseHash(options.Destination ?? ""); }
            catch (ArgumentException ex) { error.WriteLine($"rnpath: Could not {(options.Blackhole ? "blackhole" : "unblackhole")} identity: {ex.Message}"); return 20; }
            var call = options.Blackhole
                ? new Dictionary<object, object?>
                {
                    ["blackhole_identity"] = hash,
                    ["until"] = options.Duration is { } hours && hours > 0
                        ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d + hours * 3600 : null,
                    ["reason"] = options.Reason,
                }
                : new Dictionary<object, object?> { ["unblackhole_identity"] = hash };
            var changed = await node.CallManagementAsync(call, token).ConfigureAwait(false);
            if (options.Blackhole)
            {
                if (changed is true) output.WriteLine($"Blackholed identity {options.Destination}");
                else if (changed is null) output.WriteLine($"Identity {options.Destination} already blackholed");
                else { output.WriteLine($"Could not blackhole identity {options.Destination}"); return 20; }
            }
            else
            {
                if (changed is true) output.WriteLine($"Lifted blackhole for identity {options.Destination}");
                else if (changed is null) output.WriteLine($"Identity {options.Destination} not blackholed");
                else { output.WriteLine($"Could not unblackhole identity {options.Destination}"); return 20; }
            }
            return 0;
        }
        if (options.RemoteBlackholeList) return await ReadBlackholeListAsync(node, options, output, error, token).ConfigureAwait(false);
        if (options.Table || options.Rates) return await ShowTableAsync(node, null, options, output, error, token).ConfigureAwait(false);
        if (options.DropAnnounces)
        {
            output.WriteLine("Dropping announce queues on all interfaces...");
            await node.CallManagementAsync(new Dictionary<object, object?> { ["drop"] = "announce_queues" }, token).ConfigureAwait(false);
            return 0;
        }
        if (options.Drop)
        {
            byte[] hash;
            try { hash = ParseHash(options.Destination ?? ""); }
            catch (ArgumentException ex) { error.WriteLine($"rnpath: {ex.Message}"); return 1; }
            var dropped = await node.CallManagementAsync(new Dictionary<object, object?> { ["drop"] = "path", ["destination_hash"] = hash }, token).ConfigureAwait(false);
            if (dropped is true) { output.WriteLine($"Dropped path to {Pretty(hash)}"); return 0; }
            output.WriteLine($"Unable to drop path to {Pretty(hash)}. Does it exist?");
            return 1;
        }
        if (options.DropVia)
        {
            byte[] hash;
            try { hash = ParseHash(options.Destination ?? ""); }
            catch (ArgumentException ex) { error.WriteLine($"rnpath: {ex.Message}"); return 1; }
            var dropped = await node.CallManagementAsync(new Dictionary<object, object?>
            {
                ["drop"] = "all_via", ["destination_hash"] = hash,
            }, token).ConfigureAwait(false);
            if (dropped is long count && count > 0) { output.WriteLine($"Dropped all paths via {Pretty(hash)}"); return 0; }
            output.WriteLine($"Unable to drop paths via {Pretty(hash)}. Does the transport instance exist?");
            return 1;
        }
        return await RequestPathAsync(node, options, output, error, token).ConfigureAwait(false);
    }

    private static async Task<int> RunRemoteAsync(ManagementRemoteClient remote, Options options, TextWriter output,
        TextWriter error, CancellationToken token)
    {
        if (options.Blackholed || options.Blackhole || options.Unblackhole)
        {
            output.WriteLine(options.Blackholed
                ? "Listing blackholed identities on remote instances not yet implemented"
                : "Blackholing identity on remote instances not yet implemented");
            return 255;
        }
        if (options.RemoteBlackholeList)
        {
            output.WriteLine("Remote blackhole-list requests are not available through remote management.");
            return 255;
        }
        if (options.DropAnnounces || options.Drop || options.DropVia)
        {
            output.WriteLine(options.DropAnnounces ? "Dropping announce queues on remote instances not yet implemented"
                : options.Drop ? "Dropping path on remote instances not yet implemented"
                : "Dropping all paths via specific transport instance on remote instances not implemented");
            return 255;
        }
        if (!options.Table && !options.Rates)
        {
            output.WriteLine("Requesting paths on remote instances not implemented");
            return 255;
        }
        try
        {
            byte[]? filter = null;
            if (options.Destination is not null) filter = ParseHash(options.Destination);
            var data = options.Table
                ? new object?[] { "table", filter, (long?)options.MaxHops }
                : new object?[] { "rates", filter };
            var response = await remote.RequestAsync("/path", data, token).ConfigureAwait(false);
            if (response is not object?[] rows)
            {
                error.WriteLine("rnpath: The remote request failed. Likely authentication failure.");
                return 10;
            }
            return RenderRows(rows.OfType<Dictionary<object, object?>>().ToArray(), options, output);
        }
        catch (ArgumentException ex) { error.WriteLine($"rnpath: {ex.Message}"); return 1; }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException)
        {
            error.WriteLine($"rnpath: {ex.Message}");
            return 10;
        }
    }

    private static async Task<int> ShowTableAsync(ReticulumNode node, ManagementRemoteClient? remote, Options options,
        TextWriter output, TextWriter error, CancellationToken token)
    {
        byte[]? filter = null;
        if (options.Destination is not null)
        {
            try { filter = ParseHash(options.Destination); }
            catch (ArgumentException ex) { error.WriteLine($"rnpath: {ex.Message}"); return 1; }
        }
        object? result = await node.CallManagementAsync(new Dictionary<object, object?>
        {
            ["get"] = options.Table ? "path_table" : "rate_table",
            ["max_hops"] = options.Table ? (long?)options.MaxHops : null,
        }, token).ConfigureAwait(false);
        if (result is not object?[] rows) throw new InvalidDataException("Management table response is not an array.");
        var selected = rows.OfType<Dictionary<object, object?>>()
            .Where(row => filter is null || row.GetValueOrDefault("hash") is byte[] hash && hash.AsSpan().SequenceEqual(filter))
            .ToArray();
        if (options.Rates) selected = selected.OrderBy(row => Numeric(row, "last")).ToArray();
        else selected = selected.OrderBy(row => Text(row, "interface"), StringComparer.Ordinal)
            .ThenBy(row => Numeric(row, "hops")).ToArray();
        if (selected.Length == 0 && filter is not null && !options.Json)
        {
            output.WriteLine(options.Rates ? "No information available" : "No path known");
            return 1;
        }
        return RenderRows(selected, options, output);
    }

    private static int RenderRows(Dictionary<object, object?>[] rows, Options options, TextWriter output)
    {
        if (options.Json)
        {
            output.WriteLine(WriteJson(rows.Select(row => (object?)row).ToArray()));
            return 0;
        }
        if (!options.Rates)
        {
            foreach (var row in rows)
            {
                var hash = Binary(row, "hash");
                var via = Binary(row, "via");
                var hops = Integer(row, "hops");
                var interfaceName = Text(row, "interface");
                var expires = Numeric(row, "expires");
                var stamp = DateTimeOffset.FromUnixTimeMilliseconds((long)(expires * 1000)).ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
                output.WriteLine($"{Pretty(hash)} is {hops} hop{(hops == 1 ? "" : "s")} away via {Pretty(via)} on {interfaceName} expires {stamp}");
            }
            return 0;
        }
        if (rows.Length == 0) { output.WriteLine("No information available"); return 0; }
        foreach (var row in rows)
        {
            var hash = Binary(row, "hash");
            var timestamps = row.GetValueOrDefault("timestamps") as object?[] ?? [];
            var count = timestamps.Length;
            var first = timestamps.Length > 0 ? MsgPack.ToDouble(timestamps[0]) : Numeric(row, "last");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var span = Math.Max(now - first, 3600);
            var perHour = Math.Round(count / (span / 3600d), 3);
            var lastAge = PrettyDate(Math.Max(0L, now - (long)Numeric(row, "last")));
            var violations = Integer(row, "rate_violations");
            var blockedUntil = Numeric(row, "blocked_until");
            var violationText = violations > 0 ? $", {violations} active rate violation{(violations == 1 ? "" : "s")}" : "";
            var blockedText = blockedUntil > now ? $", new announces allowed in {PrettyDate((long)(blockedUntil - now))}" : "";
            output.WriteLine($"{Pretty(hash)} last heard {lastAge} ago, {perHour.ToString("0.###", CultureInfo.InvariantCulture)} announces/hour in the last {PrettyDate(Math.Max(0, now - (long)first))}{violationText}{blockedText}");
        }
        return 0;
    }

    private static async Task<int> RequestPathAsync(ReticulumNode node, Options options, TextWriter output,
        TextWriter error, CancellationToken token)
    {
        byte[] hash;
        try { hash = ParseHash(options.Destination ?? ""); }
        catch (ArgumentException ex) { error.WriteLine($"rnpath: {ex.Message}"); return 1; }
        if (!node.Router.HasPath(hash))
        {
            output.Write($"Path to {Pretty(hash)} requested  ");
            await output.FlushAsync(token).ConfigureAwait(false);
        }
        var medium = await NumberAsync(node, "medium_path_timeout", null, token).ConfigureAwait(false);
        var timeout = Math.Max(options.Timeout, medium);
        if (!await node.WaitForPathAsync(hash, TimeSpan.FromSeconds(timeout), token).ConfigureAwait(false))
        {
            output.WriteLine("\rPath not found");
            return 1;
        }
        var hops = node.Router.HopsTo(hash);
        var viaValue = await node.CallManagementAsync(new Dictionary<object, object?>
        {
            ["get"] = "next_hop", ["destination_hash"] = hash,
        }, token).ConfigureAwait(false);
        if (viaValue is not byte[] via)
        {
            output.WriteLine("\rError: Invalid path data returned");
            return 1;
        }
        var interfaceValue = await node.CallManagementAsync(new Dictionary<object, object?>
        {
            ["get"] = "next_hop_if_name", ["destination_hash"] = hash,
        }, token).ConfigureAwait(false);
        output.WriteLine($"\rPath found, destination {Pretty(hash)} is {hops} hop{(hops == 1 ? "" : "s")} away via {Pretty(via)} on {interfaceValue ?? "None"}");
        return 0;
    }

    private static async Task<double> NumberAsync(ReticulumNode node, string path, byte[]? hash, CancellationToken token)
    {
        var call = new Dictionary<object, object?> { ["get"] = path };
        if (hash is not null) call["destination_hash"] = hash;
        var result = await node.CallManagementAsync(call, token).ConfigureAwait(false);
        return result is null ? 0 : MsgPack.ToDouble(result);
    }

    private static async Task<int> ReadBlackholeListAsync(ReticulumNode node, Options options, TextWriter output,
        TextWriter error, CancellationToken token)
    {
        byte[] identityHash;
        try { identityHash = ParseHash(options.Destination ?? ""); }
        catch (ArgumentException ex) { error.WriteLine(ex.Message); return 20; }
        var destination = Destination.ComputeHash(identityHash, "rnstransport", ["info", "blackhole"]);
        var connected = await ClientLinks.ConnectAsync(node, destination, TimeSpan.FromSeconds(options.RemoteTimeout), token).ConfigureAwait(false);
        if (connected.Link is null) { error.WriteLine(connected.Error ?? "The remote request failed."); return 10; }
        var link = connected.Link;
        try
        {
            if (!options.Json)
            {
                output.Write("Sending request... ");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            var receipt = link.Request("/list", timeout: options.RemoteTimeout, maxResponseSize: BlackholeWire.MaximumBytes + 32, cancellationToken: token);
            if (receipt is null) { error.WriteLine("The remote request failed."); return 10; }
            byte[] packed;
            try
            {
                await receipt.ResponseTask.WaitAsync(TimeSpan.FromSeconds(options.RemoteTimeout), token).ConfigureAwait(false);
                packed = BlackholeWire.ExtractResponse(receipt.RawResponse ?? throw new InvalidDataException("No response envelope."),
                    receipt.RequestId);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or FormatException)
            {
                error.WriteLine($"The remote request failed: {ex.Message}");
                return 10;
            }
            var records = BlackholeWire.Read(packed);
            if (records.Count == 0) { output.WriteLine("No blackholed identity data available"); return 20; }
            var values = records.Select(record => new KeyValuePair<object, object?>(record.Hash, new Dictionary<object, object?>
            {
                ["source"] = record.Source, ["until"] = record.Until, ["reason"] = record.Reason,
            })).ToArray();
            WriteBlackholes(values, options.ListFilter, output);
            return 0;
        }
        finally
        {
            link.Teardown();
        }
    }

    internal static void WriteBlackholes(IReadOnlyList<KeyValuePair<object, object?>> entries, string? filter,
        TextWriter output)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (key, value) in entries)
        {
            if (key is not byte[] hash || value is not Dictionary<object, object?> fields) continue;
            var until = fields.GetValueOrDefault("until") is { } expiration ? MsgPack.ToDouble(expiration) : 0;
            var reason = fields.GetValueOrDefault("reason") as string;
            var source = fields.GetValueOrDefault("source") as byte[] ?? [];
            var expiryText = until > 0 ? $"for {PrettyDate(Math.Max(0L, (long)(until - now)))}" : "indefinitely";
            var reasonText = string.IsNullOrEmpty(reason) ? "" : $" ({Truncate(reason, 63)})";
            var sourceText = source.Length == 16 ? $" by {Pretty(source)}" : "";
            var line = $"{Pretty(hash)} {expiryText}{reasonText}{sourceText}";
            if (filter is not null && !line.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            output.WriteLine($"{Pretty(hash)} blackholed {expiryText}{reasonText}{sourceText}");
        }
    }

    private static Identity LoadIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Remote management requires an identity file. Use -i to specify the path.");
        return IdentityFile.Load(CliSupport.ExpandHome(path));
    }

    internal static byte[] ParseHash(string value)
    {
        if (value.Length != 32)
            throw new ArgumentException("Destination length is invalid, must be 32 hexadecimal characters (16 bytes).");
        try { return Convert.FromHexString(value); }
        catch (FormatException) { throw new ArgumentException("Invalid destination entered. Check your input."); }
    }

    private static byte[] Binary(Dictionary<object, object?> map, string key) =>
        map.GetValueOrDefault(key) as byte[] ?? throw new InvalidDataException($"Path response field '{key}' is not a byte hash.");
    private static string Text(Dictionary<object, object?> map, string key) => map.GetValueOrDefault(key) as string ?? "None";
    private static long Integer(Dictionary<object, object?> map, string key) =>
        map.GetValueOrDefault(key) is { } value ? MsgPack.ToInt64(value) : 0;
    private static double Numeric(Dictionary<object, object?> map, string key) =>
        map.GetValueOrDefault(key) is { } value ? MsgPack.ToDouble(value) : 0;
    private static string Pretty(byte[] hash) => "<" + Convert.ToHexStringLower(hash) + ">";
    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
    private static string PrettyDate(long value)
    {
        if (value < 0) return "";
        var duration = TimeSpan.FromSeconds(value);
        if (value < 60) return $"{(int)value} seconds";
        if (value < 70) return "1 minute";
        if (value < 7200) return $"{(int)duration.TotalMinutes} minutes";
        if (value < 86400) return $"{(int)duration.TotalHours} hours";
        if (value < 172800) return "1 day";
        if (value < 2592000) return $"{(int)duration.TotalDays} days";
        return $"{(int)(duration.TotalDays / 30)} months";
    }

    internal static string WriteJson(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteValue(writer, value);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case byte[] bytes: writer.WriteStringValue(Convert.ToHexStringLower(bytes)); break;
            case string text: writer.WriteStringValue(text); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case byte n: writer.WriteNumberValue(n); break;
            case sbyte n: writer.WriteNumberValue(n); break;
            case short n: writer.WriteNumberValue(n); break;
            case ushort n: writer.WriteNumberValue(n); break;
            case int n: writer.WriteNumberValue(n); break;
            case uint n: writer.WriteNumberValue(n); break;
            case long n: writer.WriteNumberValue(n); break;
            case ulong n: writer.WriteNumberValue(n); break;
            case double n when double.IsFinite(n): writer.WriteNumberValue(n); break;
            case float n when float.IsFinite(n): writer.WriteNumberValue(n); break;
            case Dictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map) { writer.WritePropertyName(key); WriteValue(writer, item); }
                writer.WriteEndObject();
                break;
            case Dictionary<object, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map) { writer.WritePropertyName(Key(key)); WriteValue(writer, item); }
                writer.WriteEndObject();
                break;
            case IReadOnlyList<KeyValuePair<object, object?>> pairs:
                writer.WriteStartObject();
                foreach (var (key, item) in pairs) { writer.WritePropertyName(Key(key)); WriteValue(writer, item); }
                writer.WriteEndObject();
                break;
            case object?[] array:
                writer.WriteStartArray();
                foreach (var item in array) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default: throw new InvalidDataException($"Unsupported JSON value type '{value.GetType().Name}'.");
        }
    }
    private static string Key(object key) => key switch
    {
        byte[] bytes => Convert.ToHexStringLower(bytes),
        string text => text,
        _ => Convert.ToString(key, CultureInfo.InvariantCulture) ?? "",
    };

    internal sealed record Options(string? Config, bool Help, bool Version, bool Table, int? MaxHops, bool Rates,
        bool Drop, bool DropAnnounces, bool DropVia, double Timeout, string? Remote, string? IdentityFile,
        double RemoteTimeout, bool Blackholed, bool Blackhole, bool Unblackhole, double? Duration,
        string? Reason, bool RemoteBlackholeList, bool Json, int Verbosity, string? Destination, string? ListFilter);

    internal static Options Parse(string[] args)
    {
        string? config = null, remote = null, identity = null, reason = null, destination = null, listFilter = null;
        int? maxHops = null;
        double timeout = 15, remoteTimeout = 15;
        double? duration = null;
        var help = false; var version = false; var table = false; var rates = false; var drop = false;
        var dropAnnounces = false; var dropVia = false; var blackholed = false; var blackhole = false;
        var unblackhole = false; var remoteList = false; var json = false; var verbosity = 0;
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
                case "-t" or "--table": table = true; break;
                case "-m" or "--max": maxHops = int.Parse(Value(args[i]), CultureInfo.InvariantCulture); break;
                case "-r" or "--rates": rates = true; break;
                case "-d" or "--drop": drop = true; break;
                case "-D" or "--drop-announces": dropAnnounces = true; break;
                case "-x" or "--drop-via": dropVia = true; break;
                case "-w": timeout = double.Parse(Value(args[i]), CultureInfo.InvariantCulture); break;
                case "-R": remote = Value(args[i]); break;
                case "-i": identity = Value(args[i]); break;
                case "-W": remoteTimeout = double.Parse(Value(args[i]), CultureInfo.InvariantCulture); break;
                case "-b" or "--blackholed": blackholed = true; break;
                case "-B" or "--blackhole": blackhole = true; break;
                case "-U" or "--unblackhole": unblackhole = true; break;
                case "--duration": duration = double.Parse(Value(args[i]), CultureInfo.InvariantCulture); break;
                case "--reason": reason = Value(args[i]); break;
                case "-p" or "--blackholed-list": remoteList = true; break;
                case "-j" or "--json": json = true; break;
                case "-v" or "--verbose": verbosity++; break;
                default:
                    if (args[i].Length > 1 && args[i][0] == '-' && args[i][1..].All(c => c == 'v'))
                    {
                        verbosity += args[i].Length - 1;
                        break;
                    }
                    if (args[i].StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option '{args[i]}'.");
                    if (destination is null) destination = args[i];
                    else if (listFilter is null) listFilter = args[i];
                    else throw new ArgumentException("Too many positional arguments.");
                    break;
            }
        }
        return new Options(config, help, version, table, maxHops, rates, drop, dropAnnounces, dropVia, timeout,
            remote, identity, remoteTimeout, blackholed, blackhole, unblackhole, duration, reason, remoteList, json,
            verbosity, destination, listFilter) with { Rates = rates && !table };
    }
}
