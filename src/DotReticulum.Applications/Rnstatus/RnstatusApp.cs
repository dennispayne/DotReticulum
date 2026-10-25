using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rnstatus;

public static class RnstatusApp
{
    public const string Usage = """
        DotReticulum rnstatus - Reticulum network status (experimental)

        Usage:
          rnstatus [options] [interface-filter]
          rnstatus -h, --help

        Options:
          --config <dir>          Reticulum configuration directory
          --version               Show version
          --attach <name>         Attach an interface by name
          --detach <name>         Detach an interface by name
          --reload <name>         Reload an interface by name
          -a, --all               Show every interface
          -A, --announce-stats    Show announce statistics
          -P, --pr-stats          Show path-request statistics
          -l, --link-stats        Show link-table counts
          -B, --burst             Show interfaces with active bursts
          -b, --blocked-ips       Show blocked-IP counts when measured
          -t, --totals            Show traffic totals
          -p, --pps               Include packets/second when measured
          -q, --queues            Show queue statistics when available
          -z, --profiling         Show profiling results when available
          -s, --sort <field>      Sort interfaces by a supported statistic
          -r, --reverse           Reverse sort order
          -j, --json              Emit statistics as JSON
          -R <hash>               Query an identified remote transport
          -i <identity-file>      Identity for remote management
          -w <seconds>            Remote query timeout (default: 15)
          -d, --discovered        List discovered interfaces
          -D                      Show discovered-interface details
          --show-stale            Include stale discovery records
          --show-unknown          Include unknown discovery records
          -m, --monitor           Monitor continuously
          -I, --monitor-interval  Monitor interval (default: 1)
          -v, --verbose           Increase verbosity
        """;

    private static readonly string[] SortFields =
    [
        "rate", "bitrate", "traffic", "rx", "tx", "rxs", "txs", "anns", "announces", "arx", "atx",
        "arxc", "atxc", "held", "prx", "ptx", "prxc", "ptxc", "pvs", "ivs", "flt", "gravity", "g",
        "txdrp", "txdrb", "txbuf",
    ];

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            Options options;
            try { options = Parse(args); }
            catch (FormatException ex) { error.WriteLine($"rnstatus: {ex.Message}"); return 2; }
            catch (OverflowException ex) { error.WriteLine($"rnstatus: {ex.Message}"); return 2; }
            catch (ArgumentException ex) when (ex.Message.StartsWith("Unknown option", StringComparison.Ordinal)
                || ex.Message.StartsWith("Option '", StringComparison.Ordinal)
                || ex.Message.StartsWith("Only one interface filter", StringComparison.Ordinal))
            {
                error.WriteLine($"rnstatus: {ex.Message}");
                return 2;
            }
            if (options.Help) { output.WriteLine(Usage); return 0; }
            if (options.Version) { output.WriteLine("rnstatus 1.5.7"); return 0; }
            if (options.Monitor && (!double.IsFinite(options.MonitorInterval) || options.MonitorInterval <= 0))
                throw new ArgumentException("Monitor interval must be a finite positive number.");
            if (!double.IsFinite(options.RemoteTimeout) || options.RemoteTimeout <= 0)
                throw new ArgumentException("Remote timeout must be a finite positive number.");
            if (options.Sort is not null && !SortFields.Contains(options.Sort, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown sort field '{options.Sort}'.");

            var loaded = RnsdApp.LoadConfiguration(options.Config);
            var config = loaded.Config with
            {
                SharedInstance = loaded.Config.SharedInstance with { Required = options.Remote is null },
            };
            if (options.Discovered || options.ConfigEntries)
                return await RunDiscoveryAsync(config, loaded.Directory, options, output, error, stop).ConfigureAwait(false);

            await using var node = await ReticulumNode.CreateAsync(config, loaded.Directory,
                CliSupport.Logger(error, Math.Clamp(3 + options.Verbosity, 0, 8)), cancellationToken: stop).ConfigureAwait(false);
            await node.StartAsync(stop).ConfigureAwait(false);

            if (options.Attach is not null || options.Detach is not null || options.Reload is not null)
            {
                var (operation, name, success, failure) = options.Attach is { } attach
                    ? ("attach_interface", attach, $"Interface {attach} was attached", $"Could not attach interface {attach}")
                    : options.Detach is { } detach
                        ? ("detach_interface", detach, $"Interface {detach} was detached", $"Could not detach interface {detach}")
                        : ("reload_interface", options.Reload!, $"Interface {options.Reload} was reloaded", $"Could not reload interface {options.Reload}");
                var changed = await node.CallManagementAsync(new Dictionary<object, object?>
                {
                    ["manage"] = operation, ["name"] = name,
                }, stop).ConfigureAwait(false);
                if (changed is true) { output.WriteLine(success); return 0; }
                if (changed is false) { output.WriteLine(failure); return 1; }
                output.WriteLine($"The interface {name} does not exist");
                return 1;
            }

            Identity? remoteIdentity = null;
            byte[]? remoteDestination = null;
            if (options.Remote is not null)
            {
                try
                {
                    remoteDestination = ManagementRemoteService.DestinationHash(ParseHash(options.Remote));
                    remoteIdentity = LoadRemoteIdentity(options.ManagementIdentity);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    error.WriteLine($"rnstatus: {ex.Message}");
                    return 20;
                }
            }
            using (remoteIdentity)
            {
                ManagementRemoteClient? remote = null;
                if (remoteDestination is not null)
                {
                    try
                    {
                        remote = await ManagementRemoteClient.ConnectAsync(node, remoteIdentity!, remoteDestination,
                            TimeSpan.FromSeconds(options.RemoteTimeout), stop).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
                    {
                        error.WriteLine($"rnstatus: {ex.Message}");
                        return 10;
                    }
                }
                using (remote)
                    return await RunStatusAsync(node, remote, options, output, error, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (RemoteManagementException ex)
        {
            error.WriteLine($"rnstatus: {ex.Message}");
            return 10;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or System.Net.Sockets.SocketException or TimeoutException)
        {
            error.WriteLine($"rnstatus: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunStatusAsync(ReticulumNode node, ManagementRemoteClient? remote, Options options,
        TextWriter output, TextWriter error, CancellationToken stop)
    {
            do
            {
                stop.ThrowIfCancellationRequested();
                if (options.Monitor)
                {
                    output.Write("\x1b[H\x1b[2J");
                    await output.FlushAsync(stop).ConfigureAwait(false);
                }

                Dictionary<object, object?>? stats;
                try { stats = await GetStatusAsync(node, remote, options, stop).ConfigureAwait(false); }
                catch (Exception ex) when (remote is not null && (ex is IOException or TimeoutException or InvalidDataException))
                {
                    error.WriteLine($"rnstatus: {ex.Message}");
                    return 10;
                }
                if (stats is null)
                {
                    error.WriteLine(options.Remote is null ? "rnstatus: could not get RNS status" :
                        $"rnstatus: could not get RNS status from remote transport instance {Pretty(ParseHash(options.Remote))}");
                    return options.Remote is null ? 2 : 20;
                }
                if (options.Json)
                {
                    await output.WriteLineAsync(WriteJson(stats)).ConfigureAwait(false);
                }
                else
                {
                    Render(stats, options, output);
                    if (options.LinkStats)
                    {
                        var links = await CallNumberAsync(node, remote, "link_count", stop).ConfigureAwait(false);
                        var active = remote is null
                            ? await CallNumberAsync(node, null, "active_link_count", stop).ConfigureAwait(false)
                            : null;
                        output.WriteLine($" {links} entr{(links == 1 ? "y" : "ies")} in link table" +
                            (active is > 0 ? $" ({active} active)" : ""));
                    }
                }

                if (!options.Monitor) return 0;
                await Task.Delay(TimeSpan.FromSeconds(options.MonitorInterval), stop).ConfigureAwait(false);
            } while (true);
    }

    private static async Task<int> RunDiscoveryAsync(RnsdConfig config, string directory, Options options,
        TextWriter output, TextWriter error, CancellationToken token)
    {
        await using var discovery = new DiscoveryService(Path.Combine(directory, "storage", "discovery"),
            config.Discovery with { Enabled = false });
        var records = await discovery.SnapshotAsync(cancellationToken: token).ConfigureAwait(false);
        var selected = records.Where(record => options.Filter is null ||
                record.Name.Contains(options.Filter, StringComparison.OrdinalIgnoreCase))
            .Where(record => options.ShowStale || record.Status != "stale")
            .Where(record => options.ShowUnknown || !string.IsNullOrEmpty(record.Implementation) && !string.IsNullOrEmpty(record.Version))
            .ToArray();
        if (options.Json)
        {
            await output.WriteLineAsync(WriteJson(records.Select(record =>
                (object?)DiscoveryJson(record, options.ConfigEntries)).ToArray())).ConfigureAwait(false);
            return 0;
        }
        if (options.ConfigEntries)
        {
            var index = 0;
            foreach (var record in selected)
            {
                if (index++ > 0) output.WriteLine("\n" + new string('=', 47) + "\n");
                RenderDiscoveryDetails(record, output);
            }
            return 0;
        }
        output.WriteLine($"{"Name",-25} {"Type",-10}   {"Status",-10} {"Last Heard",-12} {"Value",-7} {"Running",-16} {"Location",-15}");
        output.WriteLine(new string('-', 110));
        foreach (var record in selected)
        {
            var name = record.Name.Length > 25 ? record.Name[..24] + "…" : record.Name;
            var type = record.InterfaceType.Replace("Interface", "", StringComparison.Ordinal);
            var status = record.Status switch { "available" => "✓ Available", "unknown" => "? Unknown", "stale" => "× Stale", _ => record.Status };
            var running = record.Implementation is { Length: > 0 } implementation && record.Version is { Length: > 0 } version
                ? $"{implementation} {version}" : "Unknown";
            if (running.Length > 16) running = running[..15] + "…";
            var location = record.Latitude is { } latitude && record.Longitude is { } longitude
                ? $"{latitude:0.####}, {longitude:0.####}" + (record.Height is { } height ? $", {height:0.####}m h" : "")
                : "Unknown";
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d - record.LastHeard;
            output.WriteLine($"{name,-25} {type,-10}   {status,-10} {PrettyAge(age),-12} {record.StampValue,-7} {running,-16} {location,-15}");
        }
        return 0;
    }

    private static Dictionary<string, object?> DiscoveryJson(DiscoveryRecord record, bool details)
    {
        var fields = record.Packed.Length == 0 ? null : MsgPack.Unpack(record.Packed) as Dictionary<object, object?>;
        object? Field(long key) => fields?.GetValueOrDefault(key);
        var result = new Dictionary<string, object?>
        {
            ["name"] = record.Name, ["type"] = record.InterfaceType, ["status"] = record.Status,
            ["impl_name"] = record.Implementation, ["version"] = record.Version, ["transport"] = record.Transport,
            ["hops"] = record.Hops, ["discovered"] = record.Discovered, ["last_heard"] = record.LastHeard,
            ["latitude"] = record.Latitude, ["longitude"] = record.Longitude, ["height"] = record.Height,
            ["transport_id"] = Convert.FromHexString(record.TransportId),
            ["network_id"] = Convert.FromHexString(record.NetworkId),
            ["value"] = record.StampValue, ["stamp"] = record.Stamp,
            ["received"] = record.Received, ["heard_count"] = record.HeardCount,
            ["discovery_hash"] = SHA256.HashData(Convert.FromHexString(record.TransportId)
                .Concat(Encoding.UTF8.GetBytes(record.Name)).ToArray()),
        };
        if (record.OperatorAddress is { Length: > 0 }) result["operator_lxmf_address"] = record.OperatorAddress;
        if (record.ReachableOn is not null) result["reachable_on"] = record.ReachableOn;
        if (record.Port is { } port) result["port"] = port;
        if (Field(7) is string networkName && networkName.Length > 0) result["ifac_netname"] = networkName;
        if (Field(8) is string passphrase && passphrase.Length > 0) result["ifac_netkey"] = passphrase;
        if (record.InterfaceType == "RNodeInterface")
        {
            result["frequency"] = Field(9);
            result["bandwidth"] = Field(10);
            result["sf"] = Field(11);
            result["cr"] = Field(12);
        }
        else if (record.InterfaceType is "WeaveInterface" or "KISSInterface")
        {
            result["frequency"] = Field(9);
            result["bandwidth"] = Field(10);
            result["modulation"] = Field(13);
            if (record.InterfaceType == "WeaveInterface") result["channel"] = Field(14);
        }
        result["config_entry"] = DiscoveryConfigEntry(record);
        return result;
    }

    private static void RenderDiscoveryDetails(DiscoveryRecord record, TextWriter output)
    {
        var network = Convert.ToHexStringLower(Convert.FromHexString(record.NetworkId));
        var transport = Convert.ToHexStringLower(Convert.FromHexString(record.TransportId));
        if (network != transport) output.WriteLine($"Network   ID : {network}");
        output.WriteLine($"Transport ID : {transport}");
        if (record.OperatorAddress is { Length: > 0 } address)
            output.WriteLine($"LXMF address : {Convert.ToHexStringLower(address)}");
        output.WriteLine($"Name         : {record.Name}");
        output.WriteLine($"Type         : {record.InterfaceType}");
        var stack = record.Implementation is { Length: > 0 } impl && record.Version is { Length: > 0 } version
            ? $"{impl} {version}" : "Unknown";
        output.WriteLine($"Stack        : {stack}");
        output.WriteLine($"Status       : {record.Status switch { "available" => "Available", "unknown" => "Unknown", "stale" => "Stale", _ => record.Status }}");
        output.WriteLine($"Transport    : {(record.Transport ? "Enabled" : "Disabled")}");
        output.WriteLine($"Distance     : {record.Hops} hop{(record.Hops == 1 ? "" : "s")}");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        output.WriteLine($"Discovered   : {PrettyAge(now - record.Discovered)} ago");
        output.WriteLine($"Last Heard   : {PrettyAge(now - record.LastHeard)} ago");
        var location = record.Latitude is { } latitude && record.Longitude is { } longitude
            ? $"{latitude:0.####}, {longitude:0.####}" + (record.Height is { } height ? $", {height:0.####}m h" : "")
            : "Unknown";
        output.WriteLine($"Location     : {location}");
        var fields = record.Packed.Length == 0 ? null : MsgPack.Unpack(record.Packed) as Dictionary<object, object?>;
        object? Field(long key) => fields?.GetValueOrDefault(key);
        if (Field(9) is { } frequency) output.WriteLine($"Frequency    : {frequency}");
        if (Field(10) is { } bandwidth) output.WriteLine($"Bandwidth    : {bandwidth}");
        if (Field(11) is { } sf) output.WriteLine($"Sprd. Factor : {sf}");
        if (Field(12) is { } cr) output.WriteLine($"Coding Rate  : {cr}");
        if (Field(13) is { } modulation) output.WriteLine($"Modulation   : {modulation}");
        if (record.ReachableOn is not null) output.WriteLine($"Address      : {record.ReachableOn}");
        if (record.Port is { } port) output.WriteLine($"Port         : {port}");
        output.WriteLine($"Stamp Value  : {record.StampValue}");
        output.WriteLine("\nConfiguration Entry:");
        if (DiscoveryConfigEntry(record) is { } entry)
            foreach (var line in entry.Split('\n')) output.WriteLine("  " + line);
    }

    private static string? DiscoveryConfigEntry(DiscoveryRecord record)
    {
        var fields = record.Packed.Length == 0 ? null : MsgPack.Unpack(record.Packed) as Dictionary<object, object?>;
        object? Field(long key) => fields?.GetValueOrDefault(key);
        var name = record.Name;
        var transportId = record.TransportId;
        var networkName = Field(7) as string;
        var passphrase = Field(8) as string;
        var networkLine = string.IsNullOrEmpty(networkName) ? "" : $"\n  network_name = {networkName}";
        var passphraseLine = string.IsNullOrEmpty(passphrase) ? "" : $"\n  passphrase = {passphrase}";
        switch (record.InterfaceType)
        {
            case "TCPServerInterface":
            case "BackboneInterface":
                if (string.IsNullOrWhiteSpace(record.ReachableOn) || record.Port is not > 0) return null;
                var clientType = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "TCPClientInterface" : "BackboneInterface";
                var remoteName = clientType == "BackboneInterface" ? "remote" : "target_host";
                return $"[[{name}]]\n  type = {clientType}\n  enabled = yes\n  {remoteName} = {record.ReachableOn}\n  target_port = {record.Port}\n  transport_identity = {transportId}{networkLine}{passphraseLine}";
            case "I2PInterface" when !string.IsNullOrWhiteSpace(record.ReachableOn):
                return $"[[{name}]]\n  type = I2PInterface\n  enabled = yes\n  peers = {record.ReachableOn}.b32.i2p\n  transport_identity = {transportId}{networkLine}{passphraseLine}";
            case "RNodeInterface":
                return $"[[{name}]]\n  type = RNodeInterface\n  enabled = yes\n  port = \n  frequency = {Field(9)}\n  bandwidth = {Field(10)}\n  spreadingfactor = {Field(11)}\n  codingrate = {Field(12)}\n  txpower = {networkLine}{passphraseLine}";
            case "WeaveInterface":
                return $"[[{name}]]\n  type = WeaveInterface\n  enabled = yes\n  port = {networkLine}{passphraseLine}";
            case "KISSInterface":
                return $"[[{name}]]\n  type = KISSInterface\n  enabled = yes\n  port = \n  # Frequency: {Field(9)}\n  # Bandwidth: {Field(10)}\n  # Modulation: {Field(13)}\n  transport_identity = {transportId}{networkLine}{passphraseLine}";
            default:
                return null;
        }
    }

    private static string PrettyAge(double seconds) => seconds < 60 ? "Just now"
        : seconds < 3600 ? $"{(int)(seconds / 60)}m ago"
        : seconds < 86400 ? $"{(int)(seconds / 3600)}h ago" : $"{(int)(seconds / 86400)}d ago";

    private static async Task<Dictionary<object, object?>?> GetStatusAsync(ReticulumNode node, ManagementRemoteClient? remote,
        Options options, CancellationToken token)
    {
        if (remote is null && options.Profiling && node.SharedRole != SharedInstanceRole.Client)
            return node.GetStatistics(profiling: true);
        object? result;
        if (remote is null)
        {
            result = await node.CallManagementAsync(new Dictionary<object, object?> { ["get"] = "interface_stats" }, token).ConfigureAwait(false);
        }
        else
        {
            var response = await remote.RequestAsync("/status", [options.LinkStats, options.Profiling], token).ConfigureAwait(false);
            result = response is object?[] { Length: > 0 } values ? values[0] : null;
        }
        return result as Dictionary<object, object?>;
    }

    private static async Task<long?> CallNumberAsync(ReticulumNode node, ManagementRemoteClient? remote,
        string field, CancellationToken token)
    {
        object? value;
        if (remote is null)
            value = await node.CallManagementAsync(new Dictionary<object, object?> { ["get"] = field }, token).ConfigureAwait(false);
        else
        {
            var response = await remote.RequestAsync("/status", [true, false], token).ConfigureAwait(false);
            value = response is object?[] { Length: > 1 } values ? values[1] : null;
        }
        return value is null ? null : MsgPack.ToInt64(value);
    }

    private static Identity LoadRemoteIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Remote management requires an identity file. Use -i to specify the path.");
        var expanded = CliSupport.ExpandHome(path);
        return IdentityFile.Load(expanded);
    }

    private static byte[] ParseHash(string value)
    {
        if (value.Length != 32)
            throw new ArgumentException("Destination length is invalid, must be 32 hexadecimal characters (16 bytes).");
        try { return Convert.FromHexString(value); }
        catch (FormatException) { throw new ArgumentException("Invalid destination entered. Check your input."); }
    }

    internal static void Render(Dictionary<object, object?> stats, Options options, TextWriter output)
    {
        var interfaces = stats.GetValueOrDefault("interfaces") as object?[] ?? [];
        var rows = interfaces.OfType<Dictionary<object, object?>>().ToList();
        if (!options.ShowAll) rows.RemoveAll(row => IsInternalPeer(Text(row, "name")));
        if (options.Filter is not null)
            rows.RemoveAll(row => !Text(row, "name").Contains(options.Filter, StringComparison.OrdinalIgnoreCase));
        if (options.BurstOnly)
            rows.RemoveAll(row => !Flag(row, "burst_active") && !Flag(row, "pr_burst_active"));
        if (options.Sort is { } sort) rows = Sort(rows, sort, options.Reverse);

        foreach (var row in rows)
        {
            var name = Text(row, "name");
            var status = row.GetValueOrDefault("status") switch
            {
                true => "Up",
                false => "Down",
                _ => "Unknown",
            };
            output.WriteLine();
            output.WriteLine($" {name}");
            var gravity = Number(row, "gravity");
            var statusDetails = gravity is { } gravityValue && gravityValue > 0
                ? $"{status}, gravity {gravityValue}" : status;
            output.WriteLine($"    Status    : {statusDetails}");
            if (Text(row, "autoconnect_source") is { Length: > 0 } source)
                output.WriteLine($"    Source    : Auto-connect via <{source}>");
            if (Text(row, "ifac_netname") is { Length: > 0 } networkName)
                output.WriteLine($"    Network   : {networkName}");
            if (row.GetValueOrDefault("ifac_signature") is byte[] signature && signature.Length >= 5
                && Integer(row, "ifac_size") is { } accessSize)
                output.WriteLine($"    Access    : {accessSize * 8}-bit IFAC by <…{Convert.ToHexStringLower(signature[^5..])}>");
            if (Integer(row, "clients") is { } clients) output.WriteLine($"    Clients   : {clients}");
            if (Integer(row, "bitrate") is { } bitrate) output.WriteLine($"    Rate      : {SpeedString(bitrate)}");
            if (!IsInterfaceWithoutMode(name) && Integer(row, "mode") is { } mode)
            {
                var modeName = mode switch
                {
                    2 => "Point-to-Point",
                    3 => "Access Point",
                    4 => "Roaming",
                    5 => "Boundary",
                    6 => "Gateway",
                    7 => "Internal",
                    _ => "Full",
                };
                if (Flag(row, "announces_to_internal")) modeName += " (a>i)";
                output.WriteLine($"    Mode      : {modeName}");
            }
            if (options.AnnounceStats)
            {
                WriteIfNumber(output, row, "arxc", "RX announces");
                WriteIfNumber(output, row, "atxc", "TX announces");
                WriteIfNumber(output, row, "incoming_announce_frequency", "RX announce rate");
                WriteIfNumber(output, row, "outgoing_announce_frequency", "TX announce rate");
                WriteIfPositiveNumber(output, row, "announce_queue", "Announce queue");
                WriteIfPositiveNumber(output, row, "held_announces", "Held announces");
                if (Number(row, "announce_rate_target") is { } announceTarget)
                {
                    var details = $"t:{PrettyDuration(announceTarget)}";
                    if (Number(row, "announce_rate_penalty") is { } announcePenalty)
                        details += $"/p:{PrettyDuration(announcePenalty)}";
                    if (Number(row, "announce_rate_grace") is { } announceGrace && announceGrace > 0)
                        details += $"/g:{announceGrace}";
                    output.WriteLine("    " + "Announce limit".PadRight(20) + ": (" + details + ")");
                }
                WriteBurstStatus(output, row, "burst_active", "burst_activated", "burst_count", "Announce burst");
            }
            if (options.PathRequestStats)
            {
                WriteIfNumber(output, row, "prxc", "RX path requests");
                WriteIfNumber(output, row, "ptxc", "TX path requests");
                WriteIfNumber(output, row, "incoming_pr_frequency", "RX request rate");
                WriteIfNumber(output, row, "outgoing_pr_frequency", "TX request rate");
                WriteBurstStatus(output, row, "pr_burst_active", "pr_burst_activated", "pr_burst_count", "Request burst");
            }
            if (options.BlockedIps)
            {
                if (Integer(row, "blocked_ips") is { } blockedCount && blockedCount > 0)
                    output.WriteLine("    " + "Blocked IPs".PadRight(20) + ": " + blockedCount);
                if (row.GetValueOrDefault("blocked_ip_list") is IEnumerable<object?> blockedIps)
                    foreach (var address in blockedIps) output.WriteLine($"                       {address}");
            }
            if (Integer(row, "protocol_violations") is { } protocolViolations && protocolViolations > 0)
                output.WriteLine("    " + "Protocol violations".PadRight(20) + ": " + protocolViolations);
            if (Integer(row, "ifac_violations") is { } ifacViolations && ifacViolations > 0)
                output.WriteLine("    " + "IFAC violations".PadRight(20) + ": " + ifacViolations);
            WriteIfPositiveNumber(output, row, "packet_filter_hits", "Packet filter hits");
            WriteIfPositiveNumber(output, row, "txdrp", "TX drops");
            WriteIfPositiveNumber(output, row, "txdrb", "TX drop bytes");
            WriteIfNumber(output, row, "dotreticulum_failed_sends", "Failed sends");
            WriteIfNumber(output, row, "dotreticulum_pending_sends", "Pending sends");
            WriteIfPositiveNumber(output, row, "tx_write_pending_bytes", "Write pending bytes");
            if (options.QueueStats)
            {
                WriteIfPositiveNumber(output, row, "txbuffered", "TX buffered");
                if (Number(row, "txbuffered") is > 0 && Flag(row, "txstalled"))
                    output.WriteLine("    " + "TX stalled".PadRight(20) + ": True");
            }
            if (options.Totals)
            {
                var rx = Integer(row, "rxb");
                var tx = Integer(row, "txb");
                if (rx is not null || tx is not null)
                    output.WriteLine($"    Traffic   : ↑{(tx is { } t ? SizeString(t) : "unavailable")}  ↓{(rx is { } r ? SizeString(r) : "unavailable")}");
                if (Number(row, "rxs") is { } rxRate) output.WriteLine($"                ↓{SpeedString(rxRate)}");
                if (Number(row, "txs") is { } txRate) output.WriteLine($"                ↑{SpeedString(txRate)}");
            }
        }

        if (options.Totals)
        {
            var rx = Integer(stats, "rxb");
            var tx = Integer(stats, "txb");
            output.WriteLine();
            output.WriteLine($" Totals       : ↑{(tx is { } t ? SizeString(t) : "unavailable")}  ↓{(rx is { } r ? SizeString(r) : "unavailable")}");
            if (Number(stats, "txs") is not null || Number(stats, "rxs") is not null)
            {
                var txSpeed = Number(stats, "txs");
                var rxSpeed = Number(stats, "rxs");
                output.WriteLine($" Rates        : ↑{(txSpeed is { } txBits ? SpeedString(txBits) : "unavailable")}  ↓{(rxSpeed is { } rxBits ? SpeedString(rxBits) : "unavailable")}");
            }
            if (options.AnnounceStats && (Number(stats, "atxs") is not null || Number(stats, "arxs") is not null))
                output.WriteLine($" Announces    : ↑{SpeedString(Number(stats, "atxs") ?? 0)}, {Number(stats, "atxf") ?? 0:0.0}/s  ↓{SpeedString(Number(stats, "arxs") ?? 0)}, {Number(stats, "arxf") ?? 0:0.0}/s");
            if (options.PathRequestStats && (Number(stats, "ptxs") is not null || Number(stats, "prxs") is not null))
                output.WriteLine($" Path Requests : ↑{SpeedString(Number(stats, "ptxs") ?? 0)}, {Number(stats, "ptxf") ?? 0:0.0}/s  ↓{SpeedString(Number(stats, "prxs") ?? 0)}, {Number(stats, "prxf") ?? 0:0.0}/s");
            if (options.PacketsPerSecond)
            {
                WriteIfNumber(output, stats, "rxpps", "RX packets/s");
                WriteIfNumber(output, stats, "txpps", "TX packets/s");
            }
        }
        if (options.QueueStats)
        {
            output.WriteLine();
            WriteQueueStatus(output, stats, "Total queue", "rxqt", "tqpressure", "rxqtd");
            WriteQueueStatus(output, stats, "Data queue", "rxqd", "dqpressure", "rxqdd");
            WriteQueueStatus(output, stats, "Announce queue", "rxqa", "aqpressure", "rxqad");
            WriteQueueStatus(output, stats, "Path-request queue", "rxqp", "pqpressure", "rxqpd");
            WriteQueueStatus(output, stats, "Ingress-limit queue", "rxqil", "ilqpressure", "rxqild");
        }
        if (options.Profiling && stats.GetValueOrDefault("dotreticulum_profile") is Dictionary<object, object?> profile)
        {
            output.WriteLine();
            output.WriteLine(" .NET Profiling (not upstream RNS profiler)");
            foreach (var (key, value) in profile) output.WriteLine($"    {key}: {value}");
        }
        if (stats.GetValueOrDefault("transport_id") is byte[] transportId)
        {
            output.WriteLine();
            output.WriteLine($" Transport Instance {Pretty(transportId)} running");
            if (stats.GetValueOrDefault("network_id") is byte[] networkId) output.WriteLine($" Network Identity   {Pretty(networkId)}");
            if (Number(stats, "transport_uptime") is { } uptime) output.WriteLine($" Uptime is {PrettyDuration(uptime)}");
        }
        else if (options.LinkStats) output.WriteLine();
    }

    internal static List<Dictionary<object, object?>> Sort(List<Dictionary<object, object?>> rows, string field, bool reverse)
    {
        var key = field.ToLowerInvariant() switch
        {
            "rate" or "bitrate" => "bitrate",
            "traffic" => "rxb",
            "rx" => "rxb",
            "tx" => "txb",
            "anns" or "announces" => "incoming_announce_frequency",
            "arx" => "incoming_announce_frequency",
            "atx" => "outgoing_announce_frequency",
            "prx" => "incoming_pr_frequency",
            "ptx" => "outgoing_pr_frequency",
            "pvs" => "protocol_violations",
            "ivs" => "ifac_violations",
            "flt" => "packet_filter_hits",
            "gravity" or "g" => "gravity",
            "txdrp" => "txdrp",
            "txdrb" => "txdrb",
            "txbuf" => "txbuffered",
            "arxc" => "arxc",
            "atxc" => "atxc",
            "prxc" => "prxc",
            "ptxc" => "ptxc",
            "held" => "held_announces",
            _ => field,
        };
        Func<Dictionary<object, object?>, double?> selector = row => Number(row, key);
        if (field.Equals("traffic", StringComparison.OrdinalIgnoreCase))
            selector = row =>
            {
                var rx = Number(row, "rxb");
                var tx = Number(row, "txb");
                return rx.HasValue && tx.HasValue ? rx + tx : null;
            };
        if (field.Equals("anns", StringComparison.OrdinalIgnoreCase)
            || field.Equals("announces", StringComparison.OrdinalIgnoreCase))
            selector = row =>
            {
                var incoming = Number(row, "incoming_announce_frequency");
                var outgoing = Number(row, "outgoing_announce_frequency");
                return incoming.HasValue && outgoing.HasValue ? incoming + outgoing : null;
            };
        return reverse ? rows.OrderBy(selector).ToList() : rows.OrderByDescending(selector).ToList();
    }

    private static bool IsInternalPeer(string name) =>
        name.StartsWith("LocalInterface[", StringComparison.Ordinal)
        || name.StartsWith("TCPInterface[Client", StringComparison.Ordinal)
        || name.StartsWith("BackboneInterface[Client on", StringComparison.Ordinal)
        || name.StartsWith("AutoInterfacePeer[", StringComparison.Ordinal)
        || name.StartsWith("WeaveInterfacePeer[", StringComparison.Ordinal)
        || name.StartsWith("I2PInterfacePeer[Connected peer", StringComparison.Ordinal);

    private static bool IsInterfaceWithoutMode(string name) =>
        name.StartsWith("Shared Instance[", StringComparison.Ordinal)
        || name.StartsWith("TCPInterface[Client", StringComparison.Ordinal)
        || name.StartsWith("LocalInterface[", StringComparison.Ordinal);

    private static void WriteIfNumber(TextWriter writer, Dictionary<object, object?> map, string key, string label)
    {
        if (map.TryGetValue(key, out var value) && value is not null) writer.WriteLine($"    {label,-20}: {value}");
    }

    private static void WriteIfPositiveNumber(TextWriter writer, Dictionary<object, object?> map, string key, string label)
    {
        if (Number(map, key) is double value && value > 0) writer.WriteLine($"    {label,-20}: {value}");
    }

    private static void WriteQueueStatus(TextWriter output, Dictionary<object, object?> stats, string label,
        string depthKey, string pressureKey, string droppedKey)
    {
        if (Integer(stats, depthKey) is not { } depth || Number(stats, pressureKey) is not { } pressure) return;
        var dropped = Integer(stats, droppedKey);
        output.WriteLine($" {label,-22}: {pressure * 100:0.0}% {depth} packets{(dropped is > 0 ? $", {dropped} dropped" : "")}");
    }

    private static void WriteBurstStatus(TextWriter output, Dictionary<object, object?> row,
        string activeKey, string activatedKey, string countKey, string label)
    {
        if (!Flag(row, activeKey)) return;
        var count = Integer(row, countKey);
        var activated = Number(row, activatedKey);
        var duration = activated is { } timestamp
            ? PrettyDuration(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d - timestamp) : "unknown";
        output.WriteLine($"    {label,-20}: active{(count is > 0 ? $" on {count}" : "")} for {duration}");
    }

    private static bool Flag(Dictionary<object, object?> map, string key) => map.GetValueOrDefault(key) is true;
    private static string Text(Dictionary<object, object?> map, string key) => map.GetValueOrDefault(key) as string ?? "";
    private static long? Integer(Dictionary<object, object?> map, string key) => map.GetValueOrDefault(key) switch
    {
        byte value => value,
        sbyte value => value,
        short value => value,
        ushort value => value,
        int value => value,
        uint value => value,
        long value => value,
        ulong value when value <= long.MaxValue => (long)value,
        double value when double.IsFinite(value) && value >= long.MinValue && value < 9223372036854775808d => (long)value,
        _ => null,
    };

    private static double? Number(Dictionary<object, object?> map, string key) => map.GetValueOrDefault(key) switch
    {
        byte value => value,
        sbyte value => value,
        short value => value,
        ushort value => value,
        int value => value,
        uint value => value,
        long value => value,
        ulong value => value,
        float value => value,
        double value => value,
        _ => null,
    };
    private static string Pretty(byte[] hash) => "<" + Convert.ToHexStringLower(hash) + ">";

    private static string SizeString(double bytes, string suffix = "B")
    {
        string[] units = ["", "K", "M", "G", "T", "P", "E", "Z"];
        var value = suffix == "b" ? bytes * 8 : bytes;
        foreach (var unit in units)
        {
            if (Math.Abs(value) < 1000)
                return unit.Length == 0 ? $"{value:0} {suffix}" : $"{value:0.00} {unit}{suffix}";
            value /= 1000;
        }
        return $"{value:0.00}Y{suffix}";
    }

    private static string SpeedString(double bitsPerSecond)
    {
        string[] units = ["", "k", "M", "G", "T", "P", "E", "Z"];
        var value = bitsPerSecond;
        foreach (var unit in units)
        {
            if (Math.Abs(value) < 1000)
                return unit.Length == 0 ? $"{value:0} bps" : $"{value:0.00} {unit}bps";
            value /= 1000;
        }
        return $"{value:0.00} Ybps";
    }

    private static string PrettyDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{duration.Seconds}s";
    }

    internal sealed record Options(string? Config, string? Attach, string? Detach, string? Reload, bool ShowAll,
        bool AnnounceStats, bool PathRequestStats, bool LinkStats, bool BurstOnly, bool BlockedIps, bool Totals,
        bool PacketsPerSecond, bool QueueStats, bool Profiling, string? Sort, bool Reverse, bool Json, string? Remote,
        string? ManagementIdentity, double RemoteTimeout, bool Discovered, bool ConfigEntries, bool ShowStale,
        bool ShowUnknown, bool Monitor, double MonitorInterval, int Verbosity, string? Filter, bool Help, bool Version);

    internal static Options Parse(string[] args)
    {
        string? config = null, attach = null, detach = null, reload = null, sort = null, remote = null, identity = null, filter = null;
        var all = false; var announces = false; var requests = false; var links = false; var burst = false;
        var blocked = false; var totals = false; var pps = false; var queues = false; var profiling = false;
        var reverse = false; var json = false; var discovered = false; var configEntries = false;
        var stale = false; var unknown = false; var monitor = false; var monitorInterval = 1d; var remoteTimeout = 15d;
        var verbosity = 0;
        var help = false; var version = false;
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
                case "--attach": attach = Value(args[i]); break;
                case "--detach": detach = Value(args[i]); break;
                case "--reload": reload = Value(args[i]); break;
                case "-a" or "--all": all = true; break;
                case "-A" or "--announce-stats": announces = true; break;
                case "-P" or "--pr-stats": requests = true; break;
                case "-l" or "--link-stats": links = true; break;
                case "-B" or "--burst": burst = true; break;
                case "-b" or "--blocked-ips": blocked = true; break;
                case "-t" or "--totals": totals = true; break;
                case "-p" or "--pps": pps = true; break;
                case "-q" or "--queues": queues = true; break;
                case "-z" or "--profiling": profiling = true; break;
                case "-s" or "--sort": sort = Value(args[i]); break;
                case "-r" or "--reverse": reverse = true; break;
                case "-j" or "--json": json = true; break;
                case "-R": remote = Value(args[i]); break;
                case "-i": identity = Value(args[i]); break;
                case "-w": remoteTimeout = double.Parse(Value(args[i]), CultureInfo.InvariantCulture); break;
                case "-d" or "--discovered": discovered = true; break;
                case "-D": configEntries = true; break;
                case "--show-stale": stale = true; break;
                case "--show-unknown": unknown = true; break;
                case "-m" or "--monitor": monitor = true; break;
                case "-I" or "--monitor-interval": monitorInterval = double.Parse(Value(args[i]), CultureInfo.InvariantCulture); break;
                case "-v" or "--verbose": verbosity++; break;
                default:
                    if (args[i].Length > 1 && args[i][0] == '-' && args[i][1..].All(c => c == 'v'))
                    {
                        verbosity += args[i].Length - 1;
                        break;
                    }
                    if (args[i].StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option '{args[i]}'.");
                    if (filter is not null) throw new ArgumentException("Only one interface filter may be specified.");
                    filter = args[i];
                    break;
            }
        }
        if (new[] { attach, detach, reload }.Count(value => value is not null) > 1)
            throw new ArgumentException("Only one interface-management operation may be selected.");
        return new(config, attach, detach, reload, all, announces, requests, links, burst, blocked, totals, pps, queues,
            profiling, sort, reverse, json, remote, identity, remoteTimeout, discovered, configEntries, stale, unknown,
            monitor, monitorInterval, verbosity, filter, help, version);
    }

    private sealed class RemoteManagementException(string message) : IOException(message);

    private static string WriteJson(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteJsonValue(writer, value);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case byte[] bytes: writer.WriteStringValue(Convert.ToHexStringLower(bytes)); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case byte number: writer.WriteNumberValue(number); break;
            case sbyte number: writer.WriteNumberValue(number); break;
            case short number: writer.WriteNumberValue(number); break;
            case ushort number: writer.WriteNumberValue(number); break;
            case int number: writer.WriteNumberValue(number); break;
            case uint number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case ulong number: writer.WriteNumberValue(number); break;
            case float number when float.IsFinite(number): writer.WriteNumberValue(number); break;
            case double number when double.IsFinite(number): writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            case long[] numbers:
                writer.WriteStartArray();
                foreach (var number in numbers) writer.WriteNumberValue(number);
                writer.WriteEndArray();
                break;
            case int[] numbers:
                writer.WriteStartArray();
                foreach (var number in numbers) writer.WriteNumberValue(number);
                writer.WriteEndArray();
                break;
            case string[] strings:
                writer.WriteStartArray();
                foreach (var text in strings) writer.WriteStringValue(text);
                writer.WriteEndArray();
                break;
            case IReadOnlyList<double> numbers:
                writer.WriteStartArray();
                foreach (var number in numbers) writer.WriteNumberValue(number);
                writer.WriteEndArray();
                break;
            case IReadOnlyList<long> numbers:
                writer.WriteStartArray();
                foreach (var number in numbers) writer.WriteNumberValue(number);
                writer.WriteEndArray();
                break;
            case IReadOnlyList<byte[]> hashes:
                writer.WriteStartArray();
                foreach (var hash in hashes) writer.WriteStringValue(Convert.ToHexStringLower(hash));
                writer.WriteEndArray();
                break;
            case Dictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map) { writer.WritePropertyName(key); WriteJsonValue(writer, item); }
                writer.WriteEndObject();
                break;
            case Dictionary<object, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    writer.WritePropertyName(key switch
                    {
                        byte[] binary => Convert.ToHexStringLower(binary),
                        string text => text,
                        _ => Convert.ToString(key, CultureInfo.InvariantCulture) ?? "",
                    });
                    WriteJsonValue(writer, item);
                }
                writer.WriteEndObject();
                break;
            case IReadOnlyList<KeyValuePair<object, object?>> pairs:
                writer.WriteStartObject();
                foreach (var (key, item) in pairs)
                {
                    writer.WritePropertyName(key switch
                    {
                        byte[] binary => Convert.ToHexStringLower(binary),
                        string text => text,
                        _ => Convert.ToString(key, CultureInfo.InvariantCulture) ?? "",
                    });
                    WriteJsonValue(writer, item);
                }
                writer.WriteEndObject();
                break;
            case object?[] array:
                writer.WriteStartArray();
                foreach (var item in array) WriteJsonValue(writer, item);
                writer.WriteEndArray();
                break;
            case IEnumerable<object?> sequence:
                writer.WriteStartArray();
                foreach (var item in sequence) WriteJsonValue(writer, item);
                writer.WriteEndArray();
                break;
            default: throw new InvalidDataException($"Unsupported management JSON value: {value.GetType().Name}.");
        }
    }
}
