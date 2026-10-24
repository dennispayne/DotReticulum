namespace DotReticulum.Applications.Rnsd;

public static class RnsdConfigurationFiles
{
    public const string DefaultConfig = """
        # DotReticulum native default. Upstream enables AutoInterface here.
        # This experimental runtime requires explicit opt-in to network discovery.
        [reticulum]
          enable_transport = no
          share_instance = yes
        [logging]
          loglevel = 4
        [interfaces]
          [[Default Interface]]
            type = AutoInterface
            enabled = no
        """;

    public static (string Dir, string File) Resolve(string? argument, string? userDirectory = null, string? systemDirectory = null)
    {
        var home = userDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (argument is null)
        {
            var system = systemDirectory ?? (OperatingSystem.IsWindows() ? Path.Combine(Path.GetPathRoot(home)!, "etc", "reticulum") : Path.Combine(Path.DirectorySeparatorChar.ToString(), "etc", "reticulum"));
            foreach (var candidate in new[] { system, Path.Combine(home, ".config", "reticulum") })
                if (File.Exists(Path.Combine(candidate, "config"))) return (candidate, Path.Combine(candidate, "config"));
            var fallback = Path.Combine(home, ".reticulum");
            return (fallback, Path.Combine(fallback, "config"));
        }

        if (argument == "~") argument = home;
        else if (argument.StartsWith("~/", StringComparison.Ordinal) || argument.StartsWith("~\\", StringComparison.Ordinal))
            argument = Path.Combine(home, argument[2..]);
        var full = Path.GetFullPath(argument);
        // Existing files are a documented .NET extension; otherwise --config names a directory, like upstream.
        return File.Exists(full) ? (Path.GetDirectoryName(full)!, full) : (full, Path.Combine(full, "config"));
    }

    public static bool EnsureCreated(string configDir, string configFile)
    {
        Directory.CreateDirectory(configDir);
        if (File.Exists(configFile)) return false;
        var staging = Path.Combine(configDir, ".config-" + Guid.NewGuid().ToString("N") + ".new");
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream);
                writer.WriteLine(DefaultConfig);
            }
            File.Move(staging, configFile, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(configFile)) { return false; }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
