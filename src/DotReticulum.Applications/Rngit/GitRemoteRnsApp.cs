using System.Globalization;
using System.Text;

namespace DotReticulum.Applications.Rngit;

public static class GitRemoteRnsApp
{
    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error,
        CancellationToken stop)
    {
        ReticulumNode? node = null;
        Core.Identity? identity = null;
        RngitSession? session = null;
        try
        {
            if (args is not [var remote, var url] || string.IsNullOrWhiteSpace(remote))
                throw new ArgumentException("Usage: git-remote-rns <remote-name> <rns://destination/group/repository>");
            RngitAddress.Parse(url);
            var seconds = double.Parse(Environment.GetEnvironmentVariable("DOTRETICULUM_RNGIT_TIMEOUT") ?? "7200", CultureInfo.InvariantCulture);
            var limit = long.Parse(Environment.GetEnvironmentVariable("DOTRETICULUM_RNGIT_MAX_RESPONSE") ?? "268435456", CultureInfo.InvariantCulture);
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > uint.MaxValue - 1 || limit <= 0)
                throw new ArgumentException("Timeout and maximum response size must be positive; timeout must not exceed 4294967 seconds.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var timeout = TimeSpan.FromSeconds(seconds);
            deadline.CancelAfter(timeout);
            var token = deadline.Token;
            var progressEnabled = false;
            var queue = new Dictionary<string, string>(StringComparer.Ordinal);
            async Task<RngitSession> Session()
            {
                if (session is not null) return session;
                var config = Environment.GetEnvironmentVariable("DOTRETICULUM_RNS_CONFIG") ?? Environment.GetEnvironmentVariable("RNS_CONFIG");
                node = ReticulumNode.Open(config, (_, message) => error.WriteLine(message));
                identity = node.LoadOrCreateIdentity("rngit");
                await node.StartAsync(token).ConfigureAwait(false);
                session = await new RngitClient(node, identity, message =>
                {
                    if (progressEnabled) error.WriteLine(message);
                }).OpenSessionAsync(url, timeout, limit, token).ConfigureAwait(false);
                return session;
            }
            while (await ReadCommandAsync(input, token).ConfigureAwait(false) is { } line)
            {
                if (queue.Count > 0 && line.Length > 0 && !line.StartsWith("fetch ", StringComparison.Ordinal))
                    throw new InvalidDataException("Expected fetch command or blank batch terminator.");
                switch (line)
                {
                    case "capabilities":
                        await output.WriteAsync("fetch\noption\n\n").ConfigureAwait(false);
                        break;
                    case "list":
                        var listing = await Session().ConfigureAwait(false);
                        foreach (var (name, sha) in listing.Refs)
                            await output.WriteAsync($"{sha} {name}\n").ConfigureAwait(false);
                        await output.WriteAsync($"@{listing.Head} HEAD\n\n").ConfigureAwait(false);
                        break;
                    case "list for-push":
                        throw new InvalidDataException("Push is not supported by this read-only helper.");
                    case "":
                        if (queue.Count == 0) return 0;
                        var transfer = await Session().ConfigureAwait(false);
                        var gitDir = Environment.GetEnvironmentVariable("GIT_DIR");
                        if (string.IsNullOrWhiteSpace(gitDir)) throw new InvalidDataException("Git must supply GIT_DIR for object import.");
                        var received = await transfer.ImportAsync(Path.GetFullPath(gitDir), queue, token).ConfigureAwait(false);
                        await error.WriteLineAsync($"Received {received} bundle bytes over Reticulum").ConfigureAwait(false);
                        queue.Clear();
                        await output.WriteAsync("\n").ConfigureAwait(false);
                        break;
                    default:
                        if (line.StartsWith("option ", StringComparison.Ordinal))
                        {
                            var parts = line.Split(' ', 3);
                            if (parts.Length != 3 || parts[1].Length == 0 || parts[2].Length == 0)
                                throw new InvalidDataException("Malformed option command.");
                            if (parts[1] == "progress")
                            {
                                if (parts[2] is not ("true" or "false"))
                                    await output.WriteAsync("error invalid progress value\n").ConfigureAwait(false);
                                else
                                {
                                    progressEnabled = parts[2] == "true";
                                    await output.WriteAsync("ok\n").ConfigureAwait(false);
                                }
                            }
                            else await output.WriteAsync("unsupported\n").ConfigureAwait(false);
                        }
                        else if (line.StartsWith("fetch ", StringComparison.Ordinal))
                        {
                            if (session is null) throw new InvalidDataException("Fetch requires a preceding list command.");
                            var parts = line.Split(' ');
                            if (parts.Length != 3 || !session.Refs.TryGetValue(parts[2], out var sha) || sha != parts[1])
                                throw new InvalidDataException("Malformed fetch or unadvertised ref/object.");
                            if (queue.Count >= 65536) throw new InvalidDataException("Fetch batch exceeds 65536 refs.");
                            queue[parts[2]] = sha;
                        }
                        else throw new InvalidDataException("Unsupported Git helper command: " + line);
                        break;
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            if (queue.Count > 0) throw new InvalidDataException("EOF before fetch batch terminator.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync(stop.IsCancellationRequested ? "git-remote-rns: Cancelled." : "git-remote-rns: Overall timeout expired.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException or InvalidDataException
            or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await error.WriteLineAsync("git-remote-rns: " + ex.Message).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            session?.Dispose();
            identity?.Dispose();
            if (node is not null) await node.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReadCommandAsync(TextReader input, CancellationToken stop)
    {
        var text = new StringBuilder();
        var buffer = new char[1];
        // Git's Windows stdin pipe may not support cancelling the underlying read.
        while (await input.ReadAsync(buffer.AsMemory(), stop).AsTask().WaitAsync(stop).ConfigureAwait(false) != 0)
        {
            if (buffer[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= 8192 || buffer[0] == '\0') throw new InvalidDataException("Malformed or oversized helper command.");
            text.Append(buffer[0]);
        }
        if (text.Length != 0) throw new InvalidDataException("Truncated helper command.");
        return null;
    }
}
