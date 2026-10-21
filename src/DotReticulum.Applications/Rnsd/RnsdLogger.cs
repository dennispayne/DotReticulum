using System.Globalization;
using System.Text;

namespace DotReticulum.Applications.Rnsd;

/// <summary>Upstream-compatible levels, timestamps and single-backup service logging.</summary>
public sealed class RnsdLogger
{
    public const long MaximumFileSize = 5 * 1024 * 1024;
    private static readonly string[] Labels =
        ["[Critical]", "[Error]   ", "[Warning] ", "[Notice]  ", "[Info]    ", "[Verbose] ", "[Debug]   ", "[Pathing] ", "[Extra]   "];
    private readonly object _gate = new();
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly TimeProvider _clock;
    private bool _fallback;

    public RnsdLogger(int level, bool timestamps, TextWriter output, TextWriter error, string? filePath = null, TimeProvider? clock = null)
    {
        Level = Math.Clamp(level, 0, 8);
        Timestamps = timestamps;
        _output = output;
        _error = error;
        FilePath = filePath;
        _clock = clock ?? TimeProvider.System;
    }

    public int Level { get; }
    public bool Timestamps { get; }
    public string? FilePath { get; }

    public static int LevelFor(RnsdConfig config, int verbosity, bool service) =>
        (int)Math.Clamp((long)config.ConfiguredLogLevel + (service || !config.HasConfiguredLogLevel ? 0 : verbosity), 0, 8);

    public void Log(int level, string message)
    {
        if ((uint)level > 8) throw new ArgumentOutOfRangeException(nameof(level));
        if (level > Level) return;
        var line = Format(level, message);
        lock (_gate)
        {
            if (FilePath is null || _fallback) { _output.WriteLine(line); _output.Flush(); return; }
            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false));
                if (new FileInfo(FilePath).Length > MaximumFileSize) File.Move(FilePath, FilePath + ".1", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _fallback = true;
                _error.WriteLine(Format(0, $"Exception occurred while writing log message to log file: {ex.Message}"));
                _error.WriteLine(Format(0, "Dumping future log events to console!"));
                _error.Flush();
                _output.WriteLine(line);
                _output.Flush();
            }
        }
    }

    private string Format(int level, string message) =>
        (Timestamps ? $"[{_clock.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] " : "")
        + Labels[level] + " " + message;
}
