using System.Globalization;
using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public sealed partial class RngitPageService
{
    private async Task<byte[]> StatsAsync(RngitHostedRepository repo, Identity? caller, CancellationToken token)
    {
        var nav = Navigation(repo.Group, repo.Name, "stats");
        if (!Allowed(caller, repo.Group + "/" + repo.Name, "stats"))
            return await Page(">>Error\n\nThe requested repository was not found.\n", nav, token: token).ConfigureAwait(false);
        var stats = Statistics?.RepositorySummary(repo.Group, repo.Name, 90);
        if (stats is null) return await Page(">>Stats Unavailable\n\nCould not retrieve statistics for this repository.\n", nav, token: token).ConfigureAwait(false);
        var content = new StringBuilder($">>Stats for {Escape(repo.Name)}\n");
        static long Count(object? value) => value switch { null => 0, int i => i, uint u => u, _ => MsgPack.ToInt64(value) };
        long[] Daily(string key) => Items(MapValue(MapValue(stats, key), "daily")).Select(Count).ToArray();
        foreach (var (key, name, color) in new[]
        {
            ("fetches", "Fetches", "10b981"), ("pushes", "Pushes", "B9A810"),
            ("views", "Views", "3b82f6"), ("downloads_combined", "Downloads", "7831E0"),
        })
        {
            var metric = MapValue(stats, key);
            var daily = Daily(key);
            var total = Count(MapValue(metric, "total"));
            var peak = Count(MapValue(metric, "peak"));
            var today = daily.Length == 0 ? 0 : daily[^1];
            var start = key == "fetches" ? "\n" : "";
            var end = key is "fetches" or "pushes" ? " \n`f" : " `f\n";
            content.Append($"{start}`FT{color}{name}`f{new string(' ', 10 - name.Length)}: {total,5}  total `F666  today: {today,3}  peak: {peak,3}{end}");
        }
        content.Append($"`F0aaActivity`f  : {Count(MapValue(stats, "activity_score")),5} points\n\n");
        var (activityColor, activityLabel) = MapText(stats, "activity_level") switch
        {
            "inactive" => ("666", "No activity"), "low" => ("66d", "Low activity"),
            "moderate" => ("aa0", "Moderate activity"), "high" => ("0a0", "High activity"),
            _ => ("666", "Unknown"),
        };
        content.Append($"`F{activityColor}{activityLabel}`f over the last {Count(MapValue(stats, "actual_days"))} days ({MapText(stats, "date_range")})\n\n");
        var labels = Items(MapValue(stats, "timeline_labels")).Select(v => v as string ?? "").ToArray();
        foreach (var (key, name, color, secondary) in new[]
        {
            ("fetches", "Fetches", "10b981", "1c5e71"), ("pushes", "Pushes", "B9A810", "791212"),
            ("views", "Views", "3b82f6", "13428A"), ("downloads_combined", "Downloads", "7831E0", "c5754d"),
        })
        {
            if (Count(MapValue(MapValue(stats, key), "total")) <= 0) continue;
            content.Append($">>{name}\n\n").Append(RenderChart(Daily(key), labels, color, secondary, gradientFactor: key == "downloads_combined" ? 1.7 : 1.3)).Append('\n');
        }
        if (Count(MapValue(stats, "activity_score")) > 0)
            content.Append(">>Combined Activity\n\n").Append(RenderCombinedChart(Daily("views"), Daily("fetches"), Daily("pushes"), Daily("downloads_combined"), labels));
        else content.Append("`*\nNo development activity recorded for this repository in the selected time period.\n\n`*");
        return await Page(content.ToString(), nav, "stats", token).ConfigureAwait(false);
    }

    internal static string RenderChart(long[] data, string[] labels, string color = "666", string? secondaryColor = null, int height = 10, double gradientFactor = 1.3)
    {
        if (data.Length == 0 || data.All(d => d == 0)) return "No data available\n";
        if (data.Length > 3650 || height is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(data));
        var primary = ExpandColor(color);
        var secondary = secondaryColor is null ? Gradient(primary, "000000", 0.42) : ExpandColor(secondaryColor);
        var peak = data.Max();
        var content = new StringBuilder($"`FT{primary}Peak: {peak}`f\n");
        for (var row = height; row > 0; row--)
        {
            var top = row / (double)height * peak;
            var bottom = (row - 1) / (double)height * peak;
            var mid = (top + bottom) / 2;
            var topColor = Gradient(primary, secondary, Math.Min(1, row / (double)height * gradientFactor));
            var midColor = Gradient(primary, secondary, Math.Min(1, (row - 0.5) / height * gradientFactor));
            content.Append('│');
            foreach (var value in data)
            {
                if (value >= top) content.Append($"`FT{topColor}`BT{midColor}▀`f`b");
                else if (value >= mid || row == 1 && value > 0) content.Append($"`FT{midColor}▄`f");
                else content.Append(' ');
            }
            content.Append('\n');
        }
        var border = "└" + new string('─', data.Length) + "┘";
        content.Append(border).Append('\n');
        if (labels.Length > 0)
        {
            var first = labels[0][..Math.Min(12, labels[0].Length)].PadRight(12);
            var last = labels[^1][..Math.Min(12, labels[^1].Length)].PadLeft(12);
            content.Append("`F666").Append(first).Append("`f").Append(' ', Math.Max(0, border.Length - first.Length - last.Length)).Append("`F666").Append(last).Append("`f\n");
        }
        return content.ToString();
    }
    private static string ExpandColor(string color) => color.Length == 3 ? string.Concat(color.Select(c => new string(c, 2))) : color[..6];
    private static string Gradient(string color, string secondary, double factor) => string.Concat(Enumerable.Range(0, 3).Select(i =>
    {
        var c = int.Parse(color.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var s = int.Parse(secondary.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((int)(s + (c - s) * Math.Min(1, factor))).ToString("x2", CultureInfo.InvariantCulture);
    }));
    internal static string RenderCombinedChart(long[] views, long[] fetches, long[] pushes, long[] downloads, string[] labels, int height = 6)
    {
        if (views.Length == 0 || fetches.Length == 0 || pushes.Length == 0 || downloads.Length == 0) return "No data available\n";
        if (views.Length > 3650 || views.Length != fetches.Length || views.Length != pushes.Length || views.Length != downloads.Length || height is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(views));
        string[] names = ["Pushes", "Fetches", "Views", "Downloads"];
        var colors = new[] { "B9A810", "10b981", "3b82f6", "7831E0" }.Select(c => Gradient(c, "000000", 0.87)).ToArray();
        var categories = new[] { pushes, fetches, views, downloads };
        var result = new StringBuilder(string.Join("  ", Enumerable.Range(0, 4).Select(i => $"`FT{colors[i]}`BT{colors[i]}██`f`b {names[i]}")) + "\n\n");
        for (var row = height; row > 0; row--)
        {
            result.Append('│');
            for (var i = 0; i < views.Length; i++)
            {
                var total = categories.Sum(d => (double)d[i]);
                if (total == 0) { result.Append(' '); continue; }
                int Category(double min, double max)
                {
                    double cumulative = 0;
                    for (var category = 0; category < 4; category++)
                    {
                        var start = cumulative / total;
                        cumulative += categories[category][i];
                        var end = cumulative / total;
                        if (min < end && max > start) return category;
                    }
                    return -1;
                }
                var upper = Category((row - 0.5) / height, row / (double)height);
                var lower = Category((row - 1) / (double)height, (row - 0.5) / height);
                if (upper == -1 && lower == -1) result.Append(' ');
                else if (upper == lower) result.Append($"`FT{colors[upper]}`BT{colors[upper]}█`f`b");
                else if (upper >= 0 && lower >= 0) result.Append($"`FT{colors[upper]}`BT{colors[lower]}▀`f`b");
                else if (upper >= 0) result.Append($"`FT{colors[upper]}▀`f");
                else result.Append($"`FT{colors[lower]}▄`f");
            }
            result.Append('\n');
        }
        var border = "└" + new string('─', views.Length) + "┘";
        result.Append(border).Append('\n');
        if (labels.Length > 0)
        {
            var first = labels[0][..Math.Min(12, labels[0].Length)];
            var last = labels[^1][..Math.Min(12, labels[^1].Length)];
            result.Append($"`F666{first}{new string(' ', Math.Max(0, border.Length - first.Length - last.Length))}{last}`f\n");
        }
        return result.ToString();
    }
}
