using System.Globalization;
using System.Security.Cryptography;
using DotReticulum.Interfaces;

namespace DotReticulum.Applications.Rnodeconf;

public static class RnodeconfCli
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "info", "config", "show-psk", "eeprom-dump", "eeprom-backup", "normal", "tnc",
        "bluetooth-on", "bluetooth-off", "bluetooth-pair", "recondition-display",
        "ia-enable", "ia-disable", "authorize-device-write", "authorize-trust",
        "key", "public", "sign", "rom", "eeprom-wipe", "get-firmware-hash", "get-target-firmware-hash",
        "clear-cache", "update", "force-update", "flash", "autoinstall", "extract", "use-extracted", "nocheck",
        "authorize-key-write", "authorize-cache-write", "authorize-eeprom", "authorize-flash",
        "authorize-firmware-download", "authorize-unverified-device", "authorize-firmware-fallback", "version"
        , "list-models", "list-ports"
    };
    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        "display", "timeout", "rotation", "np", "display-addr", "wifi", "channel", "ssid", "psk", "ip", "nm",
        "freq", "bw", "txp", "sf", "cr", "trust-key", "state-dir", "backup-file",
        "fw-version", "fw-url", "baud-flash", "flasher", "platform", "product", "model", "hwrev", "firmware-hash", "usb-serial"
    };
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-i"] = "info", ["-c"] = "config", ["-N"] = "normal", ["-T"] = "tnc",
        ["-b"] = "bluetooth-on", ["-B"] = "bluetooth-off", ["-p"] = "bluetooth-pair",
        ["-D"] = "display", ["-t"] = "timeout", ["-R"] = "rotation", ["-w"] = "wifi",
        ["-x"] = "ia-enable", ["-X"] = "ia-disable",
        ["-k"] = "key", ["-P"] = "public", ["-S"] = "sign", ["-r"] = "rom", ["-H"] = "firmware-hash",
        ["-K"] = "get-target-firmware-hash", ["-L"] = "get-firmware-hash", ["-C"] = "clear-cache",
        ["-u"] = "update", ["-U"] = "force-update", ["-f"] = "flash", ["-a"] = "autoinstall",
        ["-e"] = "extract", ["-E"] = "use-extracted"
    };
    private static readonly string[] SettingOrder =
    [
        "display", "timeout", "rotation", "recondition-display", "ia-enable", "ia-disable", "np", "display-addr",
        "bluetooth-on", "bluetooth-off", "bluetooth-pair", "channel", "ssid", "psk", "ip", "nm", "wifi"
    ];

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default, Func<string, CancellationToken, ValueTask<Stream>>? openSerial = null)
        => await RunCoreAsync(args, output, error, cancellationToken, openSerial, null).ConfigureAwait(false);

    public static Task<int> RunWithServicesAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, Func<string, CancellationToken, ValueTask<Stream>> openSerial,
        RnodeconfServices services) => RunCoreAsync(args, output, error, cancellationToken, openSerial, services);

    private static async Task<int> RunCoreAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, Func<string, CancellationToken, ValueTask<Stream>>? openSerial, RnodeconfServices? services)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            await output.WriteLineAsync("""
                rnodeconf — native RNode maintenance (experimental, partial RNS 1.5.7 parity)
                Usage: rnodeconf PORT [--info | --config | --eeprom-dump | --eeprom-backup]
                  --backup-file PATH                 Never overwrites an existing backup
                  --normal | --tnc --freq HZ --bw HZ --txp DBM --sf N --cr N
                  --display N --timeout N --rotation N --np N --display-addr HEX
                  --recondition-display --ia-enable --ia-disable
                  --bluetooth-on --bluetooth-off --bluetooth-pair
                  --wifi OFF|AP|STATION --channel N --ssid TEXT --psk TEXT --ip IP --nm IP
                  --authorize-device-write           Required for every device setting/RF change
                  --trust-key DER_HEX --authorize-trust [--state-dir PATH]
                  --show-psk                         Explicitly reveal configured PSK
                  --key/-k --public/-P --sign/-S --rom/-r --eeprom-wipe --firmware-hash HEX
                  --update/-u --force-update/-U --flash/-f --autoinstall/-a --extract/-e
                  --fw-version VERSION --fw-url RELEASE_BASE --nocheck --use-extracted
                  --platform HEX --product HEX --model HEX --hwrev N --flasher INSTALLED_EXE
                  --list-models --list-ports --usb-serial USB_ID
                  --authorize-key-write --authorize-cache-write --authorize-eeprom
                  --authorize-flash --authorize-firmware-download --authorize-unverified-device
                  --authorize-firmware-fallback       Retry default metadata source once on failure
                Explicit independent installed flash tools only; downloaded scripts are NOT executed.
                Hardware/RF acceptance is NOT verified by simulator tests. Stop the owning daemon.
                """).ConfigureAwait(false);
            return 0;
        }
        if (args is ["--version"])
        {
            await output.WriteLineAsync("rnodeconf 2.5.0 (DotReticulum partial implementation; reference RNS 1.5.7)").ConfigureAwait(false);
            return 0;
        }
        try
        {
            foreach (var argument in args)
                if (argument.StartsWith("--", StringComparison.Ordinal) && argument.Contains('=') &&
                    Flags.Contains(argument[2..argument.IndexOf('=')]))
                    throw new RnodeconfUsageException("A boolean flag does not accept a value.");
            args = RnodeconfArguments.Normalize(args);
            if (args.Contains("--help") || args.Contains("-h"))
                return await RunCoreAsync(["--help"], output, error, cancellationToken, openSerial, services).ConfigureAwait(false);
            var options = new Dictionary<string, string?>(StringComparer.Ordinal);
            string? port = null;
            var positionalOnly = false;
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "--" && !positionalOnly) { positionalOnly = true; continue; }
                if (positionalOnly || !arg.StartsWith("-", StringComparison.Ordinal))
                {
                    if (port is not null) throw new RnodeconfUsageException("Only one serial port is allowed.");
                    port = arg;
                    continue;
                }
                var option = Aliases.GetValueOrDefault(arg) ?? (arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..] : arg);
                if (Flags.Contains(option)) options[option] = null;
                else if (Values.Contains(option))
                {
                    if (++i >= args.Length) throw new RnodeconfUsageException($"Missing value for --{option}.");
                    options[option] = args[i];
                }
                else throw new RnodeconfUsageException("Unknown option; no device or firmware operation was performed.");
            }
            if (options.ContainsKey("version"))
            {
                await output.WriteLineAsync("rnodeconf 2.5.0 (DotReticulum partial implementation; reference RNS 1.5.7)").ConfigureAwait(false);
                return 0;
            }
            if (options.TryGetValue("trust-key", out var key))
            {
                if (port is not null || options.Keys.Any(k => k is not ("trust-key" or "authorize-trust" or "state-dir")))
                    throw new ArgumentException("Trust enrollment cannot be combined with device operations.");
                if (!options.ContainsKey("authorize-trust")) throw new UnauthorizedAccessException("Trust changes require --authorize-trust.");
                await TrustKeyAsync(key!, options.GetValueOrDefault("state-dir"), output, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            if (options.ContainsKey("list-models"))
            {
                await RnodeconfInformation.ListModelsAsync(output).ConfigureAwait(false);
                return 0;
            }
            if (options.ContainsKey("list-ports"))
            {
                foreach (var serial in (services?.EnumeratePorts ?? RnodeconfSerialDiscovery.Enumerate)())
                    await output.WriteLineAsync($"{serial.Port}  USB={serial.UsbSerial ?? "unknown"}  {serial.Description}").ConfigureAwait(false);
                return 0;
            }
            if (port is null && options.TryGetValue("usb-serial", out var serialId))
            {
                var matches = (services?.EnumeratePorts ?? RnodeconfSerialDiscovery.Enumerate)()
                    .Where(p => p.UsbSerial == serialId).ToArray();
                if (matches.Length != 1) throw new IOException("USB serial selection must identify exactly one available port.");
                port = matches[0].Port;
            }
            openSerial ??= async (name, token) => await RNodeMaintenanceConnection.OpenSerialAsync(name, token).ConfigureAwait(false);
            if (RnodeconfAdvanced.Handles(options))
            {
                if (SettingOrder.Any(options.ContainsKey) || options.Keys.Any(k => k is "normal" or "tnc" or "config" or "eeprom-backup" or "eeprom-dump"))
                    throw new ArgumentException("Advanced operations must be separate from maintenance settings/exports.");
                var exclusive = options.Keys.Count(k => k is "key" or "public" or "rom" or "eeprom-wipe" or "clear-cache" or "extract" or "update" or "force-update" or "flash" or "autoinstall");
                if (exclusive > 1) throw new ArgumentException("Choose one advanced operation per invocation.");
                if (options.Keys.Any(k => k is "get-firmware-hash" or "get-target-firmware-hash") &&
                    (exclusive > 0 || options.ContainsKey("sign") || options.ContainsKey("firmware-hash")))
                    throw new ArgumentException("Hash queries cannot be combined with device mutations.");
                if (exclusive > 0 && (options.ContainsKey("sign") || options.ContainsKey("firmware-hash")))
                    throw new ArgumentException("Signing/hash mutations must be separate from installation, extraction and local-key operations.");
                return await RnodeconfAdvanced.RunAsync(options, port, output, openSerial, services ?? new(), cancellationToken).ConfigureAwait(false);
            }
            if (options.Keys.Any(k => k is "fw-version" or "fw-url" or "baud-flash" or "flasher" or "platform" or "product" or "model" or "hwrev" or "nocheck" or "use-extracted"))
                throw new ArgumentException("Firmware/manufacturing parameters require an explicit advanced operation.");
            if (port is null) throw new ArgumentException("A serial port is required.");
            if (options.ContainsKey("show-psk") && !options.ContainsKey("config"))
                throw new ArgumentException("--show-psk requires --config.");
            if (options.ContainsKey("backup-file") && !options.ContainsKey("eeprom-backup"))
                throw new ArgumentException("--backup-file requires --eeprom-backup.");
            var operations = SettingOrder.Where(options.ContainsKey).ToArray();
            if (options.ContainsKey("display-addr") && (operations.Length > 1 || options.ContainsKey("normal") || options.ContainsKey("tnc")))
                throw new ArgumentException("Display-address reset must be a separate setting operation.");
            var mutating = operations.Length > 0 || options.ContainsKey("normal") || options.ContainsKey("tnc");
            if (mutating && !options.ContainsKey("authorize-device-write"))
                throw new UnauthorizedAccessException("Device writes require --authorize-device-write; the port was not opened.");
            if (options.ContainsKey("normal") && options.ContainsKey("tnc")) throw new ArgumentException("Choose normal or TNC mode, not both.");
            if (!options.ContainsKey("tnc") && options.Keys.Any(k => k is "freq" or "bw" or "txp" or "sf" or "cr"))
                throw new ArgumentException("Radio parameters require --tnc.");
            if (options.ContainsKey("tnc") && new[] { "freq", "bw", "txp", "sf", "cr" }.Any(k => !options.ContainsKey(k)))
                throw new ArgumentException("TNC requires all five radio parameters; interactive prompting is not supported.");
            if (mutating && options.Keys.Any(k => k is "config" or "eeprom-dump" or "eeprom-backup"))
                throw new ArgumentException("Read/export and setting operations must be separate invocations.");
            if (options.ContainsKey("bluetooth-on") && options.ContainsKey("bluetooth-off") ||
                options.ContainsKey("ia-enable") && options.ContainsKey("ia-disable"))
                throw new ArgumentException("Conflicting settings.");
            // Validate every value before opening the port: no partially-applied configuration.
            var commands = operations.Select(k => RnodeconfCommands.Encode(k, options[k])).ToList();
            var tnc = options.ContainsKey("tnc") ? RnodeconfCommands.Tnc(
                long.Parse(options["freq"]!, CultureInfo.InvariantCulture), long.Parse(options["bw"]!, CultureInfo.InvariantCulture),
                int.Parse(options["txp"]!, CultureInfo.InvariantCulture), int.Parse(options["sf"]!, CultureInfo.InvariantCulture),
                int.Parse(options["cr"]!, CultureInfo.InvariantCulture), 0) : null;
            await using var stream = await openSerial(port, cancellationToken).ConfigureAwait(false);
            using var client = new RnodeconfClient(stream);
            await client.IdentifyAsync(output, cancellationToken).ConfigureAwait(false);
            if (!mutating && !options.Keys.Any(k => k is "config" or "eeprom-dump" or "eeprom-backup"))
            {
                var infoEeprom = await client.ReadEepromAsync(cancellationToken).ConfigureAwait(false);
                await RnodeconfInformation.PrintAsync(client, infoEeprom, StateDirectory(options.GetValueOrDefault("state-dir")), output).ConfigureAwait(false);
                return 0;
            }
            var eeprom = await client.ReadEepromAsync(cancellationToken).ConfigureAwait(false);
            if (mutating && !RnodeconfClient.IsProvisioned(eeprom)) throw new IOException("EEPROM is unprovisioned or its checksum is invalid; refusing device settings.");
            if (options.ContainsKey("config")) await client.PrintConfigurationAsync(eeprom, options.ContainsKey("show-psk"), output, cancellationToken).ConfigureAwait(false);
            if (options.ContainsKey("eeprom-dump")) await output.WriteLineAsync(Convert.ToHexStringLower(eeprom)).ConfigureAwait(false);
            if (options.ContainsKey("eeprom-backup"))
            {
                var path = options.GetValueOrDefault("backup-file") ?? Path.Combine(
                    StateDirectory(options.GetValueOrDefault("state-dir")), "eeprom", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".eeprom");
                await SaveNewAsync(path, eeprom, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"EEPROM backup written to: {Path.GetFullPath(path)}").ConfigureAwait(false);
            }
            if (options.ContainsKey("normal")) commands.Add(RnodeconfCommands.Encode("normal"));
            if (tnc is not null)
            {
                commands.AddRange(tnc);
                if (client.Platform == 0x80) commands.Add(RnodeconfCommands.Encode("reset"));
            }
            if (options.ContainsKey("display-addr")) commands.Add(RnodeconfCommands.Encode("reset"));
            if (mutating)
            {
                try
                {
                    await client.WriteAuthorizedAsync(commands, true, cancellationToken).ConfigureAwait(false);
                    if (options.ContainsKey("bluetooth-pair"))
                        await output.WriteLineAsync($"Bluetooth pairing PIN: {await client.BluetoothPinAsync(cancellationToken).ConfigureAwait(false):D6}").ConfigureAwait(false);
                }
                finally { foreach (var bytes in commands) CryptographicOperations.ZeroMemory(bytes); }
                await output.WriteLineAsync("Maintenance commands sent; persistence, RF behavior and reboot outcome are not verified.").ConfigureAwait(false);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
        catch (RnodeconfUsageException exception)
        {
            await error.WriteLineAsync($"rnodeconf: {exception.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("rnodeconf: Firmware/device-tool deadline exceeded.").ConfigureAwait(false);
            return 1;
        }

        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or
            UnauthorizedAccessException or IOException or NotSupportedException or TimeoutException or CryptographicException or
            HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Do not echo argument values: a malformed WiFi credential must not leak into logs.
            await error.WriteLineAsync($"rnodeconf: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private sealed class RnodeconfUsageException(string message) : ArgumentException(message);

    private static string StateDirectory(string? path) =>
        path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "rnodeconf");

    private static async Task TrustKeyAsync(string hex, string? directory, TextWriter output, CancellationToken token)
    {
        byte[] der;
        try { der = Convert.FromHexString(hex); } catch (FormatException) { throw new ArgumentException("Invalid trust key hex."); }
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(der, out var consumed);
        if (consumed != der.Length) throw new ArgumentException("Trailing trust key data.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(der));
        await SaveNewAsync(Path.Combine(StateDirectory(directory), "trusted_keys", hash + ".pubkey"), der, token).ConfigureAwait(false);
        await output.WriteLineAsync($"Trusting key: {hash}").ConfigureAwait(false);
    }

    private static async Task SaveNewAsync(string path, byte[] data, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var file = new FileStream(path, settings);
        try
        {
            await using (file)
            {
                await file.WriteAsync(data, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
}
