using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rnodeconf;

namespace DotReticulum.Applications.Tests;

public sealed class RnodeconfFirmwareTests
{
    [Fact]
    public async Task FirmwareAcquisitionVerifiesHashCachesAndSupportsOfflineRestart()
    {
        var directory = OwnedDirectory();
        try
        {
            var content = Encoding.UTF8.GetBytes("capture-only firmware payload; never executed");
            var hash = Convert.ToHexStringLower(SHA256.HashData(content));
            var calls = new List<string>();
            using var http = new HttpClient(new Handler(request =>
            {
                calls.Add(request.RequestUri!.AbsoluteUri);
                return request.RequestUri.AbsolutePath.EndsWith("release.json", StringComparison.Ordinal)
                    ? new StringContent($"{{\"rnode_firmware_rak4631.zip\":{{\"version\":\"1.86\",\"hash\":\"{hash}\"}}}}")
                    : new ByteArrayContent(content);
            }));
            var service = new RnodeconfFirmware(http, directory);
            var artifact = await service.PrepareAsync("rnode_firmware_rak4631.zip", null, new Uri("https://firmware.invalid/releases/"), false, true, default);
            Assert.Equal(content, await File.ReadAllBytesAsync(artifact.Path));
            Assert.Equal(new[] { "https://firmware.invalid/releases/latest/download/release.json", "https://firmware.invalid/releases/download/1.86/rnode_firmware_rak4631.zip" }, calls);
            calls.Clear();
            var restarted = await new RnodeconfFirmware(http, directory).PrepareAsync(artifact.Filename, "1.86", null, true, false, default);
            Assert.Equal(artifact, restarted);
            Assert.Empty(calls);
            await File.WriteAllTextAsync(artifact.Path, "corrupt");
            await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync(artifact.Filename, "1.86", null, true, false, default));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RejectsFirmwareHashMismatchMissingMetadataAndUnauthorizedNetwork()
    {
        var directory = OwnedDirectory();
        try
        {
            var calls = 0;
            using var http = new HttpClient(new Handler(request =>
            {
                calls++;
                return request.RequestUri!.AbsolutePath.EndsWith("release.json", StringComparison.Ordinal)
                    ? new StringContent($"{{\"firmware.zip\":{{\"version\":\"1.86\",\"hash\":\"{new string('0', 64)}\"}}}}")
                    : new ByteArrayContent([1, 2, 3]);
            }));
            var service = new RnodeconfFirmware(http, directory);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PrepareAsync("firmware.zip", null, null, false, false, default));
            Assert.Equal(0, calls);
            await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync("firmware.zip", null, null, true, true, default));
            await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync("firmware.zip", "1.86", null, true, false, default));
            await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync("firmware.zip", null, null, false, true, default));
            Assert.False(File.Exists(Path.Combine(directory, "update", "1.86", "firmware.zip")));
            await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync("..\\escape.zip", "1.86", null, true, true, default));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ArchiveRejectsTraversalBeforeWritingAnyEntry()
    {
        var directory = OwnedDirectory();
        try
        {
            var path = Path.Combine(directory, "firmware.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("safe.bin").Open())) writer.Write("safe");
                using (var writer = new StreamWriter(archive.CreateEntry("../escape.bin").Open())) writer.Write("unsafe");
            }
            await Assert.ThrowsAsync<IOException>(() => RnodeconfFirmware.ExtractArchiveAsync(new("1.86", "firmware.zip", path, ""), default));
            Assert.False(File.Exists(Path.Combine(directory, "safe.bin")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FlashPlansMatchExecutedUpstreamAndToolFailureIsNotSuccess()
    {
        var directory = OwnedDirectory();
        try
        {
            var executable = Environment.ProcessPath!;
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rnodeconf-firmware-vectors.json")));
            var cases = fixture.RootElement.GetProperty("plans");
            foreach (var (platform, filename) in new[] { ((byte)0x70, "rnode_firmware_rak4631.zip"), ((byte)0x90, "rnode_firmware.hex"), ((byte)0x80, "rnode_firmware_tbeam.zip") })
            {
                var artifact = new RnodeconfFirmwareArtifact("1.86", filename, Path.Combine(directory, filename), "");
                var plan = RnodeconfFirmware.FlashPlan(platform, artifact, directory, "SIM", 921600, executable);
                var reference = cases.GetProperty($"{platform:x2}/1.86/{filename}").EnumerateArray().Select(v => v.GetString()!).ToArray();
                var expected = reference.Skip(reference[0] == "{python}" ? 2 : 1).Select(v => v
                    .Replace("{cache}", Path.Combine(directory, "update"), StringComparison.Ordinal)
                    .Replace("{version}", "1.86", StringComparison.Ordinal)
                    .Replace("{port}", "SIM", StringComparison.Ordinal)
                    .Replace("{baud}", "921600", StringComparison.Ordinal))
                    .Select(v => v.Contains(directory, StringComparison.Ordinal) ? v.Replace('/', Path.DirectorySeparatorChar) : v);
                Assert.Equal(expected, plan.Arguments);
                var executed = false;
                Task<int> Execute(RnodeconfToolCall _, CancellationToken __) { executed = true; return Task.FromResult(7); }
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RnodeconfFirmware.RunToolAsync(plan, false, default, Execute));
                Assert.False(executed);
                await Assert.ThrowsAsync<IOException>(() => RnodeconfFirmware.RunToolAsync(plan, true, default, Execute));
                Assert.True(executed);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CacheScriptsCannotBeSelectedAsExecutableAndHomebrewMappingMatchesSource()
    {
        var directory = OwnedDirectory();
        try
        {
            var script = Path.Combine(directory, "downloaded-esptool.py");
            File.WriteAllText(script, "capture only, never executed");
            Assert.Throws<UnauthorizedAccessException>(() => RnodeconfFirmware.ValidateFlasher(script, directory));
            Assert.Equal("rnode_firmware_rak4631.zip", RnodeconfFirmware.FirmwareForDevice(0xff, 0x70, 0x71, 0x51));
            Assert.Equal("rnode_firmware_m2560.hex", RnodeconfFirmware.FirmwareForDevice(0xff, 0x90, 0x92, 0x31));
            Assert.Throws<NotSupportedException>(() => RnodeconfFirmware.FirmwareForDevice(0xff, 0x70, 0x71, 0));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RealOwnedToolProcessesDrainOutputReportFailureAndCancelWithoutFirmware()
    {
        var directory = OwnedDirectory();
        try
        {
            var app = typeof(RnodeconfCli).Assembly.Location;
            var success = new RnodeconfToolCall("dotnet", [app, "rnodeconf", "--version"]);
            await RnodeconfFirmware.RunToolAsync(success, true, default);
            await Assert.ThrowsAsync<IOException>(() => RnodeconfFirmware.RunToolAsync(
                new("dotnet", [app, "rnodeconf", "--invalid-tool-test"]), true, default));
            var config = Path.Combine(directory, "runtime");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"), "[reticulum]\nshare_instance=no\n[interfaces]\n");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RnodeconfFirmware.RunToolAsync(
                new("dotnet", [app, "rnsd", "--config", config]), true, default, timeout: TimeSpan.FromMilliseconds(300)));
            // The process owner waited for exit before returning; persisted files can be exclusively reopened.
            using var file = new FileStream(Path.Combine(config, "config"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DefaultFirmwareFallbackIsExplicitBoundedAndNeverUsedForCustomPublisher()
    {
        var directory = OwnedDirectory();
        try
        {
            var bytes = new byte[] { 4, 5, 6 };
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var calls = 0;
            using var http = new HttpClient(new AsyncHandler(request =>
            {
                calls++;
                if (calls == 1) throw new HttpRequestException("captured first request failure");
                return request.RequestUri!.AbsolutePath.EndsWith("release.json", StringComparison.Ordinal)
                    ? new StringContent($"{{\"firmware.bin\":{{\"version\":\"1.86\",\"hash\":\"{hash}\"}}}}")
                    : new ByteArrayContent(bytes);
            }));
            var firmware = new RnodeconfFirmware(http, directory);
            await Assert.ThrowsAsync<HttpRequestException>(() => firmware.PrepareAsync("firmware.bin", null, null, false, true, default));
            Assert.Equal(1, calls);
            calls = 0;
            var artifact = await firmware.PrepareAsync("firmware.bin", null, null, false, true, default, authorizeFallback: true);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(artifact.Path));
            Assert.Equal(3, calls);
            calls = 0;
            await Assert.ThrowsAsync<HttpRequestException>(() => firmware.PrepareAsync("firmware.bin", null, new Uri("https://custom.invalid/releases/"),
                false, true, default, authorizeFallback: true));
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, HttpContent> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = respond(request) });
    }

    private static string OwnedDirectory()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "rnodeconf-firmware-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpContent> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = respond(request) });
    }
}
