using System.Globalization;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public sealed partial class RngitPageService
{
    private async Task<string> ProvenanceAsync(RngitHostedRepository repo, CancellationToken token)
    {
        var type = (await TextAsync(repo, ["config", "--get", "repository.rngit.type"], token).ConfigureAwait(false))?.Trim();
        if (type is not ("fork" or "mirror")) return "";
        var source = (await TextAsync(repo, ["config", "--get", "repository.rngit.upstream.source"], token).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(source)) return "";
        var display = PresentSource(source);
        var linked = false;
        if (display.StartsWith("rns://", StringComparison.OrdinalIgnoreCase))
        {
            var parts = display.Split('/');
            if (parts.Length == 5 && parts[2].Length == 32 && parts[2].All(Uri.IsHexDigit))
            {
                try
                {
                    using var identity = KnownDestinations?.Recall(Convert.FromHexString(parts[2]));
                    if (identity is not null)
                    {
                        var destination = Destination.ComputeHashFromNameAndIdentity("nomadnetwork.node", identity.Hash);
                        var label = display.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
                        display = $"`!`[{label}`{Convert.ToHexStringLower(destination)}:/page/repo.mu`g={Encode(parts[3])}|r={Encode(parts[4])}]`!";
                        linked = true;
                    }
                }
                catch (ArgumentException) { }
            }
        }
        var sync = (await TextAsync(repo, ["config", "--get", "repository.rngit.upstream.sync"], token).ConfigureAwait(false))?.Trim() ?? "";
        if (!TryDecimal(sync, out var synced)) synced = 0;
        var elapsed = Math.Max(0, (Clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds - synced);
        var description = type == "fork" ? "Forked from" : "Mirrored from";
        var indent = new string(' ', Math.Max(0, $"Node / {repo.Group} / {repo.Name}".EnumerateRunes().Count() - description.Length));
        if (!linked) display = Escape(display);
        return $"`F666{description}{indent} {display}`f `*`F444synced {SyncAge(elapsed)} ago`f`*\n\n";
    }

    private static string PresentSource(string source)
    {
        var separator = source.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            var scpAt = source.LastIndexOf('@');
            if (scpAt <= 0) return "[upstream source]";
            var hostStart = scpAt + 1;
            var colon = source.IndexOf(':', hostStart);
            if (colon <= hostStart || !source[hostStart..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
                return "[upstream source]";
            source = source[hostStart..];
            var query = source.IndexOfAny(['?', '#']);
            return query < 0 ? source : source[..query];
        }
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return "[upstream source]";
        var authorityStart = separator + 3;
        var authorityEnd = source.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0) authorityEnd = source.Length;
        var at = source.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        if (at >= authorityStart) source = source[..authorityStart] + source[(at + 1)..];
        var suffix = source.IndexOfAny(['?', '#']);
        if (suffix >= 0) source = source[..suffix];
        return source;
    }

    internal static string SyncAge(double seconds)
    {
        seconds = Math.Max(0, seconds);
        foreach (var (duration, suffix) in new[] { (86400d, "d"), (3600d, "h"), (60d, "m"), (1d, "s") })
            if (seconds >= duration) return Math.Floor(seconds / duration).ToString("0", CultureInfo.InvariantCulture) + suffix;
        return "0s";
    }
}
