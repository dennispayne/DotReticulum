using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DotReticulum.Core;

/// <summary>Publishes a closed same-filesystem staging file without replacing an existing destination.
/// The caller remains responsible for deleting the staging path.</summary>
public static class AtomicFilePublication
{
    public static bool TryPublishNew(string staging, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staging);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        // Unix File.Move(false) may check existence and then rename, allowing concurrent replacement.
        var windows = OperatingSystem.IsWindows();
        if (windows ? CreateHardLink(destination, staging, IntPtr.Zero) : Link(staging, destination) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        if ((windows ? error is 80 or 183 : error == 17) && File.Exists(destination)) return false;
        throw new IOException($"Could not publish '{destination}' without replacement.", new Win32Exception(error));
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string destination, string source, IntPtr attributes);
}
