using System.Diagnostics;
using System.Text;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rngit;

// Owns one identified link. Fetch imports objects only; Git owns the caller's refs.
internal sealed partial class RngitSession : IDisposable
{
    private readonly Link link;
    private readonly RngitAddress address;
    private readonly TimeSpan timeout;
    private readonly long limit;
    private readonly Action<string>? progress;
    private readonly int refBatchSize;
    internal Dictionary<string, string> Refs { get; private set; } = new(StringComparer.Ordinal);
    internal string Head { get; private set; } = "";

    private RngitSession(Link link, RngitAddress address, TimeSpan timeout, long limit, Action<string>? progress, int refBatchSize)
    {
        this.link = link;
        this.address = address;
        this.timeout = timeout;
        this.limit = limit;
        this.progress = progress;
        this.refBatchSize = refBatchSize;
    }

    internal static async Task<RngitSession> OpenAsync(ReticulumNode node, Identity identity, string url,
        TimeSpan timeout, long limit, Action<string>? progress, CancellationToken stop, bool forPush = false, int refBatchSize = 25)
    {
        if (refBatchSize is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(refBatchSize));
        var address = RngitAddress.Parse(url);
        progress?.Invoke("Resolving path and establishing link...");
        var connection = await ClientLinks.ConnectAsync(node, address.DestinationHash, timeout, stop).ConfigureAwait(false);
        var link = connection.Link ?? throw new IOException(connection.Error);
        var session = new RngitSession(link, address, timeout, limit, progress, refBatchSize);
        try
        {
            link.Identify(identity);
            await session.ListAsync(forPush, stop).ConfigureAwait(false);
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    internal async Task ListAsync(bool forPush, CancellationToken stop)
    {
        var listing = await RequestAsync("/git/list", new Dictionary<object, object?>
        {
            [0] = address.Repository, ["for_push"] = forPush,
        }, Math.Min(limit, 4 * 1024 * 1024), stop).ConfigureAwait(false);
        if (listing.Metadata is not null) throw new InvalidDataException("Unexpected metadata on list response.");
        CheckStatus(listing.Data, "list");
        var (refs, head) = RngitClient.ParseRefs(new UTF8Encoding(false, true).GetString(listing.Data.AsSpan(1)));
        // Validate before a remote string can enter Git's line-oriented protocol or ref transactions.
        foreach (var name in refs.Keys.Append(head).Distinct())
        {
            if (!ValidRef(name)) throw new InvalidDataException("Invalid remote ref name.");
        }
        Refs = refs;
        Head = head;
        progress?.Invoke($"Remote HEAD {Head} {Refs.GetValueOrDefault(Head, "(unborn)")} ({Refs.Count} advertised refs)");
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
        foreach (var batch in requested.Chunk(refBatchSize))
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
        await RngitClient.VerifyImportedObjectsAsync(directory, format, requested.Values, stop).ConfigureAwait(false);
        return bytes;
    }

    private async Task<(byte[] Data, object? Metadata)> RequestAsync(string path, object data, long sizeLimit, CancellationToken stop)
    {
        var lastProgress = 0L;
        var receipt = link.Request(path, data, timeout: timeout.TotalSeconds, maxResponseSize: sizeLimit, cancellationToken: stop, progressCallback: receipt =>
        {
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(lastProgress, now) >= TimeSpan.FromSeconds(2))
            {
                lastProgress = now;
                progress?.Invoke($"{path}: {receipt.Progress:P1}");
            }
        }) ?? throw new IOException("Link closed before " + path);
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
