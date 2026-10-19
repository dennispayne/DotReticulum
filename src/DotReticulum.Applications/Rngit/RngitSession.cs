using System.Diagnostics;
using System.Text;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rngit;

// Owns one identified link and imports objects only. Ref ownership belongs to the caller.
internal sealed class RngitSession : IDisposable
{
    private readonly Link link;
    private readonly RngitAddress address;
    private readonly TimeSpan timeout;
    private readonly long limit;
    private readonly Action<string>? progress;
    internal Dictionary<string, string> Refs { get; private set; } = new(StringComparer.Ordinal);
    internal string Head { get; private set; } = "";

    private RngitSession(Link link, RngitAddress address, TimeSpan timeout, long limit, Action<string>? progress)
    {
        this.link = link;
        this.address = address;
        this.timeout = timeout;
        this.limit = limit;
        this.progress = progress;
    }

    internal static async Task<RngitSession> OpenAsync(ReticulumNode node, Identity identity, string url,
        TimeSpan timeout, long limit, Action<string>? progress, CancellationToken stop)
    {
        var address = RngitAddress.Parse(url);
        progress?.Invoke("Resolving path and establishing link...");
        var connection = await ClientLinks.ConnectAsync(node, address.DestinationHash, timeout, stop).ConfigureAwait(false);
        var link = connection.Link ?? throw new IOException(connection.Error);
        var session = new RngitSession(link, address, timeout, limit, progress);
        try
        {
            link.Identify(identity);
            var listing = await session.RequestAsync("/git/list", new Dictionary<object, object?>
            {
                [0] = address.Repository, ["for_push"] = false,
            }, Math.Min(limit, 4 * 1024 * 1024), stop).ConfigureAwait(false);
            if (listing.Metadata is not null) throw new InvalidDataException("Unexpected metadata on list response.");
            CheckStatus(listing.Data, "list");
            (session.Refs, session.Head) = RngitClient.ParseRefs(new UTF8Encoding(false, true).GetString(listing.Data.AsSpan(1)));
            // Validate before a remote string can enter Git's line-oriented protocol or ref transactions.
            foreach (var name in session.Refs.Keys.Append(session.Head).Distinct())
            {
                if (!ValidRef(name)) throw new InvalidDataException("Invalid remote ref name.");
            }
            progress?.Invoke($"Remote HEAD {session.Head} {session.Refs.GetValueOrDefault(session.Head, "(unborn)")} ({session.Refs.Count} advertised refs)");
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    internal static bool ValidRef(string name) =>
        name.StartsWith("refs/", StringComparison.Ordinal) && !name.EndsWith('.') && !name.Contains("..", StringComparison.Ordinal)
        && !name.Contains("@{", StringComparison.Ordinal) && !name.Any(c => char.IsControl(c) || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')
        && name.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));

    internal async Task<long> ImportAsync(string directory, IReadOnlyDictionary<string, string> requested, CancellationToken stop)
    {
        foreach (var (name, sha) in requested)
        {
            if (!Refs.TryGetValue(name, out var advertised) || advertised != sha)
                throw new InvalidDataException("Fetch ref/object does not match the advertised snapshot.");
        }
        var format = (await RngitClient.GitAsync(directory, ["rev-parse", "--show-object-format"], stop).ConfigureAwait(false)).Trim();
        if (Refs.Count > 0 && (format == "sha256" ? 64 : 40) != Refs.First().Value.Length)
            throw new InvalidDataException("Local and remote Git object formats differ.");
        var localListing = await RngitClient.GitAsync(directory, ["for-each-ref", "--format=%(objectname)"], stop).ConfigureAwait(false);
        var have = localListing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        var objects = await RngitClient.GitAsync(directory, ["rev-list", "--objects", "--all", "--no-object-names"], stop).ConfigureAwait(false);
        var known = objects.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var batch in requested.Chunk(25))
        {
            var request = new Dictionary<object, object?>
            {
                [0] = address.Repository,
                ["refs"] = batch.Select(r => (object?)new Dictionary<object, object?> { ["sha"] = r.Value, ["ref"] = r.Key }).ToArray(),
            };
            if (have.Length > 0) request["have"] = have.Cast<object?>().ToArray();
            var response = await RequestAsync("/git/fetch", request, limit, stop).ConfigureAwait(false);
            if (response.Metadata is null)
            {
                CheckStatus(response.Data, "fetch");
                if (response.Data.Length != 1) throw new InvalidDataException("Unexpected non-bundle fetch response.");
            }
            else
            {
                if (response.Metadata is not Dictionary<object, object?> metadata
                    || !metadata.TryGetValue(1L, out var status) || status is not long code || code != 0)
                    throw new InvalidDataException("Missing or unsuccessful bundle result metadata.");
                bytes += response.Data.LongLength;
                var bundle = Path.Combine(directory, "rngit-fetch-" + Guid.NewGuid().ToString("N") + ".bundle");
                try
                {
                    await File.WriteAllBytesAsync(bundle, response.Data, stop).ConfigureAwait(false);
                    await RngitClient.GitAsync(directory, ["bundle", "verify", bundle], stop).ConfigureAwait(false);
                    var heads = await RngitClient.GitAsync(directory, ["bundle", "list-heads", bundle], stop).ConfigureAwait(false);
                    var advertised = heads.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
                    if (batch.Any(r => !advertised.Contains(r.Value + " " + r.Key) && !known.Contains(r.Value)))
                        throw new InvalidDataException("Remote refs changed while fetching, or bundle refs do not match list response. Retry.");
                    await RngitClient.GitAsync(directory, ["bundle", "unbundle", bundle], stop).ConfigureAwait(false);
                }
                finally { File.Delete(bundle); }
            }
            // A status-only success must not masquerade as imported objects.
            foreach (var sha in batch.Select(r => r.Value).Distinct())
                await RngitClient.GitAsync(directory, ["cat-file", "-e", sha], stop).ConfigureAwait(false);
        }
        // During clone Git deliberately uses an invalid placeholder HEAD until it owns the final refs.
        await RngitClient.GitAsync(directory, ["fsck", "--full", "--strict", "--no-reflogs", .. requested.Values.Distinct()], stop).ConfigureAwait(false);
        return bytes;
    }

    private async Task<(byte[] Data, object? Metadata)> RequestAsync(string path, object data, long sizeLimit, CancellationToken stop)
    {
        var lastProgress = 0L;
        var receipt = link.Request(path, data, timeout: timeout.TotalSeconds, progressCallback: receipt =>
        {
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(lastProgress, now) >= TimeSpan.FromSeconds(2))
            {
                lastProgress = now;
                progress?.Invoke($"{path}: {receipt.Progress:P1}");
            }
        }) ?? throw new IOException("Link closed before " + path);
        receipt.MaxResponseSize = sizeLimit;
        var response = await receipt.ResponseTask.WaitAsync(stop).ConfigureAwait(false);
        if (response is not byte[] bytes || bytes.LongLength > sizeLimit) throw new InvalidDataException("Invalid or oversized response to " + path);
        return (bytes, receipt.Metadata);
    }

    private static void CheckStatus(byte[] bytes, string operation)
    {
        if (bytes.Length == 0) throw new InvalidDataException("Empty " + operation + " response.");
        if (bytes[0] != 0) throw new IOException($"Server refused {operation} (status {bytes[0]}): {Encoding.UTF8.GetString(bytes.AsSpan(1))}");
    }

    public void Dispose() => link.Teardown();
}
