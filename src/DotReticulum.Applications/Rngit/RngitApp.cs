using System.Globalization;

namespace DotReticulum.Applications.Rngit;

public static class RngitApp
{
    public const string Usage = """
        DotReticulum rngit - experimental read-only Git over Reticulum

        Usage:
          rngit clone <rns://destination/group/repository> <new-directory> [options]
          rngit fetch <rns://destination/group/repository> <existing-bare-directory> [options]

        Creates a bare repository; requires git on PATH. Never pushes or publishes.
        Options:
          --config <path>        .NET Reticulum config directory or file (default ~/.dotreticulum)
          --timeout <seconds>    Overall deadline including path, link, transfers and local git (default 7200)
          --max-response <bytes> Maximum response resource size (default 268435456; buffered in memory)
          --single-branch        Transfer only the remote default branch (no other branches/tags)
          -h, --help             Show help
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        string? config = null;
        var seconds = 7200.0;
        var limit = RngitClient.DefaultMaxResponseBytes;
        var singleBranch = false;
        var positional = new List<string>();
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
                switch (args[i])
                {
                    case "-h" or "--help": output.WriteLine(Usage); return 0;
                    case "--config": config = Next(); break;
                    case "--timeout": seconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--max-response": limit = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--single-branch": singleBranch = true; break;
                    default:
                        if (args[i].StartsWith('-')) throw new ArgumentException("Unknown option: " + args[i]);
                        positional.Add(args[i]);
                        break;
                }
            }

            if (positional is not [var operation, var url, var directory] || operation is not ("clone" or "fetch")) throw new ArgumentException(Usage);
            RngitAddress.Parse(url);
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > uint.MaxValue - 1 || limit <= 0)
            {
                throw new ArgumentException("Timeout and maximum response size must be positive; timeout must not exceed 4294967 seconds.");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
            await using var node = ReticulumNode.Open(config, (_, message) => error.WriteLine(message));
            using var identity = node.LoadOrCreateIdentity("rngit");
            await node.StartAsync(deadline.Token).ConfigureAwait(false);
            var client = new RngitClient(node, identity, message => error.WriteLine(message));
            var result = operation == "clone"
                ? await client.CloneAsync(url, directory, TimeSpan.FromSeconds(seconds), limit, deadline.Token, singleBranch).ConfigureAwait(false)
                : await client.FetchAsync(url, directory, TimeSpan.FromSeconds(seconds), limit, deadline.Token, singleBranch).ConfigureAwait(false);
            output.WriteLine($"{(operation == "clone" ? "Cloned" : "Fetched")} {result.RefCount} refs into bare repository {result.Directory}");
            output.WriteLine($"Received {result.NetworkBundleBytes} bundle bytes over Reticulum");
            output.WriteLine($"HEAD {result.Head ?? "(unborn)"}");
            output.WriteLine($"TREE {result.Tree ?? "(none)"}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            error.WriteLine("rngit: " + ex.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine(stop.IsCancellationRequested ? "rngit: Cancelled." : "rngit: Overall timeout expired.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine("rngit: " + ex.Message);
            return 1;
        }
    }
}
