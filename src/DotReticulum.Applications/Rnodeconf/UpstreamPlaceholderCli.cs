namespace DotReticulum.Applications.Rnodeconf;

/// <summary>Observable shells only; pinned upstream has no resolver or package operation implementation.</summary>
public static class UpstreamPlaceholderCli
{
    public static async Task<int> RunAsync(string tool, string[] args, TextWriter output, TextWriter error, CancellationToken token = default)
    {
        if (tool is not ("rnir" or "rnpkg")) throw new ArgumentException("Unknown upstream placeholder.", nameof(tool));
        string? config = null;
        var verbosity = 0;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--version": await output.WriteLineAsync($"{tool} 1.5.7 (DotReticulum placeholder shell)").ConfigureAwait(false); return 0;
                    case "-h" or "--help":
                        await output.WriteLineAsync($"{tool}: upstream placeholder; initializes Reticulum and exits. No resolution/package operations.\n--config PATH -v/--verbose -q/--quiet --version" +
                            (tool == "rnpkg" ? " --exampleconfig" : "")).ConfigureAwait(false); return 0;
                    case "--exampleconfig" when tool == "rnpkg":
                        await output.WriteLineAsync("# This is an example package manager configuration file.\n").ConfigureAwait(false); return 0;
                    case "--config": if (++i == args.Length) throw new ArgumentException("Missing --config path."); config = args[i]; break;
                    case "-v" or "--verbose": verbosity++; break;
                    case "-q" or "--quiet": verbosity--; break;
                    default:
                        if (args[i].Length > 2 && args[i][0] == '-' && args[i].AsSpan(1).IndexOfAnyExcept('v', 'q') < 0)
                            foreach (var character in args[i].AsSpan(1)) verbosity += character == 'v' ? 1 : -1;
                        else throw new ArgumentException("Unsupported option; upstream implements no resolver/package operations.");
                        break;
                }
            }
            await using var node = ReticulumNode.Open(config, CliSupport.Logger(error, Math.Clamp(verbosity, 0, 8)));
            await node.StartAsync(token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 130; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await error.WriteLineAsync($"{tool}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
