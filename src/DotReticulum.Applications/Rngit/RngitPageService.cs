using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rngit;

/// <summary>Bounded local Git-backed Nomad Network requests. Authentication is supplied by the link owner.</summary>
public sealed partial class RngitPageService
{
    public static IReadOnlyList<string> Paths { get; } = Array.AsReadOnly(new[]
    {
        "/page/index.mu", "/page/group.mu", "/page/repo.mu", "/page/tree.mu", "/page/blob.mu",
        "/page/commits.mu", "/page/commit.mu", "/page/refs.mu", "/page/stats.mu",
        "/page/releases.mu", "/page/release.mu", "/page/work.mu", "/page/work_doc.mu",
        "/media", "/file/artifact", "/file/download", "/file/workdoc",
    });
    private const int BlobLimit = 256 * 1024;
    private const int OutputLimit = 8 * 1024 * 1024;
    private const int DownloadLimit = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string AnonymousHash = Convert.ToHexStringLower(SHA256.HashData(new byte[64]).AsSpan(0, 16));
    private readonly RngitRepositoryStore store;
    private readonly RngitHostConfiguration configuration;
    private readonly Action<string>? diagnostic;
    private readonly SemaphoreSlim requests = new(8, 8);
    private readonly AsyncLocal<byte[]?> callerHash = new();
    private readonly AsyncLocal<byte[]?> requestLink = new();
    private readonly AsyncLocal<long?> requestStarted = new();
    private readonly object thanksGate = new();
    private readonly Queue<string> thanksOrder = new();
    private readonly HashSet<string> thanksSeen = new(StringComparer.Ordinal);
    private static readonly string[] ImageExtensions = [".webp", ".png", ".jpg", ".jpeg", ".gif", ".tiff", ".tif", ".bmp"];

    public RngitPageService(RngitRepositoryStore store, RngitHostConfiguration config, Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        this.store = store;
        configuration = config;
        this.diagnostic = diagnostic;
        Templates = new(Path.Combine(config.DirectoryPath, "templates"), diagnostic: Report);
    }

    /// <summary>Replace only with an operator-approved instance to enable executable templates.</summary>
    public RngitTemplates Templates { get; set; }

    /// <summary>The host's rngit destination, for repository clone URLs (not the Nomad Network destination).</summary>
    public byte[]? RepositoryDestinationHash { get; set; }

    /// <summary>Shared host counters; responses are counted when prepared, as in pinned pages.py.</summary>
    public RngitHostStatistics? Statistics { get; set; }

    /// <summary>The host's announced identities, used only to link a known upstream's public page.</summary>
    public KnownDestinations? KnownDestinations { get; set; }

    /// <summary>Wall clock for upstream synchronization age; generation time uses a monotonic clock.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public Task<object?> HandleAsync(string path, object? data, Identity? caller, CancellationToken cancellationToken) =>
        HandleAsync(path, data, caller, cancellationToken, null);

    /// <summary>The network owner supplies the established link ID for per-link thanks deduplication.</summary>
    public async Task<object?> HandleAsync(string path, object? data, Identity? caller, CancellationToken cancellationToken, byte[]? linkId)
    {
        if (!Paths.Contains(path, StringComparer.Ordinal)) return null;
        await requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            requestStarted.Value = Stopwatch.GetTimestamp();
            callerHash.Value = caller?.Hash.ToArray();
            requestLink.Value = linkId is { Length: 16 } ? linkId.ToArray() : null;
            cancellationToken.ThrowIfCancellationRequested();
            if (path == "/media") return await MediaAsync(data, caller, cancellationToken).ConfigureAwait(false);
            var group = Field(data, "g");
            var name = Field(data, "r");
            var file = path.StartsWith("/file/", StringComparison.Ordinal);
            if (!file && (path == "/page/group.mu" && group.Length == 0
                || (path is "/page/repo.mu" or "/page/commit.mu" or "/page/stats.mu" or "/page/releases.mu" or "/page/release.mu" or "/page/work.mu" or "/page/work_doc.mu")
                    && (group.Length == 0 || name.Length == 0)
                || path == "/page/release.mu" && Field(data, "t").Length == 0
                || path == "/page/work_doc.mu" && Field(data, "id").Length == 0))
                return await Page(">>Error\n\nInvalid request\n", token: cancellationToken).ConfigureAwait(false);
            if (!file && caller is null && configuration.BlockedIdentities.Contains(AnonymousHash))
            {
                var nav = path switch
                {
                    "/page/index.mu" => ">>\n" + Link("Node", "/page/index.mu") + " /\n",
                    "/page/group.mu" => ">>\n" + Link("Node", "/page/index.mu") + " / " + Escape(group) + "\n",
                    "/page/repo.mu" => RepositoryNavigation(group, name),
                    _ => null,
                };
                if (path == "/page/index.mu") Statistics?.RecordView();
                return await Page("", nav, "no_ident", cancellationToken).ConfigureAwait(false);
            }
            if (path == "/page/index.mu") return await FrontAsync(caller, cancellationToken).ConfigureAwait(false);
            if (path == "/page/group.mu") return await GroupAsync(group, caller, cancellationToken).ConfigureAwait(false);
            if (group.Length == 0 || name.Length == 0)
                return file ? null : await Page(">>Error\n\nInvalid request\n", token: cancellationToken).ConfigureAwait(false);
            if (!file && (path == "/page/release.mu" && Field(data, "t").Length == 0
                || path == "/page/work_doc.mu" && Field(data, "id").Length == 0))
                return await Page(">>Error\n\nInvalid request\n", token: cancellationToken).ConfigureAwait(false);
            var repository = store.Find(group + "/" + name);
            if (repository is null || !Allowed(caller, group + "/" + name, "read"))
            {
                if (file) return null;
                var message = path switch
                {
                    "/page/tree.mu" or "/page/blob.mu" or "/page/commits.mu" =>
                        ">Not Found\n\n\nThe requested repository does not exist or you do not have access to it.\n",
                    "/page/repo.mu" => ">Not Found\n\nThe requested repository was not found.\n",
                    "/page/work_doc.mu" => ">>Error\n\nThe requested repository was not found\n",
                    _ => ">>Error\n\nThe requested repository was not found.\n",
                };
                return await Page(message, path == "/page/repo.mu" ? RepositoryNavigation(group, name)
                    : path is "/page/refs.mu" or "/page/stats.mu" ? Navigation(group, name, path == "/page/refs.mu" ? "refs" : "stats") : null,
                    token: cancellationToken).ConfigureAwait(false);
            }
            using var lease = await store.LockRepositoryAsync(group + "/" + name, cancellationToken).ConfigureAwait(false);
            if (!Allowed(caller, group + "/" + name, "read")) return file ? null : await Page(">>Error\n\nThe requested repository was not found.\n", token: cancellationToken).ConfigureAwait(false);
            EnsureUnlinked(repository.Path);
            var result = path switch
            {
                "/page/repo.mu" => await RepositoryAsync(repository, data, caller, cancellationToken).ConfigureAwait(false),
                "/page/tree.mu" => await TreeAsync(repository, data, cancellationToken).ConfigureAwait(false),
                "/page/blob.mu" => await BlobAsync(repository, data, cancellationToken).ConfigureAwait(false),
                "/page/commits.mu" => await CommitsAsync(repository, data, cancellationToken).ConfigureAwait(false),
                "/page/commit.mu" => await CommitAsync(repository, data, cancellationToken).ConfigureAwait(false),
                "/page/refs.mu" => await RefsAsync(repository, data, cancellationToken).ConfigureAwait(false),
                "/page/stats.mu" => await StatsAsync(repository, caller, cancellationToken).ConfigureAwait(false),
                "/page/releases.mu" or "/page/release.mu" => await ReleasesAsync(repository, path, data, cancellationToken).ConfigureAwait(false),
                "/page/work.mu" or "/page/work_doc.mu" => await WorkAsync(repository, path, data, caller, cancellationToken).ConfigureAwait(false),
                "/file/download" => await DownloadAsync(repository, data, cancellationToken).ConfigureAwait(false),
                "/file/artifact" => await ArtifactAsync(repository, data, cancellationToken).ConfigureAwait(false),
                "/file/workdoc" => await WorkDownloadAsync(repository, data, caller, cancellationToken).ConfigureAwait(false),
                _ => null,
            };
            if (result is FileResponse)
            {
                if (path == "/file/artifact") Statistics?.RecordReleaseDownload(group, name, callerHash.Value ?? []);
                else Statistics?.RecordDownload(group, name, callerHash.Value ?? []);
            }
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Report($"Nomad Network request {path} failed: {e.Message}");
            if (path == "/media") return false;
            return path.StartsWith("/file/", StringComparison.Ordinal) ? null
                : await Page(">>Error\n\nCould not process request.\n", token: cancellationToken).ConfigureAwait(false);
        }
        finally { callerHash.Value = null; requestLink.Value = null; requestStarted.Value = null; requests.Release(); }
    }

    private bool Allowed(Identity? caller, string repository, string permission) => caller is null
        ? store.ResolveAnonymousPermission(repository, permission) : store.ResolvePermission(caller, repository, permission);
    private bool DocumentAllowed(Identity? caller, string repository, long id) => caller is null
        ? store.ResolveAnonymousDocumentPermission(repository, id, "read") : store.ResolveDocumentPermission(caller, repository, id, "read");
    private string Icon(string name) => RngitTemplates.Icon(name, configuration.UnicodeIcons);
    private void Report(string message)
    {
        if (diagnostic is null) return;
        try { diagnostic(message); }
        catch (Exception error) { Console.Error.WriteLine($"Page diagnostic callback failed: {error.Message}; {message}"); }
    }
    private void View(RngitHostedRepository repository) => Statistics?.RecordView(repository.Group, repository.Name, callerHash.Value ?? []);
    private Task<byte[]> Page(string content, string? navigation = null, string? template = null, CancellationToken token = default) =>
        Templates.RenderPageAsync(content, configuration.NodeName, "1.5.7", navigation, template, token,
            generationTime: () => requestStarted.Value is { } started
                ? GenerationTime(Stopwatch.GetElapsedTime(started).TotalSeconds) : "Unknown generation time");

    internal static string GenerationTime(double elapsed)
    {
        if (!double.IsFinite(elapsed) || Math.Abs(elapsed) > TimeSpan.MaxValue.TotalSeconds)
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        var negative = elapsed < 0;
        elapsed = Math.Abs(elapsed);
        var days = (long)(elapsed / 86400);
        elapsed %= 86400;
        var hours = (int)(elapsed / 3600);
        elapsed %= 3600;
        var minutes = (int)(elapsed / 60);
        elapsed %= 60;
        var seconds = RoundHundredths(elapsed);
        var components = new List<string>();
        if (days > 0) components.Add(days.ToString(CultureInfo.InvariantCulture) + "d");
        if (hours > 0) components.Add(hours.ToString(CultureInfo.InvariantCulture) + "h");
        if (minutes > 0) components.Add(minutes.ToString(CultureInfo.InvariantCulture) + "m");
        if (seconds > 0) components.Add(((decimal)seconds / 100m).ToString("0.0#", CultureInfo.InvariantCulture) + "s");
        if (components.Count == 0) return "Generated in 0s";
        var duration = components.Count == 1 ? components[0]
            : string.Join(", ", components.Take(components.Count - 1)) + " and " + components[^1];
        return "Generated in " + (negative ? "-" : "") + duration;
    }
    private static object? Get(object? data, string key) => data is IDictionary map && map.Contains(key) ? map[key] : null;
    private static string Field(object? data, string key, string fallback = "") => Get(data, "var_" + key) is string value && value.Length <= 8192 ? value : fallback;
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
    private static string Encode(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
    private static bool TryDecimal(string value, out long result)
    {
        result = 0;
        value = value.Trim();
        var normalized = new StringBuilder(value.Length);
        var digitBefore = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i == 0 && c is '+' or '-') { normalized.Append(c); continue; }
            if (c == '_')
            {
                if (!digitBefore || i + 1 >= value.Length || CharUnicodeInfo.GetDecimalDigitValue(value, i + 1) < 0) { result = 0; return false; }
                digitBefore = false;
                continue;
            }
            var digit = CharUnicodeInfo.GetDecimalDigitValue(value, i);
            if (digit < 0) { result = 0; return false; }
            normalized.Append((char)('0' + digit));
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            digitBefore = true;
        }
        return digitBefore && long.TryParse(normalized.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }
    private static string Escape(string value) => value.Replace("`", "\\`", StringComparison.Ordinal);
    private static string Link(string label, string route, bool bold = true, params (string Key, object Value)[] fields)
    {
        label = label.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
        var parameters = fields.Length == 0 ? "" : "`" + string.Join('|', fields.Select(f =>
            f.Key + "=" + Encode(Convert.ToString(f.Value, CultureInfo.InvariantCulture) ?? "")));
        var emphasis = bold ? "`!" : "";
        return $"{emphasis}`[{label}`:{route}{parameters}]{emphasis}";
    }
    private static string Navigation(string group, string name, string suffix) =>
        ">>\n" + Link("Node", "/page/index.mu") + " / " + Link(group, "/page/group.mu", true, ("g", group))
        + " / " + Link(name, "/page/repo.mu", true, ("g", group), ("r", name)) + " / " + suffix + "\n";
    private string RepositoryNavigation(string group, string name)
    {
        var url = RepositoryDestinationHash is { Length: 16 } hash ? $" `F666rns://{Convert.ToHexStringLower(hash)}/{group}/{name}`f" : "";
        return ">>\n" + Link("Node", "/page/index.mu") + " / " + Link(group, "/page/group.mu", true, ("g", group)) + " / " + Escape(name) + url + "\n";
    }
    internal static string Size(long bytes)
    {
        var value = (double)bytes;
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];
        var index = 0;
        while (Math.Abs(value) >= 1000 && index < suffixes.Length - 1) { value /= 1000; index++; }
        if (index == 0) return value.ToString("0", CultureInfo.InvariantCulture) + " B";
        // Python's printf formats the exact binary value; .NET's shortest decimal and
        // Math.Round can disagree at values such as 2.675 and 262.145.
        var rounded = RoundHundredths(value);
        return (bytes < 0 ? "-" : "") + (rounded / 100).ToString(CultureInfo.InvariantCulture) + "." + ((int)(rounded % 100)).ToString("D2", CultureInfo.InvariantCulture) + " " + suffixes[index];
    }
    private static BigInteger RoundHundredths(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var encodedExponent = (int)((bits >> 52) & 2047);
        var exponent = (encodedExponent == 0 ? -1022 : encodedExponent - 1023) - 52;
        var numerator = new BigInteger((bits & 0x000fffffffffffffL) | (encodedExponent == 0 ? 0 : 1L << 52)) * 100;
        var denominator = BigInteger.One;
        if (exponent >= 0) numerator <<= exponent;
        else denominator <<= -exponent;
        var rounded = BigInteger.DivRem(numerator, denominator, out var remainder);
        var comparison = (remainder * 2).CompareTo(denominator);
        if (comparison > 0 || comparison == 0 && !rounded.IsEven) rounded++;
        return rounded;
    }
    private static void EnsureUnlinked(string path)
    {
        for (var item = new FileInfo(path); item is not null; item = item.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((item.Exists || Directory.Exists(item.FullName)) && (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked filesystem paths are not authorized.");
    }
    private static bool SafeGitPath(string path) => path.Length <= 4096 && !path.StartsWith('/') && !path.Contains('\\')
        && !path.Contains('\0') && !path.Any(c => c < 32) && !path.Split('/').Any(p => p is "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase));

    private async Task<byte[]> FrontAsync(Identity? caller, CancellationToken token)
    {
        var visible = store.Repositories.Where(r => Allowed(caller, r.Group + "/" + r.Name, "read"))
            .GroupBy(r => r.Group).OrderBy(r => r.Key, StringComparer.Ordinal).ToArray();
        var content = visible.Length == 0 ? ">>\nNo groups available\n" : string.Concat(visible.Select(g =>
            Link("  • " + g.Key, "/page/group.mu", true, ("g", g.Key)) + $" ({g.Count()} {(g.Count() == 1 ? "repository" : "repositories")})\n"));
        Statistics?.RecordView(identityHash: callerHash.Value ?? []);
        return await Page(content, ">>\n" + Link("Node", "/page/index.mu") + " /\n", "front", token).ConfigureAwait(false);
    }

    private async Task<byte[]> GroupAsync(string group, Identity? caller, CancellationToken token)
    {
        if (group.Length == 0) return await Page(">>Error\n\nInvalid request\n", token: token).ConfigureAwait(false);
        var nav = ">>\n" + Link("Node", "/page/index.mu") + " / " + Escape(group) + "\n";
        var repositories = store.Repositories.Where(r => r.Group == group && Allowed(caller, r.Group + "/" + r.Name, "read")).OrderBy(r => r.Name, StringComparer.Ordinal).ToArray();
        if (repositories.Length == 0) return await Page(">>Group Not Found\n\nThe requested group was not found\n", nav, token: token).ConfigureAwait(false);
        var content = new StringBuilder("> Repositories\n\n");
        foreach (var repo in repositories)
        {
            using var lease = await store.LockRepositoryAsync(group + "/" + repo.Name, token).ConfigureAwait(false);
            var description = await DescriptionAsync(repo, token).ConfigureAwait(false);
            content.Append(Link("  • " + repo.Name, "/page/repo.mu", true, ("g", group), ("r", repo.Name)))
                .Append(description.Length == 0 ? "\n" : " - " + Escape(description) + "\n");
        }
        Statistics?.RecordView(group, identityHash: callerHash.Value ?? []);
        return await Page(content.ToString(), nav, "group", token).ConfigureAwait(false);
    }
}
