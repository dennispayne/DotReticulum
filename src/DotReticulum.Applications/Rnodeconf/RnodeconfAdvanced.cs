using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Core;
using DotReticulum.Interfaces;

namespace DotReticulum.Applications.Rnodeconf;

public sealed class RnodeconfServices
{
    public HttpClient? Http { get; init; }
    public Func<RnodeconfToolCall, CancellationToken, Task<int>>? ExecuteTool { get; init; }
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    public Func<IReadOnlyList<RnodeconfSerialPort>> EnumeratePorts { get; init; } = RnodeconfSerialDiscovery.Enumerate;
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan PortRecoveryTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

internal static class RnodeconfAdvanced
{
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 });
    internal static bool Handles(Dictionary<string, string?> options) => options.Keys.Any(k => k is
        "key" or "public" or "sign" or "rom" or "eeprom-wipe" or "firmware-hash" or "get-firmware-hash" or
        "get-target-firmware-hash" or "clear-cache" or "update" or "force-update" or "flash" or "autoinstall" or "extract");

    internal static async Task<int> RunAsync(Dictionary<string, string?> options, string? port, TextWriter output,
        Func<string, CancellationToken, ValueTask<Stream>> openSerial, RnodeconfServices services, CancellationToken token)
    {
        var state = Path.GetFullPath(options.GetValueOrDefault("state-dir") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "rnodeconf"));
        bool Has(string name) => options.ContainsKey(name);
        void Require(string authorization)
        {
            if (!Has(authorization)) throw new UnauthorizedAccessException($"Operation requires --{authorization}; no device tool was run.");
        }
        if (Has("key"))
        {
            Require("authorize-key-write");
            await RnodeconfSigning.GenerateKeysAsync(state, token).ConfigureAwait(false);
            await output.WriteLineAsync("Signing keys present; existing keys were not overwritten. Protect Windows directory ACLs.").ConfigureAwait(false);
            return 0;
        }
        if (Has("public"))
        {
            using var key = RnodeconfSigning.LoadRsa(Path.Combine(state, "firmware", "signing.key"));
            await output.WriteLineAsync("EEPROM Signing Public key:\n" + Convert.ToHexStringLower(key.ExportSubjectPublicKeyInfo())).ConfigureAwait(false);
            var deviceKey = Path.Combine(state, "firmware", "device.key");
            if (new FileInfo(deviceKey).Length != 64) throw new IOException("Invalid device signing key length.");
            var bytes = await File.ReadAllBytesAsync(deviceKey, token).ConfigureAwait(false);
            try
            {
                using var identity = Identity.FromPrivateKey(bytes);
                await output.WriteLineAsync("Device Signing Public key:\n" + Convert.ToHexStringLower(identity.ExportPublicKey().AsSpan(32))).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return 0;
        }
        if (Has("clear-cache"))
        {
            Require("authorize-cache-write");
            var cache = Path.Combine(state, "update");
            if (Directory.Exists(cache))
            {
                if ((File.GetAttributes(cache) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to delete a linked firmware cache.");
                Directory.Delete(cache, true);
            }
            await output.WriteLineAsync("Local firmware cache cleared.").ConfigureAwait(false);
            return 0;
        }
        if (port is null) throw new ArgumentException("A serial port is required.");
        var originalPort = port;
        var usbSerial = options.GetValueOrDefault("usb-serial") ?? services.EnumeratePorts()
            .FirstOrDefault(p => p.Port.Equals(originalPort, StringComparison.OrdinalIgnoreCase))?.UsbSerial;
        var onlyHashQuery = Has("get-firmware-hash") || Has("get-target-firmware-hash");
        if (!onlyHashQuery && !Has("extract")) Require("authorize-device-write");
        if (Has("rom") || Has("eeprom-wipe") || Has("autoinstall") || Has("flash")) Require("authorize-eeprom");
        if (Has("update") || Has("force-update") || Has("flash") || Has("autoinstall") || Has("extract")) Require("authorize-flash");
        if (Has("extract")) Require("authorize-device-write"); // esptool resets device even when reading flash.
        if (Has("update") || Has("force-update") || Has("flash") || Has("autoinstall") || Has("extract"))
        {
            _ = RnodeconfFirmware.ValidateFlasher(options.GetValueOrDefault("flasher") ??
                throw new ArgumentException("Specify an independently installed --flasher executable."), state);
            if (!int.TryParse(options.GetValueOrDefault("baud-flash") ?? "921600", NumberStyles.None, CultureInfo.InvariantCulture, out var baud) || baud is < 1200 or > 4000000)
                throw new ArgumentException("Invalid flash baud rate.");
        }
        if (Has("sign") || Has("firmware-hash"))
        {
            if (Has("firmware-hash"))
            {
                var hash = ParseHash(options["firmware-hash"]!);
                CryptographicOperations.ZeroMemory(hash);
            }
        }
        if (Has("flash") || Has("autoinstall"))
        {
            var platform = ParseByte(options.GetValueOrDefault("platform") ?? throw new ArgumentException("Installation requires --platform HEX."));
            var model = ParseByte(options.GetValueOrDefault("model") ?? throw new ArgumentException("Installation requires --model HEX."));
            _ = ParseByte(options.GetValueOrDefault("product") ?? throw new ArgumentException("Installation requires --product HEX."));
            if (!byte.TryParse(options.GetValueOrDefault("hwrev"), out var hardwareRevision) || hardwareRevision == 0)
                throw new ArgumentException("Installation requires --hwrev 1..255.");
            if (platform is not (0x70 or 0x80 or 0x90)) throw new ArgumentException("Invalid installation platform.");
            if (Has("autoinstall"))
            {
                Require("authorize-key-write");
                await RnodeconfSigning.GenerateKeysAsync(state, token).ConfigureAwait(false);
            }
            using (var key = RnodeconfSigning.LoadRsa(Path.Combine(state, "firmware", "signing.key"))) { }
            var artifact = await PrepareAsync(options, services, state, RnodeconfFirmware.FirmwareForModel(model), token).ConfigureAwait(false);
            await FlashAsync(options, services, artifact, state, port, platform, token).ConfigureAwait(false);
            await services.Delay(TimeSpan.FromSeconds(platform == 0x70 ? 6 : 8), token).ConfigureAwait(false);
            port = await ResolvePortAsync(port, usbSerial, services, token).ConfigureAwait(false);
            if (Has("autoinstall"))
            {
                await using var resetStream = await openSerial(port, token).ConfigureAwait(false);
                using var probe = new RnodeconfClient(resetStream);
                await probe.IdentifyAsync(output, token).ConfigureAwait(false);
                var existing = await probe.ReadEepromAsync(token).ConfigureAwait(false);
                if (RnodeconfSigning.VerifyKnownSigner(existing, state) is null)
                {
                    await probe.WriteAuthorizedAsync([RnodeconfCommands.Encode("eeprom-wipe")], true, token).ConfigureAwait(false);
                    await services.Delay(TimeSpan.FromSeconds(probe.Board == 0x51 ? 23 : 13), token).ConfigureAwait(false);
                }
            }
            port = await ResolvePortAsync(port, usbSerial, services, token).ConfigureAwait(false);
            await BootstrapAsync(options, output, openSerial, services, state, port, token).ConfigureAwait(false);
            await CompleteInstallationAsync(options, output, openSerial, services, state, port, usbSerial, artifact, token).ConfigureAwait(false);
            return 0;
        }
        if (Has("rom"))
        {
            await using (var existingStream = await openSerial(port, token).ConfigureAwait(false))
            {
                using var existingClient = new RnodeconfClient(existingStream);
                await existingClient.IdentifyAsync(output, token).ConfigureAwait(false);
                if (RnodeconfClient.IsProvisioned(await existingClient.ReadEepromAsync(token).ConfigureAwait(false)))
                {
                    await output.WriteLineAsync("EEPROM already provisioned; no changes made.").ConfigureAwait(false);
                    return 0;
                }
            }
            await BootstrapAsync(options, output, openSerial, services, state, port, token).ConfigureAwait(false);
            await CompleteInstallationAsync(options, output, openSerial, services, state, port, usbSerial, null, token).ConfigureAwait(false);
            return 0;
        }
        await using var stream = await openSerial(port, token).ConfigureAwait(false);
        using var client = new RnodeconfClient(stream);
        await client.IdentifyAsync(output, token).ConfigureAwait(false);
        if (Has("eeprom-wipe"))
        {
            await client.WriteAuthorizedAsync([RnodeconfCommands.Encode("eeprom-wipe")], true, token).ConfigureAwait(false);
            await services.Delay(TimeSpan.FromSeconds(client.Board == 0x51 ? 23 : 13), token).ConfigureAwait(false);
            if (client.Platform != 0x70)
            {
                await client.WriteAuthorizedAsync([RnodeconfCommands.Encode("reset")], true, token).ConfigureAwait(false);
                await services.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            await output.WriteLineAsync("EEPROM wipe command sent; physical erase/restart outcome is not independently verified.").ConfigureAwait(false);
            return 0;
        }
        var eeprom = await client.ReadEepromAsync(token).ConfigureAwait(false);
        if (!RnodeconfClient.IsProvisioned(eeprom)) throw new IOException("Device EEPROM is not provisioned or checksum is invalid.");
        var vendor = RnodeconfSigning.VerifyKnownSigner(eeprom, state);
        await output.WriteLineAsync(vendor is null ? "EEPROM signature is unknown/unverified." : $"EEPROM signature verified: {vendor}").ConfigureAwait(false);
        if (Has("get-firmware-hash") || Has("get-target-firmware-hash"))
        {
            if (Has("get-target-firmware-hash")) await output.WriteLineAsync("The target firmware hash is: " + Convert.ToHexStringLower(await client.FirmwareHashAsync(true, token).ConfigureAwait(false))).ConfigureAwait(false);
            if (Has("get-firmware-hash")) await output.WriteLineAsync("The actual firmware hash is: " + Convert.ToHexStringLower(await client.FirmwareHashAsync(false, token).ConfigureAwait(false))).ConfigureAwait(false);
            return 0;
        }
        if (Has("sign"))
        {
            var deviceKey = Path.Combine(state, "firmware", "device.key");
            if (new FileInfo(deviceKey).Length != 64) throw new IOException("Invalid device signing key length.");
            var key = await File.ReadAllBytesAsync(deviceKey, token).ConfigureAwait(false);
            try
            {
                using var identity = Identity.FromPrivateKey(key);
                var signature = identity.Sign(await client.DeviceHashAsync(token).ConfigureAwait(false));
                await client.WriteAuthorizedAsync([RNodeProtocol.Frame(0x57, signature)], true, token).ConfigureAwait(false);
                await output.WriteLineAsync("Device signature command sent.").ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        if (Has("firmware-hash"))
        {
            var hash = ParseHash(options["firmware-hash"]!);
            try { await client.WriteAuthorizedAsync([RNodeProtocol.Frame(0x58, hash)], true, token).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(hash); }
            await output.WriteLineAsync("Firmware hash command sent.").ConfigureAwait(false);
        }
        if (Has("extract"))
        {
            if (client.Platform != 0x80) throw new NotSupportedException("Pinned upstream firmware extraction is ESP32-only.");
            if (vendor is null) Require("authorize-unverified-device");
            var hash = await client.FirmwareHashAsync(false, token).ConfigureAwait(false);
            var version = client.FirmwareVersion;
            stream.Dispose();
            await ExtractAsync(options, services, state, port, token).ConfigureAwait(false);
            await RnodeconfSigning.SaveNewAsync(Path.Combine(state, "extracted", "extracted_rnode_firmware.version"),
                Encoding.UTF8.GetBytes($"{version} {Convert.ToHexStringLower(hash)}"), token).ConfigureAwait(false);
            await output.WriteLineAsync("Firmware extraction completed by the selected tool; publisher provenance is not authenticated.").ConfigureAwait(false);
            return 0;
        }
        if (Has("update") || Has("force-update"))
        {
            if (vendor is null) Require("authorize-unverified-device");
            var artifact = await PrepareAsync(options, services, state,
                Has("use-extracted") ? "extracted_rnode_firmware.zip" : RnodeconfFirmware.FirmwareForDevice(eeprom[1], client.Platform, client.Mcu, client.Board), token).ConfigureAwait(false);
            if (!Has("force-update") && decimal.Parse(artifact.Version, CultureInfo.InvariantCulture) <= decimal.Parse(client.FirmwareVersion, CultureInfo.InvariantCulture))
            {
                await output.WriteLineAsync("Selected firmware is not newer; no update performed. Use --force-update to authorize reinstall/downgrade.").ConfigureAwait(false);
                return 0;
            }
            if (artifact.Filename.EndsWith(".zip", StringComparison.Ordinal) && artifact.Filename != "extracted_rnode_firmware.zip")
                await RnodeconfFirmware.ExtractArchiveAsync(artifact, token).ConfigureAwait(false);
            var partitionHash = await RnodeconfFirmware.PartitionHashAsync(client.Platform, artifact, token).ConfigureAwait(false);
            await client.WriteAuthorizedAsync([RnodeconfCommands.Encode("update-indicator")], true, token).ConfigureAwait(false);
            if (partitionHash is not null)
            {
                await client.WriteAuthorizedAsync([RNodeProtocol.Frame(0x58, partitionHash)], true, token).ConfigureAwait(false);
                await services.Delay(TimeSpan.FromSeconds(client.Platform == 0x70 ? 14 : 1), token).ConfigureAwait(false);
            }
            var platform = client.Platform;
            stream.Dispose();
            await FlashAsync(options, services, artifact, state, port, platform, token).ConfigureAwait(false);
            await services.Delay(TimeSpan.FromSeconds(platform == 0x70 ? 6 : 8), token).ConfigureAwait(false);
            port = await ResolvePortAsync(port, usbSerial, services, token).ConfigureAwait(false);
            await using var reconnected = await openSerial(port, token).ConfigureAwait(false);
            using var verified = new RnodeconfClient(reconnected);
            await verified.IdentifyAsync(output, token).ConfigureAwait(false);
            if (decimal.Parse(verified.FirmwareVersion, CultureInfo.InvariantCulture) != decimal.Parse(artifact.Version, CultureInfo.InvariantCulture))
                throw new IOException("Device firmware version after update does not match selected version.");
            var restored = await verified.ReadEepromAsync(token).ConfigureAwait(false);
            if (!RnodeconfClient.IsProvisioned(restored)) throw new IOException("Device EEPROM invalid after firmware update.");
            if (partitionHash is not null)
                await VerifyFirmwareHashesAsync(verified, partitionHash, token).ConfigureAwait(false);
            await output.WriteLineAsync("Firmware update tool succeeded and device reported selected version; RF behavior is unverified.").ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<RnodeconfFirmwareArtifact> PrepareAsync(Dictionary<string, string?> options, RnodeconfServices services, string state, string filename, CancellationToken token)
    {
        if (options.ContainsKey("use-extracted"))
        {
            var folder = Path.Combine(state, "extracted");
            var fields = (await File.ReadAllTextAsync(Path.Combine(folder, "extracted_rnode_firmware.version"), token).ConfigureAwait(false))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2) throw new IOException("Invalid extracted firmware metadata.");
            RnodeconfFirmware.ValidateComponent(fields[0]);
            _ = ParseHash(fields[1]);
            var path = Path.Combine(folder, "extracted_rnode_firmware.bin");
            // ESP32's recorded firmware hash covers the image preceding its embedded final digest,
            // not the entire 2MiB extracted flash region. The embedded image validation is separate.
            foreach (var suffix in new[] { ".bin", ".bootloader", ".boot_app0", ".partitions" })
            {
                var component = Path.Combine(folder, "extracted_rnode_firmware" + suffix);
                if (!File.Exists(component)) throw new IOException("Required extracted firmware component is missing.");
                if (new FileInfo(component).Length > RnodeconfFirmware.MaximumFirmwareBytes) throw new IOException("Extracted component exceeds size limit.");
            }
            if (!File.Exists(Path.Combine(folder, "extracted_console_image.bin"))) throw new IOException("Extracted console image is missing.");
            return new(fields[0], "extracted_rnode_firmware.zip", path, fields[1]);
        }
        return await new RnodeconfFirmware(services.Http ?? DefaultHttp, state).PrepareAsync(filename, options.GetValueOrDefault("fw-version"),
            options.TryGetValue("fw-url", out var url) ? new Uri(url!, UriKind.Absolute) : null, options.ContainsKey("nocheck"),
            options.ContainsKey("authorize-firmware-download"), token, options.ContainsKey("authorize-firmware-fallback")).ConfigureAwait(false);
    }

    private static async Task FlashAsync(Dictionary<string, string?> options, RnodeconfServices services, RnodeconfFirmwareArtifact artifact,
        string state, string port, byte platform, CancellationToken token)
    {
        if (artifact.Filename.EndsWith(".zip", StringComparison.Ordinal) && artifact.Filename != "extracted_rnode_firmware.zip")
            await RnodeconfFirmware.ExtractArchiveAsync(artifact, token).ConfigureAwait(false);
        var plan = RnodeconfFirmware.FlashPlan(platform, artifact, state, port,
            int.Parse(options.GetValueOrDefault("baud-flash") ?? "921600", CultureInfo.InvariantCulture),
            options.GetValueOrDefault("flasher") ?? throw new ArgumentException("Specify an independently installed --flasher executable."));
        var dataRoots = new[] { Path.Combine(state, "update"), Path.Combine(state, "extracted") };
        foreach (var argument in plan.Arguments)
            if (dataRoots.Any(root => argument.StartsWith(root, StringComparison.OrdinalIgnoreCase)) && !File.Exists(argument))
                throw new IOException("Required firmware component is missing.");
        await RnodeconfFirmware.RunToolAsync(plan, options.ContainsKey("authorize-flash") && options.ContainsKey("authorize-device-write"), token, services.ExecuteTool, services.ToolTimeout).ConfigureAwait(false);
    }

    private static async Task ExtractAsync(Dictionary<string, string?> options, RnodeconfServices services, string state, string port, CancellationToken token)
    {
        var executable = RnodeconfFirmware.ValidateFlasher(options.GetValueOrDefault("flasher") ??
            throw new ArgumentException("Specify an independently installed --flasher executable."), state);
        var folder = Path.Combine(state, "extracted");
        Directory.CreateDirectory(folder);
        var expectedPaths = new[] { "extracted_rnode_firmware.bootloader", "extracted_rnode_firmware.partitions", "extracted_rnode_firmware.boot_app0",
            "extracted_rnode_firmware.bin", "extracted_console_image.bin", "extracted_rnode_firmware.version" };
        if (expectedPaths.Any(name => File.Exists(Path.Combine(folder, name)))) throw new IOException("Extracted firmware already exists; refusing overwrite.");
        try
        {
            foreach (var (offset, length, filename) in new[]
            {
                ("0x1000", "0x4650", "extracted_rnode_firmware.bootloader"), ("0x8000", "0xC00", "extracted_rnode_firmware.partitions"),
                ("0xe000", "0x2000", "extracted_rnode_firmware.boot_app0"), ("0x10000", "0x200000", "extracted_rnode_firmware.bin"),
                ("0x210000", "0x1F0000", "extracted_console_image.bin")
            })
            {
                var path = Path.Combine(folder, filename);
                var call = new RnodeconfToolCall(executable,
                    ["--chip", "esp32", "--port", port, "--baud", options.GetValueOrDefault("baud-flash") ?? "921600",
                     "--before", "default_reset", "--after", "hard_reset", "read_flash", offset, length, path]);
                await RnodeconfFirmware.RunToolAsync(call, true, token, services.ExecuteTool, services.ToolTimeout).ConfigureAwait(false);
                if (!File.Exists(path) || new FileInfo(path).Length != Convert.ToInt64(length[2..], 16)) throw new IOException("Extraction tool did not produce the expected component length.");
            }
        }
        catch
        {
            foreach (var filename in expectedPaths)
            {
                var path = Path.Combine(folder, filename);
                if (File.Exists(path)) File.Delete(path);
            }
            throw;
        }
    }

    private static async Task<int> BootstrapAsync(Dictionary<string, string?> options, TextWriter output,
        Func<string, CancellationToken, ValueTask<Stream>> openSerial, RnodeconfServices services, string state, string port, CancellationToken token)
    {
        var product = ParseByte(options.GetValueOrDefault("product") ?? throw new ArgumentException("EEPROM bootstrap requires --product HEX."));
        var model = ParseByte(options.GetValueOrDefault("model") ?? throw new ArgumentException("EEPROM bootstrap requires --model HEX."));
        var revision = byte.Parse(options.GetValueOrDefault("hwrev") ?? throw new ArgumentException("EEPROM bootstrap requires --hwrev."), CultureInfo.InvariantCulture);
        if (revision == 0) throw new ArgumentException("Hardware revision must be 1..255.");
        using var rsa = RnodeconfSigning.LoadRsa(Path.Combine(state, "firmware", "signing.key"));
        await using var stream = await openSerial(port, token).ConfigureAwait(false);
        using var client = new RnodeconfClient(stream);
        await client.IdentifyAsync(output, token).ConfigureAwait(false);
        var eeprom = await client.ReadEepromAsync(token).ConfigureAwait(false);
        if (RnodeconfClient.IsProvisioned(eeprom))
        {
            await output.WriteLineAsync("EEPROM already provisioned; no changes made.").ConfigureAwait(false);
            return 0;
        }
        var counterPath = Path.Combine(state, "firmware", "serial.counter");
        Directory.CreateDirectory(Path.GetDirectoryName(counterPath)!);
        await using var counter = new FileStream(counterPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(counterPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (counter.Length > 32) throw new IOException("Invalid manufacturing serial counter.");
        var bytes = new byte[(int)counter.Length];
        await counter.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var serial = bytes.Length == 0 ? 1u : checked(uint.Parse(Encoding.UTF8.GetString(bytes), CultureInfo.InvariantCulture) + 1);
        var timestamp = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var commands = RnodeconfSigning.Bootstrap(product, model, revision, serial, timestamp, rsa);
        counter.Position = 0;
        counter.SetLength(0);
        await counter.WriteAsync(Encoding.ASCII.GetBytes(serial.ToString(CultureInfo.InvariantCulture)), token).ConfigureAwait(false);
        counter.Flush(flushToDisk: true); // Reserve before hardware writes; interrupted attempts never reuse a serial.
        foreach (var command in commands)
        {
            await client.WriteAuthorizedAsync([command], true, token).ConfigureAwait(false);
            await services.Delay(TimeSpan.FromMilliseconds(6), token).ConfigureAwait(false);
        }
        if (client.Platform == 0x70) await services.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        var readBack = await client.ReadEepromAsync(token).ConfigureAwait(false);
        if (!RnodeconfSigning.VerifyEeprom(readBack, rsa.ExportSubjectPublicKeyInfo())) throw new IOException("EEPROM bootstrap read-back/signature verification failed.");
        await output.WriteLineAsync($"EEPROM provisioned and read-back verified; serial {serial}.").ConfigureAwait(false);
        return 0;
    }

    private static async Task CompleteInstallationAsync(Dictionary<string, string?> options, TextWriter output,
        Func<string, CancellationToken, ValueTask<Stream>> openSerial, RnodeconfServices services, string state,
        string port, string? usbSerial, RnodeconfFirmwareArtifact? artifact, CancellationToken token)
    {
        byte platform;
        byte[] eeprom;
        byte[]? expectedHash = null;
        await using (var stream = await openSerial(port, token).ConfigureAwait(false))
        {
            using var client = new RnodeconfClient(stream);
            await client.IdentifyAsync(output, token).ConfigureAwait(false);
            platform = client.Platform;
            if (artifact is not null && decimal.Parse(client.FirmwareVersion, CultureInfo.InvariantCulture) != decimal.Parse(artifact.Version, CultureInfo.InvariantCulture))
                throw new IOException("Device firmware version after installation does not match selected version.");
            var hash = artifact is null ? null : await RnodeconfFirmware.PartitionHashAsync(platform, artifact, token).ConfigureAwait(false);
            expectedHash = hash;
            if (hash is not null)
            {
                await client.WriteAuthorizedAsync([RNodeProtocol.Frame(0x58, hash)], true, token).ConfigureAwait(false);
                await services.Delay(TimeSpan.FromSeconds(platform == 0x70 ? 5 : 1), token).ConfigureAwait(false);
            }
            await client.WriteAuthorizedAsync([RnodeconfCommands.Encode("reset")], true, token).ConfigureAwait(false);
        }
        await services.Delay(TimeSpan.FromSeconds(platform == 0x70 ? 20 : 7), token).ConfigureAwait(false);
        port = await ResolvePortAsync(port, usbSerial, services, token).ConfigureAwait(false);
        await using (var stream = await openSerial(port, token).ConfigureAwait(false))
        {
            using var client = new RnodeconfClient(stream);
            await client.IdentifyAsync(output, token).ConfigureAwait(false);
            eeprom = await client.ReadEepromAsync(token).ConfigureAwait(false);
            if (!RnodeconfClient.IsProvisioned(eeprom) || RnodeconfSigning.VerifyKnownSigner(eeprom, state) is null)
                throw new IOException("EEPROM signature/read-back verification after installation reset failed.");
            if (expectedHash is not null) await VerifyFirmwareHashesAsync(client, expectedHash, token).ConfigureAwait(false);
        }
        var serial = Convert.ToHexStringLower(eeprom.AsSpan(3, 4));
        var backup = Path.Combine(state, "firmware", "device_db", serial);
        if (!File.Exists(backup)) await RnodeconfSigning.SaveNewAsync(backup, eeprom, token).ConfigureAwait(false);
        else if (!(await File.ReadAllBytesAsync(backup, token).ConfigureAwait(false)).SequenceEqual(eeprom))
            throw new IOException("Existing manufacturing backup differs; refusing overwrite.");
        await output.WriteLineAsync($"Installation reset/version/EEPROM verification complete; device backup {backup}. RF operation is unverified.").ConfigureAwait(false);
    }

    private static async Task<string> ResolvePortAsync(string previousPort, string? usbSerial, RnodeconfServices services, CancellationToken token)
    {
        if (usbSerial is null) return previousPort;
        if (services.PortRecoveryTimeout <= TimeSpan.Zero || services.PortRecoveryTimeout > TimeSpan.FromSeconds(20))
            throw new ArgumentException("Invalid port recovery timeout.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(services.PortRecoveryTimeout);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) break;
            var matches = services.EnumeratePorts().Where(p => p.UsbSerial == usbSerial).ToArray();
            if (matches.Length > 1) throw new IOException("USB serial matches multiple ports; refusing ambiguous reconnect.");
            if (matches.Length == 1) return matches[0].Port;
            try { await services.Delay(TimeSpan.FromMilliseconds(500), deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
        }
        throw new IOException("USB serial device did not reappear within the recovery deadline.");
    }

    private static async Task VerifyFirmwareHashesAsync(RnodeconfClient client, byte[] expected, CancellationToken token)
    {
        var target = await client.RefreshFirmwareHashAsync(true, token).ConfigureAwait(false);
        var actual = await client.RefreshFirmwareHashAsync(false, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(expected, target) || !CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new IOException("Device target/actual firmware hashes after reset do not match the selected firmware partition.");
    }

    private static byte ParseByte(string hex) => byte.Parse(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static byte[] ParseHash(string hex)
    {
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) throw new ArgumentException("Firmware hash must be exactly 32 hexadecimal bytes.");
        return Convert.FromHexString(hex);
    }
}
