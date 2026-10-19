using System.Buffers.Binary;
using System.Globalization;

namespace DotReticulum.Applications.Rngit;

public sealed record MediaConversion(
    bool Enabled = true,
    int? Quality = null,
    int? MaxDimension = null,
    string? Backend = null,
    TimeSpan? Timeout = null,
    int MaxInputBytes = 16 * 1024 * 1024,
    int MaxOutputBytes = 16 * 1024 * 1024);

public enum RngitMediaStatus { Bypassed, Converted, Failed }

public sealed record RngitMediaResult(RngitMediaStatus Status, byte[] Data, string FileName,
    string? Backend = null, string? Error = null, int? Width = null, int? Height = null);

public sealed record RngitMediaBackend(string Name, bool Available);

/// <summary>Upstream encoder ordering and WebP header acceptance, with bounded binary I/O.</summary>
public sealed class RngitMedia
{
    private static readonly string[] BackendNames = ["magick", "convert", "gm", "ffmpeg", "avconv"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".tiff", ".tif", ".bmp"];
    private readonly Func<string, string?> findExecutable;
    private readonly Action<string>? diagnostic;
    private readonly string? environmentBackend;
    private string? winner;
    private int noBackendLogged;

    public RngitMedia(Func<string, string?>? findExecutable = null, Action<string>? diagnostic = null)
    {
        this.findExecutable = findExecutable ?? FindExecutable;
        this.diagnostic = diagnostic ?? Console.Error.WriteLine;
        environmentBackend = Environment.GetEnvironmentVariable("RNGIT_MEDIA_BACKEND");
    }

    public IReadOnlyList<RngitMediaBackend> AvailableBackends() => BackendNames
        .Select(name => new RngitMediaBackend(name, findExecutable(name) is not null)).ToArray();

    public async Task<RngitMediaResult> ConvertAsync(byte[] data, string fileName,
        MediaConversion? config = null, CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        stop.ThrowIfCancellationRequested();
        config ??= new();
        if (config.MaxInputBytes is < 1 or > 256 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(config));
        if (config.MaxOutputBytes is < 1 or > 256 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(config));
        if (data.Length > config.MaxInputBytes)
            return Failure(data, fileName, null, "Media input exceeds its byte limit.");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!config.Enabled || extension == ".webp" || !ImageExtensions.Contains(extension))
        {
            var dimensions = WebpInfo(data);
            return new(RngitMediaStatus.Bypassed, data, fileName,
                Width: dimensions?.Width, Height: dimensions?.Height);
        }

        var backend = SelectBackend(config.Backend ?? environmentBackend);
        if (backend is null)
            return Failure(data, fileName, null,
                "No WebP encoding backend available for media conversion. You can Install ImageMagick or ffmpeg to enable image conversion.",
                report: Interlocked.Exchange(ref noBackendLogged, 1) == 0);
        Volatile.Write(ref noBackendLogged, 0);
        var (name, executable) = backend.Value;
        var result = await RngitProcess.RunAsync(executable, EncoderArguments(name, config.Quality, config.MaxDimension),
            stdin: data, timeout: config.Timeout ?? TimeSpan.FromSeconds(8),
            maxOutputBytes: config.MaxOutputBytes, stop: stop).ConfigureAwait(false);
        if (result.Code != 0)
            return Failure(data, fileName, name, $"Media conversion via {name} failed ({result.Code}): {result.Error}");
        var outputDimensions = WebpInfo(result.Output);
        if (outputDimensions is null)
            return Failure(data, fileName, name, $"Media conversion via {name} produced invalid WebP output.");
        return new(RngitMediaStatus.Converted, result.Output,
            Path.ChangeExtension(fileName, ".webp"), name,
            Width: outputDimensions.Value.Width, Height: outputDimensions.Value.Height);
    }

    public static IReadOnlyList<string> EncoderArguments(string backend, int? quality = null, int? maxDimension = null)
    {
        if (!BackendNames.Contains(backend)) throw new ArgumentException("Unknown upstream encoder backend.", nameof(backend));
        var arguments = backend switch
        {
            "magick" or "convert" => new List<string> { "-", "webp:-" },
            "gm" => new List<string> { "convert", "-", "webp:-" },
            _ => new List<string> { "-y", "-loglevel", "error", "-i", "-", "-f", "webp", "pipe:1" }
        };
        var options = new List<string>();
        if (quality is not null) options.AddRange(["-quality", Math.Clamp(quality.Value, 1, 100).ToString(CultureInfo.InvariantCulture)]);
        if (maxDimension > 0)
        {
            var dimension = maxDimension.Value.ToString(CultureInfo.InvariantCulture);
            if (backend is "magick" or "convert" or "gm") options.AddRange(["-resize", $"{dimension}x{dimension}>"]);
            else options.AddRange(["-vf", $"scale='min(iw,{dimension})':'min(ih,{dimension})':force_original_aspect_ratio=decrease"]);
        }
        arguments.InsertRange(backend is "magick" or "convert" or "gm" ? arguments.Count - 1 : arguments.IndexOf("-f"), options);
        return arguments;
    }

    public static (int Width, int Height)? WebpInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WEBP"u8)) return null;
        var chunk = data[12..16];
        int width, height;
        if (chunk.SequenceEqual("VP8X"u8))
        {
            width = data[24] + (data[25] << 8) + (data[26] << 16) + 1;
            height = data[27] + (data[28] << 8) + (data[29] << 16) + 1;
        }
        else if (chunk.SequenceEqual("VP8 "u8))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]) & 0x3fff;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[28..30]) & 0x3fff;
        }
        else if (chunk.SequenceEqual("VP8L"u8))
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..25]);
            width = (int)(bits & 0x3fff) + 1;
            height = (int)((bits >> 14) & 0x3fff) + 1;
        }
        else return null;
        return width > 0 && height > 0 ? (width, height) : null;
    }

    public static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            foreach (var suffix in OperatingSystem.IsWindows() ? new[] { ".exe", ".com", "" } : new[] { "" })
            {
                var candidate = Path.Combine(directory.Trim('"'), name + suffix);
                if (!File.Exists(candidate)) continue;
                if (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate)
                    & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    return candidate;
            }
        }
        return null;
    }

    private (string Name, string File)? SelectBackend(string? forced)
    {
        if (!string.IsNullOrEmpty(forced))
        {
            if (!BackendNames.Contains(forced)) return null;
            var file = findExecutable(forced);
            return file is null ? null : (forced, file);
        }
        var previous = Volatile.Read(ref winner);
        var order = previous is null ? BackendNames : BackendNames.OrderBy(name => name != previous).ToArray();
        foreach (var name in order)
        {
            var file = findExecutable(name);
            if (file is null) continue;
            Volatile.Write(ref winner, name);
            return (name, file);
        }
        return null;
    }

    private RngitMediaResult Failure(byte[] data, string fileName, string? backend, string error, bool report = true)
    {
        if (report) diagnostic?.Invoke(error);
        return new(RngitMediaStatus.Failed, data, fileName, backend, error);
    }
}
