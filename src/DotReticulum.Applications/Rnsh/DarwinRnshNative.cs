using System.Runtime.InteropServices;

namespace DotReticulum.Applications.Rnsh;

// Apple XNU f6217f891ac0bb64f3d375211650a4c1ff8ca1ea:
// bsd/sys/{termios,ttycom,ioccom,fcntl,spawn}.h.
internal static class DarwinRnshNative
{
    internal const int NoControllingTerminal = 0x20000;
    internal const short SpawnNewSession = 0x0400;
    internal const short SpawnCloseOnExecDefault = 0x4000;
    internal const uint GetWindow = 0x40087468;
    internal const uint SetWindow = 0x80087467;
    internal const int TermiosSize = 72;

    internal static object?[] Flags(byte[] state)
    {
        // LP64: four unsigned longs, cc[20], four padding bytes, two speeds.
        var cc = state.AsSpan(32, 20).ToArray().Select(v => (object?)new[] { v }).ToArray();
        return [(long)BitConverter.ToUInt64(state, 0), (long)BitConverter.ToUInt64(state, 8),
            (long)BitConverter.ToUInt64(state, 16), (long)BitConverter.ToUInt64(state, 24),
            (long)BitConverter.ToUInt64(state, 56), (long)BitConverter.ToUInt64(state, 64), cc];
    }

    internal static int Window(int fd, uint request, ushort[] window) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? IoctlArm64(fd, request, 0, 0, 0, 0, 0, 0, window)
            : Ioctl(fd, request, window);

    // Darwin arm64 passes variadic arguments on the stack. Fill x2-x7 so
    // the pointer occupies the first stack slot, as ioctl's va_arg expects.
    // https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlArm64(int fd, nuint request, nuint x2, nuint x3, nuint x4, nuint x5, nuint x6, nuint x7,
        [In, Out] ushort[] window);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, nuint request, [In, Out] ushort[] window);
}
