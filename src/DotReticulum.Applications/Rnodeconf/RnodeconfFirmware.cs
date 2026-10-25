using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotReticulum.Applications.Rnodeconf;

public sealed record RnodeconfFirmwareArtifact(string Version, string Filename, string Path, string Sha256);
public sealed record RnodeconfToolCall(string Executable, IReadOnlyList<string> Arguments);

/// <summary>Bounded native acquisition/cache/integrity and source-derived device-tool plans.</summary>
public sealed class RnodeconfFirmware(HttpClient http, string stateDirectory)
{
    public const long MaximumFirmwareBytes = 64 * 1024 * 1024;
    private const string DefaultReleaseBase = "https://github.com/markqvist/RNode_Firmware/releases/";
    private readonly string _cache = Path.Combine(Path.GetFullPath(stateDirectory), "update");

    public static string FirmwareForModel(byte model)
    {
        using var data = JsonDocument.Parse(RnodeconfReferenceData.Json);
        if (!data.RootElement.GetProperty("models").TryGetProperty(model.ToString("x2", CultureInfo.InvariantCulture), out var entry) ||
            entry[4].ValueKind != JsonValueKind.String) throw new NotSupportedException("No upstream firmware mapping for this model.");
        return entry[4].GetString()!;
    }

    public static string FirmwareForDevice(byte model, byte platform, byte mcu, byte board) => model != 0xff
        ? FirmwareForModel(model)
        : (platform, mcu, board) switch
        {
            (0x90, 0x91, _) => "rnode_firmware.hex",
            (0x90, 0x92, _) => "rnode_firmware_m2560.hex",
            (0x80, _, 0x34) => "rnode_firmware_featheresp32.zip",
            (0x80, _, 0x35) => "rnode_firmware_esp32_generic.zip",
            (0x70, _, 0x51) => "rnode_firmware_rak4631.zip",
            _ => throw new NotSupportedException("No upstream firmware mapping for this homebrew board.")
        };

    public static string ValidateFlasher(string flasher, string stateDirectory)
    {
        if (!Path.IsPathFullyQualified(flasher) || !File.Exists(flasher)) throw new IOException("Explicit installed --flasher executable path is required.");
        var path = Path.GetFullPath(flasher);
        var state = Path.GetFullPath(stateDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (path.StartsWith(state, StringComparison.OrdinalIgnoreCase) ||
            new[] { ".py", ".ps1", ".sh", ".bat", ".cmd", ".zip", ".bin", ".hex" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Downloaded/cache scripts and firmware files cannot be selected as flash executables.");
        return path;
    }

    public static void ValidateComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value is "." or ".." ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("Invalid firmware filename/version.");
    }

    public async Task<RnodeconfFirmwareArtifact> PrepareAsync(string filename, string? version, Uri? releaseBase,
        bool noCheck, bool authorizeDownload, CancellationToken token, bool authorizeFallback = false)
    {
        ValidateComponent(filename);
        if (version is not null) ValidateComponent(version);
        if (noCheck && version is null) throw new ArgumentException("--nocheck requires --fw-version.");
        var baseUri = releaseBase ?? new Uri(DefaultReleaseBase);
        if (baseUri.Scheme is not ("http" or "https")) throw new ArgumentException("Firmware base URL must use HTTP(S).");
        if (!baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)) throw new ArgumentException("Firmware base URL must end with '/'.");
        Directory.CreateDirectory(_cache);
        string? expected = null;
        if (!noCheck)
        {
            if (!authorizeDownload) throw new UnauthorizedAccessException("Online firmware metadata requires --authorize-firmware-download.");
            var metadataUri = new Uri(baseUri, version is null ? "latest/download/release.json" : $"download/{version}/release.json");
            byte[] metadata;
            try { metadata = await DownloadAsync(metadataUri, 1024 * 1024, token).ConfigureAwait(false); }
            catch (Exception exception) when (authorizeFallback && releaseBase is null && !token.IsCancellationRequested &&
                exception is HttpRequestException or IOException or OperationCanceledException)
            {
                // Pinned upstream fallback URL is identical to the default latest endpoint.
                // Preserve the explicit retry contract, not an invented alternate publisher.
                metadata = await DownloadAsync(new Uri("https://github.com/markqvist/rnode_firmware/releases/latest/download/release.json"),
                    1024 * 1024, token).ConfigureAwait(false);
            }
            using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty(filename, out var release)) throw new IOException("Release has no firmware variant for this board.");
            version = release.GetProperty("version").ToString();
            expected = release.GetProperty("hash").GetString();
            ValidateComponent(version);
            ValidateHash(expected);
            await AtomicWriteAsync(Path.Combine(_cache, filename + ".version.latest"), Encoding.UTF8.GetBytes($"{version} {expected}"), token).ConfigureAwait(false);
        }
        var folder = Path.Combine(_cache, version!);
        Directory.CreateDirectory(folder);
        var metadataPath = Path.Combine(folder, filename + ".version");
        if (expected is null)
        {
            if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length > 1024) throw new IOException("No valid locally cached release metadata.");
            var pieces = (await File.ReadAllTextAsync(metadataPath, token).ConfigureAwait(false)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length != 2 || pieces[0] != version) throw new IOException("Invalid cached release metadata.");
            expected = pieces[1];
            ValidateHash(expected);
        }
        else await AtomicWriteAsync(metadataPath, Encoding.UTF8.GetBytes($"{version} {expected}"), token).ConfigureAwait(false);
        var path = Path.Combine(folder, filename);
        if (!File.Exists(path))
        {
            if (!authorizeDownload) throw new UnauthorizedAccessException("Missing firmware requires --authorize-firmware-download.");
            var bytes = await DownloadAsync(new Uri(baseUri, $"download/{version}/{filename}"), MaximumFirmwareBytes, token).ConfigureAwait(false);
            if (!Matches(bytes, expected!)) throw new IOException("Firmware SHA-256 mismatch; downloaded data was not cached or executed.");
            await AtomicWriteAsync(path, bytes, token).ConfigureAwait(false);
        }
        var info = new FileInfo(path);
        if (info.Length > MaximumFirmwareBytes) throw new IOException("Firmware exceeds size limit.");
        await using var file = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(expected!))) throw new IOException("Cached firmware SHA-256 mismatch.");
        return new(version!, filename, path, expected!);
    }

    public static async Task ExtractArchiveAsync(RnodeconfFirmwareArtifact artifact, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(artifact.Path);
        if (archive.Entries.Count > 128) throw new IOException("Firmware archive has too many entries.");
        long length = 0;
        var folder = Path.GetDirectoryName(artifact.Path)!;
        var entries = new List<(ZipArchiveEntry Entry, string Path)>();
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.EndsWith("/", StringComparison.Ordinal)) continue;
            // Known firmware archives contain flat data/tool files; do not allow links or paths.
            if (normalized.Contains('/') || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000) throw new IOException("Unsafe firmware archive path/link.");
            ValidateComponent(normalized);
            length = checked(length + entry.Length);
            if (length > 128 * 1024 * 1024) throw new IOException("Expanded firmware archive exceeds size limit.");
            entries.Add((entry, Path.Combine(folder, normalized)));
        }

        if (entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new IOException("Duplicate firmware archive entry.");
        foreach (var (entry, path) in entries)
        {
            await using var input = entry.Open();
            using var buffer = new MemoryStream();
            var block = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(block, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > entry.Length) throw new IOException("Firmware archive entry exceeds declared size.");
                buffer.Write(block, 0, read);
            }
            if (buffer.Length != entry.Length) throw new IOException("Truncated firmware archive entry.");
            await AtomicWriteAsync(path, buffer.ToArray(), token).ConfigureAwait(false);
        }
    }

    public static async Task<byte[]?> PartitionHashAsync(byte platform, RnodeconfFirmwareArtifact artifact, CancellationToken token)
    {
        if (artifact.Filename == "extracted_rnode_firmware.zip") return Convert.FromHexString(artifact.Sha256);
        var partition = Path.Combine(Path.GetDirectoryName(artifact.Path)!, artifact.Filename.Replace(".zip", ".bin", StringComparison.Ordinal));
        if (!File.Exists(partition)) return null;
        if (new FileInfo(partition).Length > MaximumFirmwareBytes) throw new IOException("Firmware partition exceeds size limit.");
        var bytes = await File.ReadAllBytesAsync(partition, token).ConfigureAwait(false);
        if (platform == 0x70) return SHA256.HashData(bytes);
        if ((platform is 0x80 or 0x90) && bytes.Length >= 32)
        {
            var expected = bytes.AsSpan(bytes.Length - 32).ToArray();
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)), expected) ? expected : null;
        }
        return null;
    }

    public static RnodeconfToolCall FlashPlan(byte platform, RnodeconfFirmwareArtifact artifact, string stateDirectory, string port, int baud, string flasher)
    {
        if (baud is < 1200 or > 4000000) throw new ArgumentException("Invalid flash baud rate.");
        flasher = ValidateFlasher(flasher, stateDirectory);
        using var data = JsonDocument.Parse(RnodeconfReferenceData.Json);
        var boundary = decimal.Parse(artifact.Version, CultureInfo.InvariantCulture) >= 1.55m ? "1.86" : "1.54";
        var name = $"{platform:x2}/{boundary}/{artifact.Filename}";
        if (!data.RootElement.GetProperty("plans").TryGetProperty(name, out var plan)) throw new NotSupportedException("No pinned-upstream flash plan for this firmware/platform.");
        var parts = plan.EnumerateArray().Select(v => v.GetString()!
            .Replace("{cache}", Path.Combine(Path.GetFullPath(stateDirectory), "update"), StringComparison.Ordinal)
            .Replace("{state}", Path.GetFullPath(stateDirectory), StringComparison.Ordinal)
            .Replace("{extracted}", Path.Combine(Path.GetFullPath(stateDirectory), "extracted"), StringComparison.Ordinal)
            .Replace("{version}", artifact.Version, StringComparison.Ordinal)
            .Replace("{port}", port, StringComparison.Ordinal)
            .Replace("{baud}", baud.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            .Select(p => p.Contains(Path.GetFullPath(stateDirectory), StringComparison.Ordinal) ? p.Replace('/', Path.DirectorySeparatorChar) : p).ToArray();
        // Never run Python/scripts shipped in a downloaded archive. An explicitly selected,
        // independently installed executable receives only the source-derived device arguments.
        return new(Path.GetFullPath(flasher), parts.Skip(parts[0] == "{python}" ? 2 : 1).ToArray());
    }

    public static async Task RunToolAsync(RnodeconfToolCall call, bool authorized, CancellationToken token,
        Func<RnodeconfToolCall, CancellationToken, Task<int>>? execute = null, TimeSpan? timeout = null)
    {
        if (!authorized) throw new UnauthorizedAccessException("Device tool execution requires --authorize-flash and --authorize-device-write.");
        token.ThrowIfCancellationRequested();
        var status = execute is null ? await ExecuteOwnedAsync(call, token, timeout ?? TimeSpan.FromMinutes(10)).ConfigureAwait(false) : await execute(call, token).ConfigureAwait(false);
        if (status != 0) throw new IOException($"Device tool exited with code {status}; operation did not complete.");
    }

    private static async Task<int> ExecuteOwnedAsync(RnodeconfToolCall call, CancellationToken token, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)) throw new ArgumentException("Invalid device-tool timeout.");
        var start = new ProcessStartInfo(call.Executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in call.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Could not start the selected device tool.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer, deadline.Token).ConfigureAwait(false) > 0) { }
        }
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task<byte[]> DownloadAsync(Uri uri, long maximumBytes, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes) throw new IOException("Firmware download exceeds size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > maximumBytes) throw new IOException("Firmware download exceeds size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static void ValidateHash(string? hash)
    {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new IOException("Invalid firmware SHA-256 metadata.");
    }
    private static bool Matches(byte[] bytes, string expected) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expected));
    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await RnodeconfSigning.SaveNewAsync(staging, bytes, token).ConfigureAwait(false);
            File.Move(staging, path, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
