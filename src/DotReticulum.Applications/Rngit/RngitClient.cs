using System.Diagnostics;
using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public sealed record RngitAddress(byte[] DestinationHash, string Repository)
{
    public static RngitAddress Parse(string url)
    {
        var parts = url.Split('/');
        if (parts is not ["rns:", "", var hash, var group, var repository]
            || hash.Length != 32 || !hash.All(Uri.IsHexDigit) || !ValidComponent(group) || !ValidComponent(repository))
        {
            throw new ArgumentException("Expected rns://<32 hexadecimal destination characters>/<group>/<repository> (no query, fragment or escaping).");
        }

        return new(Convert.FromHexString(hash), group + "/" + repository);
    }

    private static bool ValidComponent(string value) => value.Length is > 0 and <= 256 && value is not "." and not ".."
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

public sealed record RngitCloneResult(string Directory, string? Head, string? Tree, int RefCount, long NetworkBundleBytes);

/// <summary>
/// Read-only upstream rngit client: /git/list and /git/fetch on an identified git.repositories link.
/// Network transport is entirely .NET RNS; git is invoked only to validate/import local bundles and refs.
/// </summary>
public sealed class RngitClient(ReticulumNode node, Identity identity, Action<string>? progress = null)
{
    public const long DefaultMaxResponseBytes = 256L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal Task<RngitSession> OpenSessionAsync(string url, TimeSpan timeout, long limit, CancellationToken stop) =>
        RngitSession.OpenAsync(node, identity, url, timeout, limit, progress, stop);

    public Task<RngitCloneResult> CloneAsync(string url, string directory, TimeSpan timeout,
        long maxResponseBytes = DefaultMaxResponseBytes, CancellationToken cancellationToken = default, bool singleBranch = false) =>
        TransferAsync(url, directory, timeout, maxResponseBytes, cancellationToken, fetch: false, singleBranch);

    public Task<RngitCloneResult> FetchAsync(string url, string directory, TimeSpan timeout,
        long maxResponseBytes = DefaultMaxResponseBytes, CancellationToken cancellationToken = default, bool singleBranch = false) =>
        TransferAsync(url, directory, timeout, maxResponseBytes, cancellationToken, fetch: true, singleBranch);

    private async Task<RngitCloneResult> TransferAsync(string url, string directory, TimeSpan timeout,
        long maxResponseBytes, CancellationToken cancellationToken, bool fetch, bool singleBranch)
    {
        RngitAddress.Parse(url);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
        var target = Path.GetFullPath(directory);
        if (fetch && !Directory.Exists(target)) throw new IOException("Fetch destination must be an existing bare Git directory.");
        if (!fetch && (Directory.Exists(target) || File.Exists(target))) throw new IOException("Clone destination already exists: " + target);
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Clone destination must have a parent directory.");
        Directory.CreateDirectory(parent);
        var staging = fetch ? target : Path.Combine(parent, ".rngit-" + Guid.NewGuid().ToString("N"));
        if (!fetch) Directory.CreateDirectory(staging);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stop = deadline.Token;
        try
        {
            var localRefs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (fetch)
            {
                if ((await GitAsync(staging, ["rev-parse", "--is-bare-repository"], stop).ConfigureAwait(false)).Trim() != "true")
                {
                    throw new IOException("Fetch destination must be a bare Git directory, not a working tree.");
                }

                var localListing = await GitAsync(staging, ["for-each-ref", "--format=%(objectname) %(refname)"], stop).ConfigureAwait(false);
                foreach (var line in localListing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.TrimEnd('\r').Split(' ');
                    if (parts.Length != 2) throw new InvalidDataException("Malformed local ref listing.");
                    localRefs.Add(parts[1], parts[0]);
                }
            }

            using var session = await OpenSessionAsync(url, timeout, maxResponseBytes, stop).ConfigureAwait(false);
            var refs = session.Refs;
            var headRef = session.Head;
            if (singleBranch) refs = refs.Where(r => r.Key == headRef).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
            var objectFormat = refs.Count > 0 && refs.First().Value.Length == 64 ? "sha256" : "sha1";
            if (!fetch) await GitAsync(staging, ["init", "--bare", "--object-format=" + objectFormat, "."], stop).ConfigureAwait(false);
            else if (refs.Count > 0 && (await GitAsync(staging, ["rev-parse", "--show-object-format"], stop).ConfigureAwait(false)).Trim() != objectFormat)
            {
                throw new InvalidDataException("Local and remote Git object formats differ.");
            }
            var bundleBytes = await session.ImportAsync(staging, refs, stop).ConfigureAwait(false);
            string? head = null, tree = null;
            if (refs.TryGetValue(headRef, out var headSha))
            {
                head = (await GitAsync(staging, ["rev-parse", "--verify", headSha + "^{commit}"], stop).ConfigureAwait(false)).Trim();
                tree = (await GitAsync(staging, ["rev-parse", "--verify", headSha + "^{tree}"], stop).ConfigureAwait(false)).Trim();
            }

            if (refs.Count > 0)
            {
                var transaction = new StringBuilder("start\n");
                foreach (var (name, sha) in refs)
                {
                    if (localRefs.TryGetValue(name, out var old)) transaction.Append("update ").Append(name).Append(' ').Append(sha).Append(' ').Append(old).Append('\n');
                    else transaction.Append("create ").Append(name).Append(' ').Append(sha).Append('\n');
                }
                transaction.Append("prepare\ncommit\n");
                await GitAsync(staging, ["update-ref", "--no-deref", "--stdin"], stop, transaction.ToString()).ConfigureAwait(false);
            }

            await GitAsync(staging, ["symbolic-ref", "HEAD", headRef], stop).ConfigureAwait(false);
            await GitAsync(staging, ["config", fetch ? "remote.rngit.url" : "remote.origin.url", url], stop).ConfigureAwait(false);

            stop.ThrowIfCancellationRequested();
            if (!fetch) Directory.Move(staging, target);
            return new(target, head, tree, refs.Count, bundleBytes);
        }
        finally
        {
            if (!fetch && Directory.Exists(staging))
            {
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                }

                Directory.Delete(staging, recursive: true);
            }
        }
    }

    internal static (Dictionary<string, string> Refs, string Head) ParseRefs(string text)
    {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        string? head = null;
        int? shaLength = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ');
            if (parts.Length != 2) throw new InvalidDataException("Malformed remote ref listing.");
            if (parts[1] == "HEAD" && parts[0].StartsWith('@'))
            {
                if (head is not null) throw new InvalidDataException("Duplicate remote HEAD.");
                head = parts[0][1..];
            }
            else
            {
                var sha = parts[0];
                if (sha.Length is not (40 or 64) || !sha.All(Uri.IsHexDigit) || !parts[1].StartsWith("refs/", StringComparison.Ordinal)
                    || !refs.TryAdd(parts[1], sha) || (shaLength.HasValue && shaLength != sha.Length))
                {
                    throw new InvalidDataException("Invalid, duplicate or mixed-format remote ref.");
                }

                shaLength = sha.Length;
            }
        }

        if (head is null || !head.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Server did not advertise a valid symbolic branch HEAD (detached remote HEAD is unsupported).");
        }

        return (refs, head);
    }

    internal static async Task<string> GitAsync(string directory, string[] args, CancellationToken stop, string? input = null)
    {
        var info = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = directory,
            StandardInputEncoding = StrictUtf8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "--no-pager", "--git-dir=" + directory, "-c", "core.hooksPath=", "-c", "protocol.allow=never" }.Concat(args)) info.ArgumentList.Add(arg);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info) ?? throw new IOException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), stop).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(stop).ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException($"git {args[0]} failed ({process.ExitCode}): {error.Trim()}");
        return output;
    }
}
