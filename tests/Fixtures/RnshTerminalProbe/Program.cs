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
if (!before.AsSpan().SequenceEqual(restored))
    throw new IOException($"Original terminal modes were not restored: before={Convert.ToHexString(before)}, restored={Convert.ToHexString(restored)}");
Console.WriteLine("terminal-modes-restored");
