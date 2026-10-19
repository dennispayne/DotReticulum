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
        Action<int, string>? log = null;
        try
        {
            if (args is not [var remote, var url] || string.IsNullOrWhiteSpace(remote))
                throw new ArgumentException("Usage: git-remote-rns <remote-name> <rns://destination/group/repository>");
            var addressParts = url.Split('/');
            if (addressParts is not ["rns:", "", var destination, var group, var repository]
                || destination.Length == 0 || destination.Any(char.IsWhiteSpace))
                throw new ArgumentException("Expected rns://<destination hash or alias>/<group>/<repository>.");
            RngitAddress.Parse($"rns://0123456789abcdef0123456789abcdef/{group}/{repository}");
            var seconds = double.Parse(Environment.GetEnvironmentVariable("DOTRETICULUM_RNGIT_TIMEOUT") ?? "7200", CultureInfo.InvariantCulture);
            var limit = long.Parse(Environment.GetEnvironmentVariable("DOTRETICULUM_RNGIT_MAX_RESPONSE") ?? "268435456", CultureInfo.InvariantCulture);
            var pushLimit = long.Parse(Environment.GetEnvironmentVariable("DOTRETICULUM_RNGIT_MAX_PUSH") ?? "268435456", CultureInfo.InvariantCulture);
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > uint.MaxValue - 1 || limit <= 0 || pushLimit <= 0)
                throw new ArgumentException("Timeout and maximum response size must be positive; timeout must not exceed 4294967 seconds.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var timeout = TimeSpan.FromSeconds(seconds);
            deadline.CancelAfter(timeout);
            var token = deadline.Token;
            var progressEnabled = false;
            var queue = new Dictionary<string, string>(StringComparer.Ordinal);
            var pushes = new List<RngitPushSpec>();
            var pushTargets = new HashSet<string>(StringComparer.Ordinal);
            var pushListed = false;
            async Task<RngitSession> Session(bool forPush)
            {
                if (session is not null)
                {
                    await session.ListAsync(forPush, token).ConfigureAwait(false);
                    return session;
                }
                var clientConfig = RngitClientConfiguration.Load();
                var resolvedUrl = clientConfig.ResolveUrl(url);
                log = CreateLogger(clientConfig, error);
                var config = Environment.GetEnvironmentVariable("DOTRETICULUM_RNS_CONFIG") ?? Environment.GetEnvironmentVariable("RNS_CONFIG");
                node = ReticulumNode.Open(config, log);
                identity = clientConfig.LoadOrCreateIdentity();
                if (OperatingSystem.IsWindows())
                    await error.WriteLineAsync("git-remote-rns: Protect client_identity with a private directory ACL on Windows.").ConfigureAwait(false);
                await node.StartAsync(token).ConfigureAwait(false);
                session = await new RngitClient(node, identity, message =>
                {
                    log(4, message);
                    if (progressEnabled) error.WriteLine(message);
                }).OpenSessionAsync(resolvedUrl, timeout, limit, token, forPush, clientConfig.RefBatchSize).ConfigureAwait(false);
                return session;
            }
            while (await ReadCommandAsync(input, token).ConfigureAwait(false) is { } line)
            {
                if (queue.Count > 0 && line.Length > 0 && !line.StartsWith("fetch ", StringComparison.Ordinal))
                    throw new InvalidDataException("Expected fetch command or blank batch terminator.");
                if (pushes.Count > 0 && line.Length > 0 && !line.StartsWith("push ", StringComparison.Ordinal))
                    throw new InvalidDataException("Expected push command or blank batch terminator.");
                switch (line)
                {
                    case "capabilities":
                        await output.WriteAsync("fetch\npush\noption\n\n").ConfigureAwait(false);
                        break;
                    case "list":
                    case "list for-push":
                        pushListed = line == "list for-push";
                        var listing = await Session(pushListed).ConfigureAwait(false);
                        if (listing.Refs.Values.Any(sha => sha.Length != 40))
                            throw new InvalidDataException("git-remote-rns supports SHA-1 repositories only; SHA-256 negotiation is not implemented.");
                        foreach (var (name, sha) in listing.Refs)
                            await output.WriteAsync($"{sha} {name}\n").ConfigureAwait(false);
                        await output.WriteAsync($"@{listing.Head} HEAD\n\n").ConfigureAwait(false);
                        break;
                    case "":
                        if (queue.Count == 0 && pushes.Count == 0) return 0;
                        var transfer = session ?? throw new InvalidDataException("Transfer requires a preceding list command.");
                        var gitDir = Environment.GetEnvironmentVariable("GIT_DIR");
                        if (string.IsNullOrWhiteSpace(gitDir)) throw new InvalidDataException("Git must supply GIT_DIR for object transfer.");
                        if (queue.Count > 0)
                        {
                            var received = await transfer.ImportAsync(Path.GetFullPath(gitDir), queue, token).ConfigureAwait(false);
                            await error.WriteLineAsync($"Received {received} bundle bytes over Reticulum").ConfigureAwait(false);
                            queue.Clear();
                        }
                        foreach (var spec in pushes)
                        {
                            string? failure;
                            try
                            {
                                var result = await transfer.PushAsync(Path.GetFullPath(gitDir), spec, token, pushLimit).ConfigureAwait(false);
                                failure = result.Error;
                                await error.WriteLineAsync($"Sent {result.BundleBytes} bundle bytes over Reticulum for {spec.Target}").ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                or InvalidOperationException or System.ComponentModel.Win32Exception)
                            {
                                failure = ex.Message;
                            }
                            if (failure is null) await output.WriteAsync($"ok {spec.Target}\n").ConfigureAwait(false);
                            else
                            {
                                log?.Invoke(1, $"Push {spec.Target}: {failure}");
                                await output.WriteAsync($"error {spec.Target} {RngitSession.QuoteStatus(failure)}\n").ConfigureAwait(false);
                            }
                            await output.FlushAsync(token).ConfigureAwait(false);
                        }
                        pushes.Clear();
                        pushTargets.Clear();
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
                        else if (line.StartsWith("push ", StringComparison.Ordinal))
                        {
                            if (session is null || !pushListed) throw new InvalidDataException("Push requires a preceding list for-push command.");
                            var spec = RngitPushSpec.Parse(line[5..]);
                            if (pushes.Count >= 65536) throw new InvalidDataException("Push batch exceeds 65536 refs.");
                            if (!pushTargets.Add(spec.Target)) throw new InvalidDataException("Duplicate destination ref in push batch.");
                            pushes.Add(spec);
                        }
                        else throw new InvalidDataException("Unsupported Git helper command: " + line);
                        break;
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            if (queue.Count > 0 || pushes.Count > 0) throw new InvalidDataException("EOF before transfer batch terminator.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync((stop.IsCancellationRequested ? "git-remote-rns: Cancelled." : "git-remote-rns: Overall timeout expired.")
                + " An in-flight remote ref operation may already have been applied.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException or InvalidDataException
            or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log?.Invoke(1, ex.Message);
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

    private static Action<int, string> CreateLogger(RngitClientConfiguration config, TextWriter error)
    {
        using (new FileStream(config.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
        var gate = new object();
        return (level, message) =>
        {
            if (level > config.LogLevel) return;
            lock (gate)
            {
                try
                {
                    if (new FileInfo(config.LogPath).Length > 5 * 1024 * 1024)
                        File.Move(config.LogPath, config.LogPath + ".1", overwrite: true);
                    File.AppendAllText(config.LogPath, $"[{DateTimeOffset.Now:O}] [{level}] {message}{Environment.NewLine}", new UTF8Encoding(false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error.WriteLine($"git-remote-rns: Could not write client_log: {ex.Message}");
                }
            }
        };
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
