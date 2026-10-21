using System.Globalization;
using System.Text;

namespace DotReticulum.Applications.Rnsd;

/// <summary>A bounded management console, deliberately not an arbitrary-code interpreter.</summary>
public static class RnsdInteractiveShell
{
    public const int MaximumLineLength = 4096;
    public const string Help = "Commands: help, status, interfaces, persist, quit, exit. No arbitrary code execution.";

    public static async Task RunAsync(RnsdHost host, TextReader input, TextWriter output, TextWriter error, CancellationToken stop)
    {
        output.WriteLine("DotReticulum native management shell (replacement for the upstream Python REPL).");
        output.WriteLine(Help);
        output.Flush();
        var buffer = new char[1024];
        var line = new StringBuilder();
        var overflow = false;
        var afterCarriageReturn = false;
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            // Console.In and user TextReaders can synchronously block before returning their
            // ValueTask. Keep that single pending read off the daemon/cancellation path.
            var read = Task.Run(async () => await input.ReadAsync(buffer.AsMemory(), stop).ConfigureAwait(false), CancellationToken.None);
            var count = await read.WaitAsync(stop).ConfigureAwait(false);
            if (count == 0)
            {
                if (!overflow && line.Length > 0)
                    _ = await ExecuteAsync(line.ToString(), host, output, error, stop).ConfigureAwait(false);
                return;
            }
            for (var i = 0; i < count; i++)
            {
                stop.ThrowIfCancellationRequested();
                var c = buffer[i];
                if (afterCarriageReturn && c == '\n') { afterCarriageReturn = false; continue; }
                afterCarriageReturn = c == '\r';
                if (c is '\r' or '\n')
                {
                    if (!overflow && !await ExecuteAsync(line.ToString(), host, output, error, stop).ConfigureAwait(false)) return;
                    line.Clear();
                    overflow = false;
                    continue;
                }
                if (overflow) continue;
                if (line.Length == MaximumLineLength)
                {
                    overflow = true;
                    line.Clear();
                    error.WriteLine($"rnsd: interactive input exceeds {MaximumLineLength} characters; discarded the entire line.");
                    error.Flush();
                }
                else line.Append(c);
            }
        }
    }

    private static async Task<bool> ExecuteAsync(string command, RnsdHost host, TextWriter output, TextWriter error, CancellationToken stop)
    {
        switch (command.Trim().ToLowerInvariant())
        {
            case "":
                return true;
            case "help":
                output.WriteLine(Help);
                break;
            case "status":
                output.WriteLine("Status:");
                foreach (var entry in host.GetStatistics().OrderBy(entry => Convert.ToString(entry.Key, CultureInfo.InvariantCulture), StringComparer.Ordinal))
                {
                    if (entry.Key is "interfaces" && entry.Value is Array interfaces)
                        output.WriteLine($"  interfaces: {interfaces.Length}");
                    else output.WriteLine($"  {entry.Key}: {Format(entry.Value)}");
                }
                break;
            case "interfaces":
                var configured = host.Interfaces;
                output.WriteLine($"Interfaces: {configured.Count}");
                foreach (var (name, packetInterface) in configured)
                    output.WriteLine($"  {name} ({packetInterface.GetType().Name})");
                break;
            case "persist":
                try
                {
                    await host.PersistAsync(stop).ConfigureAwait(false);
                    output.WriteLine("State persisted.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    error.WriteLine($"rnsd: persist failed: {ex.Message}");
                    error.Flush();
                }
                break;
            case "quit" or "exit":
                return false;
            default:
                error.WriteLine("rnsd: unknown interactive command; use help. Arbitrary code execution is not supported.");
                error.Flush();
                break;
        }
        output.Flush();
        return true;
    }

    private static string Format(object? value) => value switch
    {
        null => "unavailable",
        byte[] bytes => Convert.ToHexStringLower(bytes),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "unavailable",
    };
}
