using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Applications.Rnodeconf;
using DotReticulum.Interfaces;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Unit")]
public sealed class RnodeconfTests
{
    // Independently executed RNS a29bfb51c468945c96a1ee22cae0114e993e2391,
    // GenerateRnodeconfVectors.py: capture-only serial adapter, no actual hardware.
    [Fact]
    public void CommandsMatchExecutedUpstreamVectors()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rnodeconf-vectors.json")));
        var cases = fixture.RootElement.GetProperty("cases");
        void Check(string name, byte[] actual) => Assert.Equal(cases.GetProperty(name)[0].GetString(), Convert.ToHexStringLower(actual));
        Check("detect", RnodeconfCommands.Detect());
        Check("wipe", RnodeconfCommands.Encode("eeprom-wipe"));
        Check("update-indicator", RnodeconfCommands.Encode("update-indicator"));
        Check("hash-write", RNodeProtocol.Frame(0x58, Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()));
        Check("eeprom-write", RNodeProtocol.Frame(0x52, [192, 219]));
        foreach (var (name, command, value) in new (string, string, string?)[]
        {
            ("normal", "normal", null), ("display", "display", "192"), ("timeout", "timeout", "219"),
            ("rotation", "rotation", "3"), ("display-address", "display-addr", "3c"),
            ("recondition", "recondition-display", null), ("neopixel", "np", "255"),
            ("ia-on", "ia-enable", null), ("ia-off", "ia-disable", null),
            ("bluetooth-on", "bluetooth-on", null), ("bluetooth-off", "bluetooth-off", null), ("bluetooth-pair", "bluetooth-pair", null),
            ("wifi-ap", "wifi", "AP"), ("wifi-channel", "channel", "14"), ("ssid", "ssid", "Tést"),
            ("ssid-delete", "ssid", "NONE"), ("psk", "psk", "test-only-password"), ("psk-delete", "psk", "NONE"),
            ("ip", "ip", "192.168.1.219"), ("ip-dhcp", "ip", "NONE"), ("netmask", "nm", "255.255.255.0"),
            ("reset", "reset", null), ("eeprom-read", "eeprom-read", null), ("config-read", "config-read", null)
        }) Check(name, RnodeconfCommands.Encode(command, value));
        Assert.Equal(cases.GetProperty("tnc").EnumerateArray().Select(v => v.GetString()),
            RnodeconfCommands.Tnc(868000000, 125000, 0, 7, 5, 0x70).Select(Convert.ToHexStringLower));
        Assert.Equal(8, RnodeconfCommands.Tnc(868000000, 125000, 0, 7, 5, 0x80).Count);
    }

    [Theory]
    [InlineData("ssid", "12345678901234567890123456789012345")]
    [InlineData("psk", "short")]
    [InlineData("channel", "15")]
    [InlineData("ip", "127.1")]
    [InlineData("ip", "192.168.1.256")]
    [InlineData("wifi", "unknown")]
    public void RejectsInvalidSettings(string command, string value) =>
        Assert.ThrowsAny<ArgumentException>(() => RnodeconfCommands.Encode(command, value));

    [Fact]
    public async Task AuthorizationAndValidationRunBeforeOpeningPort()
    {
        var opened = false;
        ValueTask<Stream> Open(string _, CancellationToken __) { opened = true; throw new InvalidOperationException(); }
        foreach (var args in new[]
        {
            new[] { "COM-NOT-USED", "--normal" },
            new[] { "COM-NOT-USED", "--authorize-device-write", "--ssid", "valid", "--channel", "99" },
            new[] { "COM-NOT-USED", "--authorize-device-write", "--tnc" },
            new[] { "COM-NOT-USED", "--authorize-device-write", "--normal", "--eeprom-backup" },
            new[] { "COM-NOT-USED", "--flash" }, new[] { "COM-NOT-USED", "--eeprom-wipe" },
            new[] { "COM-NOT-USED", "--freq", "868000000" }
        })
        {
            using var error = new StringWriter();
            Assert.Equal(1, await RnodeconfCli.RunAsync(args, TextWriter.Null, error, default, Open));
            Assert.False(opened);
            Assert.NotEmpty(error.ToString());
        }
    }

    [Fact]
    public async Task IdentificationDoesNotStartRadioOrWriteConfiguration()
    {
        using var simulator = new DeviceSimulator();
        using var output = new StringWriter();
        Assert.Equal(0, await Run(simulator, ["SIM", "--info"], output));
        Assert.Contains("1.86", output.ToString());
        Assert.Contains("not authentication", output.ToString());
        Assert.Equal(2, simulator.Writes.Count);
        Assert.Equal(RnodeconfCommands.Detect(), simulator.Writes[0]);
        Assert.True(simulator.Disposed);
    }

    [Fact]
    public async Task ConfigurationMasksCredentialUnlessExplicitlyRequested()
    {
        foreach (var reveal in new[] { false, true })
        {
            using var simulator = new DeviceSimulator();
            using var output = new StringWriter();
            Assert.Equal(0, await Run(simulator, reveal ? ["SIM", "--config", "--show-psk"] : ["SIM", "--config"], output));
            Assert.Equal(reveal, output.ToString().Contains("test-only-password", StringComparison.Ordinal));
            Assert.Contains("192.168.1.219", output.ToString());
            Assert.Equal(3, simulator.Writes.Count);
        }
    }

    [Fact]
    public async Task AuthorizedSettingsRequireValidEepromAndHaveSourceOrder()
    {
        using var simulator = new DeviceSimulator();
        Assert.Equal(0, await Run(simulator, ["SIM", "--authorize-device-write", "--wifi", "AP", "--display", "300", "--ssid", "NONE"]));
        Assert.Equal(RnodeconfCommands.Encode("display", "255"), simulator.Writes[2]);
        Assert.Equal(RnodeconfCommands.Encode("ssid", "NONE"), simulator.Writes[3]);
        Assert.Equal(RnodeconfCommands.Encode("wifi", "AP"), simulator.Writes[4]);
        using var invalid = new DeviceSimulator();
        invalid.Eeprom[0] ^= 1;
        Assert.Equal(1, await Run(invalid, ["SIM", "--authorize-device-write", "--normal"]));
        Assert.Equal(2, invalid.Writes.Count);
    }

    [Fact]
    public async Task TncZeroPowerIsAcceptedWithoutPromptAndStartsOnlyWhenAuthorized()
    {
        using var simulator = new DeviceSimulator();
        Assert.Equal(0, await Run(simulator,
            ["SIM", "--authorize-device-write", "--tnc", "--freq", "868000000", "--bw", "125000", "--txp", "0", "--sf", "7", "--cr", "5"]));
        Assert.Equal(9, simulator.Writes.Count);
        Assert.Equal(new byte[] { 0xc0, 3, 0, 0xc0 }, simulator.Writes[4]);
    }

    [Fact]
    public async Task BackupIsExactAndNeverOverwrites()
    {
        var folder = OwnedDirectory();
        try
        {
            var path = Path.Combine(folder, "device.eeprom");
            using var first = new DeviceSimulator();
            Assert.Equal(0, await Run(first, ["SIM", "--eeprom-backup", "--backup-file", path]));
            Assert.Equal(first.Eeprom, await File.ReadAllBytesAsync(path));
            using var second = new DeviceSimulator();
            second.Eeprom[1] = 255;
            Assert.Equal(1, await Run(second, ["SIM", "--eeprom-backup", "--backup-file", path]));
            Assert.Equal(first.Eeprom, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task TrustEnrollmentRequiresAuthorizationAndStoresDerBySha256()
    {
        var folder = OwnedDirectory();
        try
        {
            using var rsa = RSA.Create(1024);
            var der = rsa.ExportSubjectPublicKeyInfo();
            var args = new[] { "--trust-key", Convert.ToHexString(der), "--state-dir", folder };
            Assert.Equal(1, await RnodeconfCli.RunAsync(args, TextWriter.Null, TextWriter.Null));
            Assert.Empty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
            Assert.Equal(0, await RnodeconfCli.RunAsync([.. args, "--authorize-trust"], TextWriter.Null, TextWriter.Null));
            var path = Path.Combine(folder, "trusted_keys", Convert.ToHexStringLower(SHA256.HashData(der)) + ".pubkey");
            Assert.Equal(der, await File.ReadAllBytesAsync(path));
            Assert.Equal(1, await RnodeconfCli.RunAsync([.. args, "--authorize-trust"], TextWriter.Null, TextWriter.Null));
        }

        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void EepromSignatureMatchesIndependentPythonMaxLengthPssAndUpstreamParser()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rnodeconf-signing-vectors.json")));
        var data = fixture.RootElement;
        var eeprom = Convert.FromHexString(data.GetProperty("eeprom").GetString()!);
        var publicKey = Convert.FromHexString(data.GetProperty("public_key").GetString()!);
        Assert.True(RnodeconfSigning.VerifyEeprom(eeprom, publicKey));
        eeprom[154] ^= 1;
        Assert.False(RnodeconfSigning.VerifyEeprom(eeprom, publicKey));
        eeprom[154] ^= 1;
        eeprom[0] ^= 1;
        Assert.False(RnodeconfSigning.VerifyEeprom(eeprom, publicKey));
    }

    [Fact]
    public async Task MissingMalformedAndHardwareErrorResponsesFailBoundedly()
    {
        using var noResponse = new DeviceSimulator { Answer = false };
        var client = new RnodeconfClient(noResponse, TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAsync<TimeoutException>(() => client.IdentifyAsync(TextWriter.Null, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.IdentifyAsync(TextWriter.Null, cancelled.Token));
        using var hardwareError = new DeviceSimulator { HardwareError = true };
        await Assert.ThrowsAsync<IOException>(() => new RnodeconfClient(hardwareError).IdentifyAsync(TextWriter.Null, default));
        using var truncated = new DeviceSimulator { Truncated = true };
        await Assert.ThrowsAsync<TimeoutException>(() => new RnodeconfClient(truncated, TimeSpan.FromMilliseconds(40)).IdentifyAsync(TextWriter.Null, default));
    }

    [Theory]
    [InlineData("rnir")]
    [InlineData("rnpkg")]
    public async Task PlaceholdersHaveNoInventedResolverOrPackageCommands(string tool)
    {
        using var output = new StringWriter();
        Assert.Equal(0, await UpstreamPlaceholderCli.RunAsync(tool, ["--help"], output, TextWriter.Null));
        Assert.Contains("No resolution/package operations", output.ToString());
        Assert.Equal(1, await UpstreamPlaceholderCli.RunAsync(tool, ["install", "example"], output, TextWriter.Null));
        if (tool == "rnpkg")
        {
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await UpstreamPlaceholderCli.RunAsync(tool, ["--exampleconfig"], output, TextWriter.Null));
            Assert.Equal("# This is an example package manager configuration file.\n\n", output.ToString().Replace("\r", "", StringComparison.Ordinal));
        }
    }

    private static Task<int> Run(DeviceSimulator simulator, string[] args, TextWriter? output = null) =>
        RnodeconfCli.RunAsync(args, output ?? TextWriter.Null, TextWriter.Null, default, (_, _) => ValueTask.FromResult<Stream>(simulator));

    private static string OwnedDirectory()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "rnodeconf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class DeviceSimulator : Stream
    {
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        public List<byte[]> Writes { get; } = [];
        public byte[] Eeprom { get; init; } = CreateEeprom();
        public bool Answer { get; init; } = true;
        public bool HardwareError { get; init; }
        public bool Truncated { get; init; }
        public byte Platform { get; init; } = 0x70;
        public byte FirmwareMinor { get; init; } = 86;
        public byte[] FirmwareImageHash { get; init; } = new byte[32];
        public byte[]? ActualFirmwareHashOverride { get; init; }
        public byte? FailEepromAddress { get; init; }
        public bool Disposed { get; private set; }
        private static byte[] CreateEeprom()
        {
            var bytes = new byte[200];
            bytes[0x9b] = 0x73;
            bytes[0xb0] = 0x73;
            bytes[0xba] = 1;
            bytes[0xbb] = 6;
            MD5.HashData(bytes.AsSpan(0, 11)).CopyTo(bytes, 11);
            return bytes;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(buffer.ToArray());
            if (!Answer) return ValueTask.CompletedTask;
            byte[]? response = null;
            if (buffer.Span.SequenceEqual(RnodeconfCommands.Detect()))
            {
                response = HardwareError ? RNodeProtocol.Frame(0x90, [2]) : Truncated ? [0xc0, 0x50, 1] :
                [.. RNodeProtocol.Frame(8, [0x46]), .. RNodeProtocol.Frame(0x50, [1, FirmwareMinor]),
                 .. RNodeProtocol.Frame(0x48, [Platform]), .. RNodeProtocol.Frame(0x49, [0x71]), .. RNodeProtocol.Frame(0x47, [0x3a]),
                 .. RNodeProtocol.Frame(0x56, new byte[32]),
                 .. RNodeProtocol.Frame(0x60, [1, .. FirmwareImageHash]), .. RNodeProtocol.Frame(0x60, [2, .. (ActualFirmwareHashOverride ?? FirmwareImageHash)])];
            }
            else if (buffer.Span.SequenceEqual(RnodeconfCommands.Encode("eeprom-read"))) response = RNodeProtocol.Frame(0x51, Eeprom);
            else if (buffer.Span.SequenceEqual(RnodeconfCommands.Encode("config-read")))
            {
                var config = new byte[74];
                Encoding.UTF8.GetBytes("test-network").CopyTo(config, 0);
                Encoding.UTF8.GetBytes("test-only-password").CopyTo(config, 33);
                new byte[] { 192, 168, 1, 219, 255, 255, 255, 0 }.CopyTo(config, 66);
                response = RNodeProtocol.Frame(0x6d, config);
            }
            else
            {
                foreach (var frame in new RNodeFrameDecoder().Feed(buffer.Span))
                {
                    if (frame.Command == 0x52 && frame.Payload.Length == 2)
                    {
                        if (FailEepromAddress == frame.Payload[0]) throw new IOException("Captured EEPROM write interruption.");
                        Eeprom[frame.Payload[0]] = frame.Payload[1];
                    }
                    if (frame.Command == 0x59 && frame.Payload.SequenceEqual(new byte[] { 0xf8 }))
                        Array.Clear(Eeprom);
                    if (frame.Command == 0x58 && frame.Payload.Length == 32)
                        frame.Payload.CopyTo(FirmwareImageHash, 0);
                    if (frame.Command == 0x60 && frame.Payload.Length == 1 && frame.Payload[0] is 1 or 2)
                        response = RNodeProtocol.Frame(0x60, [frame.Payload[0], .. (frame.Payload[0] == 2 ? ActualFirmwareHashOverride ?? FirmwareImageHash : FirmwareImageHash)]);
                    if (frame.Command == 0x46 && frame.Payload.SequenceEqual(new byte[] { 2 }))
                        response = RNodeProtocol.Frame(0x62, [0, 1, 0xe2, 0x40]);
                }
            }
            if (response is not null) _input.Writer.TryWrite(response);
            return ValueTask.CompletedTask;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytes = await _input.Reader.ReadAsync(cancellationToken);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
        protected override void Dispose(bool disposing) { Disposed = true; _input.Writer.TryComplete(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task KeyGenerationAndBootstrapArePersistentSourceCompatibleAndNoOverwrite()
    {
        var folder = OwnedDirectory();
        try
        {
            await RnodeconfSigning.GenerateKeysAsync(folder, default);
            var privatePath = Path.Combine(folder, "firmware", "signing.key");
            var original = await File.ReadAllBytesAsync(privatePath);
            await RnodeconfSigning.GenerateKeysAsync(folder, default);
            Assert.Equal(original, await File.ReadAllBytesAsync(privatePath));
            using var simulator = new DeviceSimulator();
            simulator.Eeprom[0x9b] = 0;
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await RnodeconfCli.RunWithServicesAsync(
                ["SIM", "--rom", "--authorize-device-write", "--authorize-eeprom", "--state-dir", folder,
                 "--product", "10", "--model", "12", "--hwrev", "1"],
                output, error, default, (_, _) => ValueTask.FromResult<Stream>(new DeviceSimulator { Eeprom = simulator.Eeprom }),
                new RnodeconfServices { Delay = (_, _) => Task.CompletedTask });
            Assert.True(code == 0, error.ToString());
            Assert.Contains("read-back verified", output.ToString());
            using var key = RnodeconfSigning.LoadRsa(privatePath);
            Assert.True(RnodeconfSigning.VerifyEeprom(simulator.Eeprom, key.ExportSubjectPublicKeyInfo()));
            Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(folder, "firmware", "serial.counter")));
            Assert.Equal((byte)0x73, simulator.Eeprom[155]);
            simulator.Eeprom[27] ^= 1;
            Assert.False(RnodeconfSigning.VerifyEeprom(simulator.Eeprom, key.ExportSubjectPublicKeyInfo()));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task AdvancedDestructiveOptionsRequireTheirExplicitAuthorizationBeforeOpen()
    {
        var opened = false;
        ValueTask<Stream> Open(string _, CancellationToken __) { opened = true; throw new InvalidOperationException(); }
        foreach (var option in new[] { "--update", "--flash", "--autoinstall", "--extract", "--rom", "--eeprom-wipe", "--sign", "--firmware-hash" })
        {
            var args = option == "--firmware-hash" ? new[] { "SIM", option, new string('0', 64) } : ["SIM", option];
            Assert.Equal(1, await RnodeconfCli.RunAsync(args, TextWriter.Null, TextWriter.Null, default, Open));
            Assert.False(opened);
        }
    }

    [Fact]
    public async Task FirmwareHashQueryAndSignatureCommandsUseSimulatorOnly()
    {
        var folder = OwnedDirectory();
        try
        {
            await RnodeconfSigning.GenerateKeysAsync(folder, default);
            using var query = new DeviceSimulator();
            using var output = new StringWriter();
            Assert.Equal(0, await Run(query, ["SIM", "-K", "-L"], output));
            Assert.Contains(new string('0', 64), output.ToString());
            using var sign = new DeviceSimulator();
            Assert.Equal(0, await Run(sign, ["SIM", "--sign", "--state-dir", folder, "--authorize-device-write"]));
            var frame = Assert.Single(new RNodeFrameDecoder().Feed(sign.Writes.Last()));
            Assert.Equal((byte)0x57, frame.Command);
            Assert.Equal(64, frame.Payload.Length);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task UpdateUsesCapturedInstalledToolReopensDeviceAndVerifiesReportedVersion()
    {
        var folder = OwnedDirectory();
        try
        {
            byte[] archiveBytes;
            using (var bytes = new MemoryStream())
            {
                using (var archive = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                {
                    using var part = archive.CreateEntry("rnode_firmware_rak4631.bin").Open();
                    part.Write([1, 2, 3, 4]);
                }
                archiveBytes = bytes.ToArray();
            }
            var digest = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
            using var http = new HttpClient(new FirmwareHttpHandler(archiveBytes, digest));
            using var before = new DeviceSimulator();
            using var after = new DeviceSimulator { FirmwareMinor = 87, FirmwareImageHash = SHA256.HashData(new byte[] { 1, 2, 3, 4 }) };
            foreach (var device in new[] { before, after })
            {
                device.Eeprom[0] = 0x10;
                device.Eeprom[1] = 0x12;
                MD5.HashData(device.Eeprom.AsSpan(0, 11)).CopyTo(device.Eeprom, 11);
            }
            var opened = 0;
            var selectedPorts = new List<string>();
            var toolCalled = false;
            var services = new RnodeconfServices
            {
                Http = http, Delay = (_, _) => Task.CompletedTask,
                EnumeratePorts = () => opened == 0 ? [new("SIM", "stable-usb-id", "simulator")] : [new("SIM-RECONNECTED", "stable-usb-id", "simulator")],
                ExecuteTool = (plan, _) =>
                {
                    Assert.True(before.Disposed);
                    Assert.Contains("dfu", plan.Arguments);
                    Assert.DoesNotContain(plan.Arguments, a => a.EndsWith(".py", StringComparison.Ordinal));
                    toolCalled = true;
                    return Task.FromResult(0);
                }
            };
            using var output = new StringWriter();
            using var error = new StringWriter();
            var result = await RnodeconfCli.RunWithServicesAsync(
                ["SIM", "--update", "--authorize-device-write", "--authorize-flash", "--authorize-firmware-download",
                 "--authorize-unverified-device", "--state-dir", folder, "--flasher", Environment.ProcessPath!],
                output, error, default, (name, _) =>
                {
                    selectedPorts.Add(name);
                    return ValueTask.FromResult<Stream>(opened++ == 0 ? before : after);
                }, services);
            Assert.True(result == 0, error.ToString());
            Assert.True(toolCalled);
            Assert.Equal(2, opened);
            Assert.Equal(new[] { "SIM", "SIM-RECONNECTED" }, selectedPorts);
            Assert.Contains("reported selected version", output.ToString());
            Assert.Contains(before.Writes, bytes => bytes.SequenceEqual(RnodeconfCommands.Encode("update-indicator")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ExtractionCapturesFiveExactSourcePartsWithoutExecutingAnyFirmware()
    {
        var folder = OwnedDirectory();
        try
        {
            using var simulator = new DeviceSimulator { Platform = 0x80 };
            var calls = new List<RnodeconfToolCall>();
            var services = new RnodeconfServices
            {
                Delay = (_, _) => Task.CompletedTask,
                ExecuteTool = (plan, _) =>
                {
                    calls.Add(plan);
                    var path = plan.Arguments.Last();
                    var length = Convert.ToInt64(plan.Arguments[^2][2..], 16);
                    using var stream = File.Create(path);
                    stream.SetLength(length);
                    return Task.FromResult(0);
                }
            };
            using var error = new StringWriter();
            var result = await RnodeconfCli.RunWithServicesAsync(
                ["SIM", "--extract", "--authorize-device-write", "--authorize-flash", "--authorize-unverified-device",
                 "--state-dir", folder, "--flasher", Environment.ProcessPath!],
                TextWriter.Null, error, default, (_, _) => ValueTask.FromResult<Stream>(simulator), services);
            Assert.True(result == 0, error.ToString());
            Assert.Equal(5, calls.Count);
            Assert.Equal(new[] { "0x1000", "0x8000", "0xe000", "0x10000", "0x210000" }, calls.Select(c => c.Arguments[^3]));
            Assert.All(calls, call => Assert.Contains("read_flash", call.Arguments));
            Assert.True(File.Exists(Path.Combine(folder, "extracted", "extracted_rnode_firmware.version")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task AutoinstallCapturesFlasherThenWipesUnknownEepromAndBootstrapsReadBack()
    {
        var folder = OwnedDirectory();
        try
        {
            byte[] package;
            using (var bytes = new MemoryStream())
            {
                using (var archive = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                {
                    using var part = archive.CreateEntry("rnode_firmware_rak4631.bin").Open();
                    part.Write([1, 2, 3]);
                }
                package = bytes.ToArray();
            }
            using var http = new HttpClient(new FirmwareHttpHandler(package, Convert.ToHexStringLower(SHA256.HashData(package))));
            var sharedEeprom = new byte[200];
            var sharedFirmwareHash = new byte[32];
            var devices = new List<DeviceSimulator>();
            var flashed = false;
            var services = new RnodeconfServices
            {
                Http = http, Delay = (_, _) => Task.CompletedTask,
                ExecuteTool = (_, _) => { flashed = true; return Task.FromResult(0); }
            };
            ValueTask<Stream> Open(string _, CancellationToken __)
            {
                Assert.True(flashed);
                var device = new DeviceSimulator { Eeprom = sharedEeprom, FirmwareMinor = 87, FirmwareImageHash = sharedFirmwareHash };
                devices.Add(device);
                return ValueTask.FromResult<Stream>(device);
            }
            using var output = new StringWriter();
            using var error = new StringWriter();
            var result = await RnodeconfCli.RunWithServicesAsync(
                ["SIM", "--autoinstall", "--authorize-device-write", "--authorize-eeprom", "--authorize-key-write",
                 "--authorize-flash", "--authorize-firmware-download", "--state-dir", folder, "--flasher", Environment.ProcessPath!,
                 "--platform", "70", "--product", "10", "--model", "12", "--hwrev", "1"],
                output, error, default, Open, services);
            Assert.True(result == 0, error.ToString());
            Assert.Equal(4, devices.Count);
            Assert.All(devices, device => Assert.True(device.Disposed));
            Assert.Contains(devices[0].Writes, frame => frame.SequenceEqual(RnodeconfCommands.Encode("eeprom-wipe")));
            Assert.True(RnodeconfClient.IsProvisioned(sharedEeprom));
            Assert.Contains("read-back verified", output.ToString());
            Assert.Contains("Installation reset/version/EEPROM verification complete", output.ToString());
            Assert.True(File.Exists(Path.Combine(folder, "firmware", "device_db", "00000001")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task MixedQueryAndMutationOrOrphanFirmwareParametersAreRejectedBeforeOpen()
    {
        var opened = false;
        ValueTask<Stream> Open(string _, CancellationToken __) { opened = true; throw new InvalidOperationException(); }
        foreach (var args in new[]
        {
            new[] { "SIM", "-K", "--sign" },
            new[] { "SIM", "--fw-url", "https://firmware.invalid/" },
            new[] { "SIM", "--key", "--sign", "--authorize-key-write" },
            new[] { "SIM", "--update", "--authorize-device-write", "--authorize-flash", "--flasher", "missing-flasher" }
        })
        {
            Assert.Equal(1, await RnodeconfCli.RunAsync(args, TextWriter.Null, TextWriter.Null, default, Open));
            Assert.False(opened);
        }
    }

    [Fact]
    public async Task ModelAndUsbSelectionAreNativeAndAmbiguityFailsBeforePortAccess()
    {
        using var output = new StringWriter();
        var services = new RnodeconfServices
        {
            EnumeratePorts = () => [new("SIM-NEW", "owned-usb-serial", "simulator")]
        };
        using var device = new DeviceSimulator();
        var opened = "";
        Assert.Equal(0, await RnodeconfCli.RunWithServicesAsync(["--list-models"], output, TextWriter.Null, default,
            (_, _) => throw new InvalidOperationException(), services));
        Assert.Contains("rnode_firmware_rak4631.zip", output.ToString());
        Assert.Equal(0, await RnodeconfCli.RunWithServicesAsync(["--usb-serial", "owned-usb-serial", "--info"], output, TextWriter.Null, default,
            (name, _) => { opened = name; return ValueTask.FromResult<Stream>(device); }, services));
        Assert.Equal("SIM-NEW", opened);
        Assert.Contains("Device info:", output.ToString());
        services = new RnodeconfServices
        {
            EnumeratePorts = () => [new("SIM-A", "duplicate", "simulator"), new("SIM-B", "duplicate", "simulator")]
        };
        Assert.Equal(1, await RnodeconfCli.RunWithServicesAsync(["--usb-serial", "duplicate", "--info"], TextWriter.Null, TextWriter.Null, default,
            (_, _) => throw new InvalidOperationException(), services));
    }

    [Fact]
    public async Task FailedExtractionRollsBackOwnedPartialPartsAndCanRetry()
    {
        var folder = OwnedDirectory();
        try
        {
            var calls = 0;
            var services = new RnodeconfServices
            {
                EnumeratePorts = () => [],
                ExecuteTool = (plan, _) =>
                {
                    using var file = File.Create(plan.Arguments.Last());
                    file.SetLength(Convert.ToInt64(plan.Arguments[^2][2..], 16));
                    return Task.FromResult(++calls == 3 ? 7 : 0);
                }
            };
            using var simulator = new DeviceSimulator { Platform = 0x80 };
            var args = new[] { "SIM", "--extract", "--authorize-device-write", "--authorize-flash", "--authorize-unverified-device",
                "--state-dir", folder, "--flasher", Environment.ProcessPath! };
            Assert.Equal(1, await RnodeconfCli.RunWithServicesAsync(args, TextWriter.Null, TextWriter.Null, default,
                (_, _) => ValueTask.FromResult<Stream>(simulator), services));
            Assert.Empty(Directory.GetFiles(Path.Combine(folder, "extracted")));
            using var retry = new DeviceSimulator { Platform = 0x80 };
            Assert.Equal(0, await RnodeconfCli.RunWithServicesAsync(args, TextWriter.Null, TextWriter.Null, default,
                (_, _) => ValueTask.FromResult<Stream>(retry), services));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ExtractedFirmwareReuseStaysOfflineAndUsesExactInstalledEspToolPlan()
    {
        var folder = OwnedDirectory();
        try
        {
            var extracted = Path.Combine(folder, "extracted");
            Directory.CreateDirectory(extracted);
            await File.WriteAllTextAsync(Path.Combine(extracted, "extracted_rnode_firmware.version"), "1.87 " + new string('0', 64));
            foreach (var name in new[] { "extracted_rnode_firmware.bin", "extracted_rnode_firmware.bootloader",
                "extracted_rnode_firmware.boot_app0", "extracted_rnode_firmware.partitions", "extracted_console_image.bin" })
                await File.WriteAllBytesAsync(Path.Combine(extracted, name), [1, 2, 3]);
            using var before = new DeviceSimulator { Platform = 0x80 };
            using var after = new DeviceSimulator { Platform = 0x80, FirmwareMinor = 87 };
            var opened = 0;
            var executed = false;
            using var http = new HttpClient(new RejectNetworkHandler());
            var services = new RnodeconfServices
            {
                Http = http, Delay = (_, _) => Task.CompletedTask, EnumeratePorts = () => [],
                ExecuteTool = (call, _) =>
                {
                    executed = true;
                    Assert.Contains("0x210000", call.Arguments);
                    Assert.Contains(Path.Combine(extracted, "extracted_rnode_firmware.bin"), call.Arguments);
                    Assert.DoesNotContain(call.Arguments, value => value.EndsWith(".py", StringComparison.Ordinal));
                    return Task.FromResult(0);
                }
            };
            using var error = new StringWriter();
            var result = await RnodeconfCli.RunWithServicesAsync(
                ["SIM", "--update", "--use-extracted", "--authorize-device-write", "--authorize-flash",
                 "--authorize-unverified-device", "--state-dir", folder, "--flasher", Environment.ProcessPath!],
                TextWriter.Null, error, default, (_, _) => ValueTask.FromResult<Stream>(opened++ == 0 ? before : after), services);
            Assert.True(result == 0, error.ToString());
            Assert.True(executed);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ParserSyntaxMatchesIndependentUpstreamVectorsAndFailuresUseArgparseExitCode()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rnodeconf-cli-vectors.json")));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var args = item.GetProperty("args").EnumerateArray().Select(v => v.GetString()!).ToArray();
            var normalized = RnodeconfArguments.Normalize(args);
            if (item.GetProperty("code").GetInt32() != 0)
            {
                Assert.Equal(2, await RnodeconfCli.RunAsync(args, TextWriter.Null, TextWriter.Null));
                continue;
            }
            Assert.DoesNotContain(normalized, value => value.StartsWith("--", StringComparison.Ordinal) && value.Contains('='));
        }
        using var simulator = new DeviceSimulator();
        Assert.Equal(2, await RnodeconfCli.RunAsync(["--info=true"], TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, await RnodeconfCli.RunAsync(["-ih"], TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, await RnodeconfCli.RunAsync(["--info", "--version"], TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, await Run(simulator, ["SIM", "--authorize-device-write", "-b", "-D192", "--ssid=Tést"]));
        Assert.Contains(simulator.Writes, bytes => bytes.SequenceEqual(RnodeconfCommands.Encode("display", "192")));
        Assert.Contains(simulator.Writes, bytes => bytes.SequenceEqual(RnodeconfCommands.Encode("ssid", "Tést")));
        using var clustered = new DeviceSimulator();
        Assert.Equal(0, await Run(clustered, ["SIM", "--authorize-device-write", "-ib"]));
        Assert.Contains(clustered.Writes, bytes => bytes.SequenceEqual(RnodeconfCommands.Encode("bluetooth-on")));
        using var pairing = new DeviceSimulator();
        using var output = new StringWriter();
        Assert.Equal(0, await Run(pairing, ["SIM", "--authorize-device-write", "-p"], output));
        Assert.Contains("123456", output.ToString());
    }

    [Fact]
    public async Task PostResetFirmwareHashMismatchFailsInsteadOfReportingSuccess()
    {
        var folder = OwnedDirectory();
        try
        {
            var extracted = Path.Combine(folder, "extracted");
            Directory.CreateDirectory(extracted);
            await File.WriteAllTextAsync(Path.Combine(extracted, "extracted_rnode_firmware.version"), "1.87 " + new string('0', 64));
            foreach (var name in new[] { "extracted_rnode_firmware.bin", "extracted_rnode_firmware.bootloader",
                "extracted_rnode_firmware.boot_app0", "extracted_rnode_firmware.partitions", "extracted_console_image.bin" })
                await File.WriteAllBytesAsync(Path.Combine(extracted, name), [1, 2, 3]);
            using var before = new DeviceSimulator { Platform = 0x80 };
            using var after = new DeviceSimulator { Platform = 0x80, FirmwareMinor = 87, ActualFirmwareHashOverride = Enumerable.Repeat((byte)1, 32).ToArray() };
            var opened = 0;
            using var output = new StringWriter();
            using var error = new StringWriter();
            var result = await RnodeconfCli.RunWithServicesAsync(
                ["SIM", "--update", "--use-extracted", "--authorize-device-write", "--authorize-flash",
                 "--authorize-unverified-device", "--state-dir", folder, "--flasher", Environment.ProcessPath!],
                output, error, default, (_, _) => ValueTask.FromResult<Stream>(opened++ == 0 ? before : after),
                new RnodeconfServices { EnumeratePorts = () => [], Delay = (_, _) => Task.CompletedTask, ExecuteTool = (_, _) => Task.FromResult(0) });
            Assert.Equal(1, result);
            Assert.Contains("target/actual firmware hashes", error.ToString());
            Assert.DoesNotContain("device reported selected version", output.ToString());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task InterruptedManufacturingReservesSerialThenRetriesAndBacksUpOnlyVerifiedImage()
    {
        var folder = OwnedDirectory();
        try
        {
            await RnodeconfSigning.GenerateKeysAsync(folder, default);
            var shared = new byte[200];
            var failing = true;
            ValueTask<Stream> Open(string _, CancellationToken __) => ValueTask.FromResult<Stream>(
                new DeviceSimulator { Eeprom = shared, FailEepromAddress = failing ? (byte)40 : null });
            var args = new[] { "SIM", "--rom", "--authorize-device-write", "--authorize-eeprom", "--state-dir", folder,
                "--product", "10", "--model", "12", "--hwrev", "1" };
            var services = new RnodeconfServices { EnumeratePorts = () => [], Delay = (_, _) => Task.CompletedTask };
            Assert.Equal(1, await RnodeconfCli.RunWithServicesAsync(args, TextWriter.Null, TextWriter.Null, default, Open, services));
            Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(folder, "firmware", "serial.counter")));
            Assert.False(RnodeconfClient.IsProvisioned(shared));
            Assert.False(Directory.Exists(Path.Combine(folder, "firmware", "device_db")));
            failing = false;
            Assert.Equal(0, await RnodeconfCli.RunWithServicesAsync(args, TextWriter.Null, TextWriter.Null, default, Open, services));
            Assert.Equal("2", await File.ReadAllTextAsync(Path.Combine(folder, "firmware", "serial.counter")));
            Assert.True(File.Exists(Path.Combine(folder, "firmware", "device_db", "00000002")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrAmbiguousUsbRecoveryFailsWithoutReportingUpdateSuccess(bool ambiguous)
    {
        var folder = OwnedDirectory();
        try
        {
            var extracted = Path.Combine(folder, "extracted");
            Directory.CreateDirectory(extracted);
            await File.WriteAllTextAsync(Path.Combine(extracted, "extracted_rnode_firmware.version"), "1.87 " + new string('0', 64));
            foreach (var name in new[] { "extracted_rnode_firmware.bin", "extracted_rnode_firmware.bootloader",
                "extracted_rnode_firmware.boot_app0", "extracted_rnode_firmware.partitions", "extracted_console_image.bin" })
                await File.WriteAllBytesAsync(Path.Combine(extracted, name), [1, 2, 3]);
            using var simulator = new DeviceSimulator { Platform = 0x80 };
            var opened = false;
            var delays = 0;
            var services = new RnodeconfServices
            {
                EnumeratePorts = () => !opened ? [new("SIM", "owned-usb", "simulator")] :
                    ambiguous ? [new("SIM-A", "owned-usb", "simulator"), new("SIM-B", "owned-usb", "simulator")] : [],
                Delay = (_, _) => { delays++; return Task.CompletedTask; },
                ExecuteTool = (_, _) => Task.FromResult(0)
            };
            using var output = new StringWriter();
            using var error = new StringWriter();
            var result = await RnodeconfCli.RunWithServicesAsync(
                ["SIM", "--update", "--use-extracted", "--authorize-device-write", "--authorize-flash",
                 "--authorize-unverified-device", "--state-dir", folder, "--flasher", Environment.ProcessPath!],
                output, error, default, (_, _) => { opened = true; return ValueTask.FromResult<Stream>(simulator); }, services);
            Assert.Equal(1, result);
            Assert.Contains(ambiguous ? "ambiguous reconnect" : "recovery deadline", error.ToString());
            Assert.InRange(delays, 2, 43);
            Assert.DoesNotContain("device reported selected version", output.ToString());
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Extracted firmware reuse must not access network.");
    }

    private sealed class FirmwareHttpHandler(byte[] archive, string hash) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("release.json", StringComparison.Ordinal)
                    ? new StringContent($"{{\"rnode_firmware_rak4631.zip\":{{\"version\":\"1.87\",\"hash\":\"{hash}\"}}}}")
                    : new ByteArrayContent(archive)
            });
    }
}
