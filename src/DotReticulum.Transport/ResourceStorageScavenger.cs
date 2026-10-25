namespace DotReticulum.Transport;

public sealed record ResourceStorageScavengeResult(
    bool OwnedDirectory, int Examined, int Deleted, int Fresh, int Active, int Unrelated, int Errors, bool ReachedLimit);

/// <summary>Bounded cleanup of expired native resource files; never recursively scans directories.</summary>
public static class ResourceStorageScavenger
{
    public const string OwnershipMarkerFileName = ".dotreticulum-resource-storage";
    public static TimeSpan CacheLifetime => TimeSpan.FromHours(24);
    private static ReadOnlySpan<byte> Marker => "DotReticulum resource storage v1\n"u8;

    /// <summary>Explicitly designates a trusted directory as native resource storage.</summary>
    public static void Initialize(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        var directory = Path.GetFullPath(storageDirectory);
        Directory.CreateDirectory(directory);
        RejectReparsePoint(directory);
        var marker = Path.Combine(directory, OwnershipMarkerFileName);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(marker))
                {
                    RejectReparsePoint(marker);
                    using var existing = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
                    ValidateMarker(existing);
                    return;
                }
                using var ownership = ResourceStorage.CreateOwnedFile(marker, FileOptions.None, FileShare.None, FileAccess.Write);
                ownership.Write(Marker);
                ownership.Flush(flushToDisk: true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>Deletes only native-named files strictly older than 24 hours in an explicitly owned directory.</summary>
    public static ResourceStorageScavengeResult Scavenge(string storageDirectory, Action<string>? diagnostic = null,
        DateTimeOffset? now = null, int maxEntries = 4096, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(storageDirectory);
        var examined = 0;
        var deleted = 0;
        var fresh = 0;
        var active = 0;
        var unrelated = 0;
        var errors = 0;
        var limited = false;
        var owned = false;
        FileStream? ownership = null;
        void Report(string message)
        {
            try { diagnostic?.Invoke(message); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("Resource scavenger diagnostic failed: {0}; original: {1}", ex, message); }
        }
        try
        {
            if (!Directory.Exists(directory))
                return new(false, 0, 0, 0, 0, 0, 0, false);
            RejectReparsePoint(directory);
            var marker = Path.Combine(directory, OwnershipMarkerFileName);
            if (!File.Exists(marker))
            {
                Report($"Resource scavenger skipped unowned directory: {directory}");
                return new(false, 0, 0, 0, 0, 0, 0, false);
            }
            RejectReparsePoint(marker);
            ownership = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateMarker(ownership);
            owned = true;
            var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime - CacheLifetime;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (examined == maxEntries)
                {
                    limited = true;
                    Report($"Resource scavenger reached its {maxEntries}-entry limit in {directory}.");
                    break;
                }
                examined++;
                if (!IsNativeFileName(Path.GetFileName(path)))
                {
                    unrelated++;
                    continue;
                }
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    {
                        unrelated++;
                        continue;
                    }
                    if (File.GetLastWriteTimeUtc(path) >= cutoff)
                    {
                        fresh++;
                        continue;
                    }
                    // Windows needs delete sharing to unlink through this handle. Unix
                    // requires an exclusive flock (FileShare.None): delete sharing only
                    // takes a shared lock there and cannot exclude active native jobs.
                    using var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                        OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None);
                    if (File.GetLastWriteTimeUtc(path) >= cutoff)
                    {
                        fresh++;
                        continue;
                    }
                    File.Delete(path);
                    deleted++;
                    Report($"Deleted expired resource cache file: {path}");
                }
                catch (FileNotFoundException) { }
                catch (IOException ex)
                {
                    active++;
                    Report($"Resource scavenger skipped active or unavailable file {path}: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    errors++;
                    Report($"Resource scavenger could not clean {path}: {ex.Message}");
                }
            }
            return new(true, examined, deleted, fresh, active, unrelated, errors, limited);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            errors++;
            Report($"Resource scavenger failed for {directory}: {ex.Message}");
            return new(owned, examined, deleted, fresh, active, unrelated, errors, limited);
        }
        finally { ownership?.Dispose(); }
    }

    internal static bool IsNativeFileName(string name)
    {
        var prefix = name.StartsWith("resource-", StringComparison.Ordinal) ? 9 :
            name.StartsWith("input-", StringComparison.Ordinal) ? 6 : 0;
        if (prefix == 0 || name.Length != prefix + 32 + 4 || !name.EndsWith(".bin", StringComparison.Ordinal)) return false;
        foreach (var character in name.AsSpan(prefix, 32))
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        return true;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Resource storage cannot be a symbolic link or reparse point.");
    }

    private static void ValidateMarker(Stream marker)
    {
        if (marker.Length != Marker.Length) throw new InvalidDataException("Unrecognized resource storage ownership marker.");
        Span<byte> bytes = stackalloc byte[Marker.Length];
        marker.ReadExactly(bytes);
        if (!bytes.SequenceEqual(Marker)) throw new InvalidDataException("Unrecognized resource storage ownership marker.");
    }
}
