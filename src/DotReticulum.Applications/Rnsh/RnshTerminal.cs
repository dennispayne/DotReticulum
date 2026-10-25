using System.Runtime.InteropServices;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rnsh;

/// <summary>Restores only the console state owned by this initiator, on every exit path.</summary>
public sealed class RnshTerminal : IDisposable
{
    private readonly byte[]? _original;
    private readonly IntPtr _input;
    private readonly IntPtr _output;
    private readonly uint? _inputMode;
    private readonly uint? _outputMode;
    private readonly bool _treatControlC;
    public object? Flags { get; }
    public bool Interactive { get; }
    internal Stream OpenInput() => OperatingSystem.IsWindows()
        ? Interactive ? new RnshConsoleInput() : new RnshNativeReader(new Microsoft.Win32.SafeHandles.SafeFileHandle(GetStdHandle(-10), false), false)
        : new RnshNativeReader(new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, false), false);
    public RnshTerminal()
    {
        Interactive = !Console.IsInputRedirected;
        _treatControlC = Interactive && Console.TreatControlCAsInput;
        if (OperatingSystem.IsWindows())
        {
            _input = GetStdHandle(-10); _output = GetStdHandle(-11);
            if (GetConsoleMode(_input, out var inputMode)) _inputMode = inputMode;
            if (GetConsoleMode(_output, out var outputMode)) _outputMode = outputMode;
        }
        else if (OperatingSystem.IsLinux() && Interactive)
        {
            var state = new byte[60];
            if (tcgetattr(0, state) == 0)
            {
                _original = state;
                // Python termios shape: four flags, input/output speed, control bytes.
                var cc = state.AsSpan(17, 32).ToArray().Select(v => (object?)new[] { v }).ToArray();
                Flags = new object?[] { (long)BitConverter.ToUInt32(state, 0), (long)BitConverter.ToUInt32(state, 4),
                    (long)BitConverter.ToUInt32(state, 8), (long)BitConverter.ToUInt32(state, 12),
                    (long)BitConverter.ToUInt32(state, 52), (long)BitConverter.ToUInt32(state, 56), cc };
            }
        }
    }
    public void Raw()
    {
        if (!Interactive) return;
        // The Console PAL may update termios when this property changes. Apply
        // the complete native mode last, not before the PAL's partial update.
        Console.TreatControlCAsInput = true;
        if (OperatingSystem.IsWindows())
        {
            if (_inputMode is { } input) SetConsoleMode(_input, (input & ~(1u | 2u | 4u | 0x20u | 0x40u)) | 0x200u | 0x80u);
            if (_outputMode is { } output) SetConsoleMode(_output, output | 4u);
        }
        else if (_original is not null)
        {
            var raw = _original.ToArray();
            cfmakeraw(raw);
            tcsetattr(0, 0, raw);
        }
    }
    public byte[]? SnapshotModes()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetConsoleMode(_input, out var input) || !GetConsoleMode(_output, out var output)) return null;
            return [.. BitConverter.GetBytes(input), .. BitConverter.GetBytes(output)];
        }
        if (OperatingSystem.IsLinux())
        {
            var modes = new byte[60];
            if (tcgetattr(0, modes) == 0) return modes;
        }
        return null;
    }
    public RnshWindow Window()
    {
        if (OperatingSystem.IsLinux())
        {
            var size = new ushort[4];
            for (var fd = 0; fd < 3; fd++)
                if (ioctl(fd, 0x5413, size) == 0) return new RnshWindow { Rows = size[0], Columns = size[1], HorizontalPixels = size[2], VerticalPixels = size[3] };
        }
        else if (OperatingSystem.IsWindows() && GetConsoleScreenBufferInfo(_output, out var info))
            return new RnshWindow { Rows = info.Window.Bottom - info.Window.Top + 1, Columns = info.Window.Right - info.Window.Left + 1, HorizontalPixels = 0, VerticalPixels = 0 };
        return new RnshWindow();
    }
    public void Dispose()
    {
        if (Interactive) Console.TreatControlCAsInput = _treatControlC;
        if (_original is not null) tcsetattr(0, 1, _original);
        if (_inputMode is { } input) SetConsoleMode(_input, input);
        if (_outputMode is { } output) SetConsoleMode(_output, output);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferInfo { public Coord Size, Cursor; public ushort Attributes; public Rect Window; public Coord Maximum; }
    [DllImport("kernel32")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32")] private static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out BufferInfo info);
    [DllImport("libc", SetLastError = true)] private static extern int tcgetattr(int fd, [Out] byte[] attr);
    [DllImport("libc", SetLastError = true)] private static extern int tcsetattr(int fd, int action, byte[] attr);
    [DllImport("libc")] private static extern void cfmakeraw([In, Out] byte[] attr);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, uint request, [Out] ushort[] window);
}
