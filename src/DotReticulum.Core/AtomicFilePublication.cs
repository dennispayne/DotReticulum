using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DotReticulum.Core;

/// <summary>Publishes a closed same-filesystem staging file without replacing an existing destination.
/// The caller remains responsible for deleting the staging path. Windows paths are canonicalized
/// and use the extended-length namespace independently of the caller's manifest.</summary>
public static class AtomicFilePublication
{
    public static bool TryPublishNew(string staging, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staging);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        // Unix File.Move(false) may check existence and then rename, allowing concurrent replacement.
        var windows = OperatingSystem.IsWindows();
        if (windows ? CreateHardLink(WindowsPath(destination), WindowsPath(staging), IntPtr.Zero) : Link(staging, destination) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        if ((windows ? error is 80 or 183 : error == 17) && File.Exists(destination)) return false;
        throw new IOException($"Could not publish '{destination}' from '{staging}' without replacement.", new Win32Exception(error));
    }

    internal static string WindowsPath(string path)
    {
        // Prefix only after normalization: extended paths do not interpret "." and ".." or '/'.
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string destination, string source, IntPtr attributes);
}
