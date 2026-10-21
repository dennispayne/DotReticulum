namespace DotReticulum.Applications.Runtime;

public static class RuntimeHousekeeping
{
    public static int CleanCaches(string storageDirectory, DateTimeOffset now, Action<string> diagnostic)
    {
        var removed = 0;
        foreach (var (directory, age) in new[]
        {
            (Path.Combine(storageDirectory, "resources"), TimeSpan.FromDays(1)),
            (Path.Combine(storageDirectory, "cache"), TimeSpan.FromDays(7)),
        })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(path);
                if (name.Length != 64 || !name.All(char.IsAsciiHexDigit)) continue;
                try
                {
                    var file = new FileInfo(path);
                    if (file.LinkTarget is not null || now.UtcDateTime - file.LastWriteTimeUtc <= age) continue;
                    file.Delete();
                    removed++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    diagnostic($"Could not clean runtime cache '{name}': {error.Message}");
                }
            }
        }
        return removed;
    }
}
