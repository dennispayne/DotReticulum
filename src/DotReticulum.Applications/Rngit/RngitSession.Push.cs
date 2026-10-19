using System.Globalization;
using System.Text;

namespace DotReticulum.Applications.Rngit;

internal sealed record RngitPushSpec(string Source, string Target, bool Force)
{
    internal static RngitPushSpec Parse(string refspec)
    {
        var parts = refspec.Split(':');
        if (parts.Length != 2 || !RngitSession.ValidRef(parts[1]))
            throw new InvalidDataException("Malformed push refspec or invalid destination ref.");
        var force = parts[0].StartsWith('+');
        var source = force ? parts[0][1..] : parts[0];
        if ((force && source.Length == 0) || source.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new InvalidDataException("Malformed push source.");
        return new(source, parts[1], force);
    }
}

internal sealed record RngitPushResult(string? Error, long BundleBytes);

internal sealed partial class RngitSession
{
    // Pinned client.py:545-676 and server.py:3005-3140. Each ref is independent,
    // and existing-object operations require explicit force for any changed SHA.
    internal async Task<RngitPushResult> PushAsync(string directory, RngitPushSpec spec, CancellationToken stop,
        long maxBundleBytes = RngitClient.DefaultMaxResponseBytes)
    {
        if (maxBundleBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBundleBytes));
        if ((await RngitClient.GitAsync(directory, ["rev-parse", "--show-object-format"], stop).ConfigureAwait(false)).Trim() != "sha1"
            || Refs.Values.Any(sha => sha.Length != 40))
            throw new InvalidDataException("git-remote-rns supports SHA-1 repositories only; SHA-256 negotiation is not implemented.");
        if (spec.Source.Length == 0)
        {
            var deleted = await RequestAsync("/git/delete", new Dictionary<object, object?>
            {
                [0] = address.Repository, ["ref"] = spec.Target,
            }, Math.Min(limit, 4 * 1024 * 1024), stop).ConfigureAwait(false);
            var deleteError = PushResponseError(deleted.Data, deleted.Metadata);
            if (deleteError is null) Refs.Remove(spec.Target);
            return new(deleteError, 0);
        }

        var sha = (await RngitClient.GitAsync(directory,
            ["rev-parse", "--verify", "--end-of-options", spec.Source], stop).ConfigureAwait(false)).Trim();
        if (sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid local Git object ID.");
        var tips = Refs.Values.Distinct().ToArray();
        var tipSet = tips.ToHashSet(StringComparer.Ordinal);
        var available = tips.Length == 0 ? "" : await RngitClient.GitAsync(directory,
            ["cat-file", "--batch-check=%(objectname) %(objecttype)"], stop, string.Join('\n', tips) + "\n").ConfigureAwait(false);
        var have = available.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split(' '))
            .Where(parts => parts.Length == 2 && parts[1] != "missing" && tipSet.Contains(parts[0]))
            .Select(parts => parts[0]).ToArray();
        var exclusions = have.Select(tip => "^" + tip).ToArray();
        var count = (await RngitClient.GitAsync(directory,
            ["rev-list", "--count", "--objects", "--stdin"], stop, string.Join('\n', new[] { sha }.Concat(exclusions)) + "\n").ConfigureAwait(false)).Trim();
        if (!long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var objectCount))
            throw new InvalidDataException("Invalid local Git object count.");

        Dictionary<object, object?> request;
        long sent = 0;
        if (objectCount == 0)
        {
            request = new()
            {
                [0] = address.Repository,
                ["operations"] = new object?[]
                {
                    new Dictionary<object, object?>
                    {
                        ["action"] = "update_ref", ["ref"] = spec.Target, ["sha"] = sha, ["force"] = spec.Force,
                    },
                },
            };
        }
        else
        {
            // An isolated local bare repository names arbitrary sources (including
            // raw OIDs) without creating even transient refs in the user's repository.
            var staging = Path.Combine(directory, "rngit-push-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var objects = (await RngitClient.GitAsync(directory,
                    ["rev-parse", "--path-format=absolute", "--git-path", "objects"], stop).ConfigureAwait(false)).TrimEnd('\r', '\n');
                if (objects.Contains('\n') || objects.Contains('\r'))
                    throw new InvalidDataException("Git object-directory paths containing newlines are unsupported.");
                await RngitClient.GitAsync(staging, ["init", "--bare", "--object-format=sha1", "."], stop, isolated: true).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(staging, "objects", "info", "alternates"),
                    objects + "\n", new UTF8Encoding(false), stop).ConfigureAwait(false);
                const string sourceRef = "refs/rngit/push";
                await RngitClient.GitAsync(staging, ["update-ref", sourceRef, sha], stop, isolated: true).ConfigureAwait(false);
                var bundle = Path.Combine(staging, "push.bundle");
                await RngitClient.GitAsync(staging,
                    ["bundle", "create", "--quiet", bundle, "--stdin"], stop,
                    string.Join('\n', new[] { sourceRef }.Concat(exclusions)) + "\n", isolated: true).ConfigureAwait(false);
                sent = new FileInfo(bundle).Length;
                if (sent <= 0 || sent > maxBundleBytes)
                    throw new InvalidDataException($"Push bundle exceeds the {maxBundleBytes} byte limit.");
                request = new()
                {
                    [0] = address.Repository, ["local_ref"] = sourceRef, ["remote_ref"] = spec.Target,
                    ["force"] = spec.Force, ["bundle"] = await File.ReadAllBytesAsync(bundle, stop).ConfigureAwait(false),
                };
            }
            finally { Directory.Delete(staging, recursive: true); }
        }
        var response = await RequestAsync("/git/push", request, Math.Min(limit, 4 * 1024 * 1024), stop).ConfigureAwait(false);
        var error = PushResponseError(response.Data, response.Metadata);
        if (error is null) Refs[spec.Target] = sha;
        return new(error, sent);
    }

    internal static string? PushResponseError(byte[] data, object? metadata)
    {
        if (metadata is not null || data.Length == 0 || data[0] is not (0 or 1 or 2 or 3 or 255))
            throw new InvalidDataException("Invalid push/delete response status or metadata.");
        if (data[0] == 0)
        {
            if (data.Length != 1) throw new InvalidDataException("Unexpected payload on successful push/delete response.");
            return null;
        }
        return $"Server refused ref operation (status {data[0]}): {new UTF8Encoding(false, true).GetString(data.AsSpan(1))}";
    }

    internal static string QuoteStatus(string message)
    {
        var result = new StringBuilder("\"");
        foreach (var b in Encoding.UTF8.GetBytes(message))
        {
            if (b is (byte)'\\' or (byte)'"') result.Append('\\').Append((char)b);
            else if (b is < 32 or >= 127) result.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else result.Append((char)b);
        }
        return result.Append('"').ToString();
    }
}
