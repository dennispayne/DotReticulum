using DotReticulum.Applications.Rnsh;

if (args is ["streams", var stdin, var stdout, var stderr])
{
    if (Console.IsInputRedirected != bool.Parse(stdin) || Console.IsOutputRedirected != bool.Parse(stdout) ||
        Console.IsErrorRedirected != bool.Parse(stderr)) throw new IOException("Child terminal/pipe endpoint assignment differs from request.");
    Console.OpenStandardOutput().Write("stdout-marker"u8);
    Console.OpenStandardError().Write("stderr-marker"u8);
    return;
}

using var terminal = new RnshTerminal();
var before = terminal.SnapshotModes() ?? throw new IOException("Probe must run in an actual terminal.");
terminal.Raw();
var raw = terminal.SnapshotModes() ?? throw new IOException("Terminal disappeared.");
if (before.AsSpan().SequenceEqual(raw)) throw new IOException("Raw mode did not change terminal flags.");
terminal.Dispose();
var restored = terminal.SnapshotModes() ?? throw new IOException("Terminal disappeared.");
var comparison = restored;
if (OperatingSystem.IsMacOS())
{
    const ulong pendingInput = 0x20000000;
    var beforeFlags = BitConverter.ToUInt64(before, 24);
    var restoredFlags = BitConverter.ToUInt64(restored, 24);
    if (restoredFlags != beforeFlags && restoredFlags == (beforeFlags | pendingInput))
    {
        // XNU tty.c TIOCSETAW adds PENDIN when restoring canonical input.
        // Compare every configured mode byte, excluding only that kernel state.
        comparison = restored.ToArray();
        before.AsSpan(24, 8).CopyTo(comparison.AsSpan(24, 8));
        Console.WriteLine($"darwin-kernel-pendin: before={beforeFlags:x}, restored={restoredFlags:x}");
    }
}
if (!before.AsSpan().SequenceEqual(comparison))
    throw new IOException($"Original terminal modes were not restored: before={Convert.ToHexString(before)}, restored={Convert.ToHexString(restored)}");
Console.WriteLine("terminal-modes-restored");
