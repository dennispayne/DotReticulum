using System.Globalization;
using System.Text;
using DotReticulum.Applications.Rngit;

namespace DotReticulum.Applications.Runtime;

/// <summary>
/// Explicitly authorized executable metadata using the shared bounded process runner.
/// Native Windows providers are a deliberate extension of upstream's Unix-only behavior.
/// </summary>
public static class DiscoveryMetadata
{
    private const int MaximumOutputBytes = 4096;
    public static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromSeconds(8);

    public static async ValueTask<DiscoveryAdvertisement> ResolveAsync(DiscoveryAdvertisement advertisement,
        string? location = null, CancellationToken cancellationToken = default, TimeSpan? commandTimeout = null,
        Func<string, bool>? authorizeExecutable = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timeout = commandTimeout ?? DefaultExecutionTimeout;
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        var result = advertisement;
        if (!string.IsNullOrEmpty(location))
        {
            var locationExecutable = Executable(location);
            Authorize(locationExecutable, authorizeExecutable);
            var output = await ExecuteAsync(locationExecutable, timeout, cancellationToken).ConfigureAwait(false);
            var coordinates = ParseLocation(output);
            result = result with { Latitude = coordinates.Latitude, Longitude = coordinates.Longitude, Height = coordinates.Height };
        }
        var endpointPath = ExpandPath(result.ReachableOn);
        if (File.Exists(endpointPath))
        {
            var endpointExecutable = Executable(endpointPath);
            Authorize(endpointExecutable, authorizeExecutable);
            result = result with { ReachableOn = Sanitize(await ExecuteAsync(endpointExecutable, timeout, cancellationToken).ConfigureAwait(false)) };
        }
        if (!DiscoveryCodec.IsHost(result.ReachableOn)) throw new FormatException("Discovery metadata provider did not return a valid IP address or hostname.");
        return result;
    }

    public static (double Latitude, double Longitude, double Height) ParseLocation(string output)
    {
        var parts = Sanitize(output).Replace(" ", "", StringComparison.Ordinal).Split(',');
        if (parts.Length != 3) throw new FormatException("Discovery location must contain latitude, longitude and height.");
        var values = new double[3];
        for (var n = 0; n < values.Length; n++)
            if (!double.TryParse(parts[n], NumberStyles.Float, CultureInfo.InvariantCulture, out values[n]) || !double.IsFinite(values[n]))
                throw new FormatException("Invalid discovery location coordinate.");
        if (values[0] is < -90 or > 90 || values[1] is < -180 or > 180 || values[2] is < -4000 or > 1_000_000)
            throw new FormatException("Discovery location coordinate is out of range.");
        return (values[0], values[1], values[2]);
    }

    private static string Sanitize(string value) => value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Trim();

    private static void Authorize(string path, Func<string, bool>? policy)
    {
        if (policy?.Invoke(path) != true) throw new UnauthorizedAccessException("Executable discovery metadata requires explicit host authorization.");
    }

    private static string ExpandPath(string input)
    {
        var path = Sanitize(input);
        if (path == "~") path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    private static string Executable(string input)
    {
        var path = ExpandPath(input);
        if (!File.Exists(path)) throw new FileNotFoundException("Configured discovery metadata provider does not exist.", path);
        if (OperatingSystem.IsWindows())
        {
            if (Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".com"))
                throw new IOException("Windows discovery metadata requires a native executable, not a command shell or script.");
            return Path.GetFullPath(path);
        }
        var mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new IOException("Configured discovery metadata provider is not executable.");
        return Path.GetFullPath(path);
    }

    private static async Task<string> ExecuteAsync(string executable, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RngitProcess.RunAsync(executable, [], timeout: timeout,
            maxOutputBytes: MaximumOutputBytes, stop: cancellationToken).ConfigureAwait(false);
        if (result.Code == RngitProcess.TimedOut) throw new TimeoutException("Discovery metadata provider timed out.");
        if (result.Code != 0) throw new IOException($"Discovery metadata provider failed with code {result.Code}: {result.Error}");
        return new UTF8Encoding(false, true).GetString(result.Output);
    }
}
