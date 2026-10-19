using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public sealed record RngitHostedRepository(string Group, string Name, string Path);

/// <summary>
/// Owned bare repository discovery and upstream hosting policies. Filesystem links and unsafe
/// path components are rejected intentionally; upstream's path splitting alone is not containment.
/// </summary>
public sealed partial class RngitRepositoryStore
{
    private readonly RngitHostConfiguration configuration;
    private readonly Action<string>? diagnostic;
    private readonly object gate = new();
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly Dictionary<string, RngitHostedRepository> repositories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> repositoryLocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> groups = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public const int MaxPermissionBytes = 1024 * 1024;

    public RngitRepositoryStore(RngitHostConfiguration configuration, Action<string>? diagnostic = null,
        Func<string, string>? permissionExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        this.configuration = configuration;
        this.diagnostic = diagnostic;
        PermissionExecutor = permissionExecutor;
        MirrorInterval = configuration.MirrorInterval;
        foreach (var (group, path) in configuration.Groups)
        {
            try
            {
                if (!ValidComponent(group)) throw new InvalidDataException("Unsafe group name.");
                var full = System.IO.Path.GetFullPath(path);
                EnsureUnlinked(full);
                if (!Directory.Exists(full)) continue;
                groups.Add(group, full);
                foreach (var entry in Directory.EnumerateDirectories(full))
                {
                    var name = System.IO.Path.GetFileName(entry);
                    if (!ValidComponent(name) || name.EndsWith(".work", StringComparison.Ordinal)
                        || name.EndsWith(".releases", StringComparison.Ordinal)) continue;
                    try
                    {
                        EnsureUnlinked(entry);
                        if (IsBareAsync(entry, CancellationToken.None).GetAwaiter().GetResult())
                            repositories.Add(group + "/" + name, new(group, name, entry));
                    }
                    catch (Exception e) { Report($"Skipping repository {group}/{name} ({FailureDescription(e)})."); }
                }
            }
            catch (Exception e) { Report($"Skipping group {group} ({FailureDescription(e)})."); }
        }
    }

    public IReadOnlyList<RngitHostedRepository> Repositories
    {
        get { lock (gate) return Array.AsReadOnly(repositories.Values.ToArray()); }
    }

    public RngitHostedRepository? Find(string repository)
    {
        if (!TryRepository(repository, out _, out _)) return null;
        lock (gate) return repositories.GetValueOrDefault(repository);
    }

    /// <summary>Serialize other hosted Git/document/release handlers with store lifecycle operations.</summary>
    public async Task<IDisposable> LockRepositoryAsync(string repository, CancellationToken cancellationToken)
    {
        if (!TryRepository(repository, out _, out _)) throw new ArgumentException("Invalid repository.", nameof(repository));
        SemaphoreSlim semaphore;
        lock (gate)
        {
            if (!repositoryLocks.TryGetValue(repository, out semaphore!))
            {
                // Only hosted or configured-group paths get registry entries; the bounded host queue
                // limits simultaneous calls. Creation uses the global mutation lock instead.
                if (!repositories.ContainsKey(repository)) throw new KeyNotFoundException("Repository not found.");
                repositoryLocks.Add(repository, semaphore = new(1, 1));
            }
        }
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? value = semaphore;
        public void Dispose() => Interlocked.Exchange(ref value, null)?.Release();
    }

    internal static bool ValidComponent(string value)
    {
        if (value.Length is 0 or > 256 || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ')) return false;
        if (value.Any(c => c < 32 || c == 127 || "\\/:*?\"<>|".Contains(c))) return false;
        var stem = value.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && stem[3] is >= '1' and <= '9');
    }

    private static bool TryRepository(string? value, out string group, out string name)
    {
        var parts = value?.Split('/');
        group = parts is { Length: 2 } ? parts[0] : "";
        name = parts is { Length: 2 } ? parts[1] : "";
        return ValidComponent(group) && ValidComponent(name);
    }

    private static void EnsureUnlinked(string path)
    {
        for (var item = new FileInfo(path); item is not null; item = item.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if (item.LinkTarget is not null || ((item.Exists || Directory.Exists(item.FullName))
                && (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Linked filesystem paths are not authorized.");
    }

    internal static void DeleteOwnedDirectory(string path)
    {
        EnsureUnlinked(path);
        if (OperatingSystem.IsWindows())
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            }))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(path, recursive: true);
    }

    private static async Task<bool> IsBareAsync(string path, CancellationToken token) =>
        (await GitAsync(path, ["rev-parse", "--is-bare-repository"], token).ConfigureAwait(false)).Trim() == "true";

    private static async Task<string> GitAsync(string path, string[] arguments, CancellationToken token)
    {
        EnsureUnlinked(path);
        var args = new[] { "--no-pager", "--git-dir=" + path, "-c", "core.hooksPath=", "-c", "protocol.allow=never" }.Concat(arguments).ToArray();
        var result = await RngitProcess.RunAsync("git", args, path, timeout: TimeSpan.FromMinutes(2),
            maxOutputBytes: 1024 * 1024, stop: token, environment: GitEnvironment()).ConfigureAwait(false);
        if (result.Code != 0) throw new RepositoryGitFailure(false, result.Code);
        return Utf8.GetString(result.Output);
    }

    private sealed class RepositoryGitFailure(bool external, int exitCode) : IOException("Git repository operation failed.")
    {
        public bool External { get; } = external;
        public int ExitCode { get; } = exitCode;
    }

    // Exception messages, stderr and command arguments may contain upstream URL credentials.
    private static string FailureDescription(Exception failure) => failure is RepositoryGitFailure git
        ? $"{(git.External ? "External Git" : "Repository Git")} exit {git.ExitCode}"
        : failure.GetType().Name;

    private static Dictionary<string, string?> GitEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string name && name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) result[name] = null;
        result["GIT_CONFIG_NOSYSTEM"] = "1";
        result["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        result["GIT_TERMINAL_PROMPT"] = "0";
        return result;
    }

    private void Report(string message)
    {
        try { diagnostic?.Invoke(message); }
        catch (Exception e) { System.Diagnostics.Trace.TraceError($"Repository diagnostic observer failed ({e.GetType().Name})."); }
    }

    private static object? Get(object? data, object key) => RngitWire.Get(data, key);
    private static bool IsMap(object? data) => data is System.Collections.IDictionary;
    private static byte[] Status(byte status, string message = "") => RngitWire.Status(status, message);
}
