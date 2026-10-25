using System.Globalization;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

/// <summary>
/// Thread-safe local-day counters in the upstream pages/groups MessagePack schema.
/// Records are synchronous, snapshots detached, and Flush atomically replaces the
/// last file. A corrupt/unreadable file disables writes rather than resetting it.
/// One instance owns a statistics file; cross-process merging is not supported.
/// </summary>
public sealed class RngitHostStatistics
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxEntries = 100_000;
    private static readonly string[] RepositoryCounters = ["view", "fetch", "push", "download", "release_download"];
    private readonly object gate = new();
    private readonly RngitHostConfiguration configuration;
    private readonly Action<string>? diagnostic;
    private readonly TimeProvider clock;
    private Dictionary<object, object?> statistics = Initial();
    private int entries;

    public RngitHostStatistics(RngitHostConfiguration configuration, Action<string>? diagnostic = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        this.configuration = configuration;
        this.diagnostic = diagnostic;
        this.clock = clock ?? TimeProvider.System;
        try
        {
            if (!File.Exists(configuration.StatisticsPath))
            {
                var staging = StagingPath();
                try
                {
                    Write(staging, MsgPack.Pack(statistics));
                    AtomicFilePublication.TryPublishNew(staging, configuration.StatisticsPath);
                }
                finally { File.Delete(staging); }
            }
            using var stream = File.OpenRead(configuration.StatisticsPath);
            if (stream.Length > MaxBytes) throw new InvalidDataException("Statistics file exceeds 16777216 bytes.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Statistics file changed while reading.");
            var loaded = Map(MsgPack.Unpack(bytes));
            entries = Validate(loaded);
            statistics = loaded;
            IsAvailable = true;
        }
        catch (Exception exception) when (PersistenceFailure(exception))
        {
            diagnostic?.Invoke($"Could not read rngit statistics '{configuration.StatisticsPath}'; writes disabled: {exception.Message}");
        }
    }

    public bool IsAvailable { get; }

    /// <summary>Null group/repository counts the front page; null repository counts the group.</summary>
    public bool RecordView(string? group = null, string? repository = null, ReadOnlySpan<byte> identityHash = default)
    {
        if (!CanRecord(identityHash, push: false)) return false;
        if (group is null && repository is null) return Record(null, null, "front");
        if (group is null) throw new ArgumentException("A repository view requires a group.", nameof(group));
        return Record(group, repository, "view");
    }

    public bool RecordFetch(string group, string repository, ReadOnlySpan<byte> identityHash = default) =>
        RecordRepository(group, repository, "fetch", identityHash);

    /// <summary>
    /// Matches pinned push_succeeded: neither stats_ignore_identities nor
    /// stats_push_ignore_identities filters pushes. The latter is parsed upstream
    /// but unused; applying it here would silently diverge from the reference.
    /// </summary>
    public bool RecordPush(string group, string repository, ReadOnlySpan<byte> identityHash = default) =>
        RecordRepository(group, repository, "push", identityHash);

    public bool RecordDownload(string group, string repository, ReadOnlySpan<byte> identityHash = default) =>
        RecordRepository(group, repository, "download", identityHash);

    public bool RecordReleaseDownload(string group, string repository, ReadOnlySpan<byte> identityHash = default) =>
        RecordRepository(group, repository, "release_download", identityHash);

    /// <summary>Detached MessagePack-compatible tree; modifying it cannot mutate live counters.</summary>
    public IReadOnlyDictionary<object, object?> Snapshot()
    {
        lock (gate) return Map(MsgPack.Unpack(MsgPack.Pack(statistics)));
    }

    public byte[] SnapshotMessagePack()
    {
        lock (gate) return MsgPack.Pack(statistics);
    }

    /// <summary>
    /// Permission-neutral upstream repository_stats aggregation. The caller must
    /// authorize statistics access. Recording disabled does not hide old history;
    /// unavailable/corrupt storage returns null rather than an empty report.
    /// Lookbacks are bounded to 1..3650 days. Labels use invariant English month names.
    /// </summary>
    public IReadOnlyDictionary<object, object?>? RepositorySummary(string group, string repository, int lookbackDays = 14)
    {
        ValidateName(group);
        ValidateName(repository);
        if (lookbackDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(lookbackDays));
        lock (gate)
        {
            if (!IsAvailable) return null;
            var now = clock.GetUtcNow();
            var days = new string[lookbackDays];
            var labels = new string[lookbackDays];
            for (var i = 0; i < lookbackDays; i++)
            {
                // Reference subtracts 86400 seconds, not local calendar days.
                var date = TimeZoneInfo.ConvertTime(now.AddSeconds(-(lookbackDays - 1L - i) * 86400), clock.LocalTimeZone);
                days[i] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                labels[i] = date.ToString("MMM dd", CultureInfo.InvariantCulture);
            }
            var result = new Dictionary<object, object?>
            {
                ["group"] = group,
                ["repository"] = repository,
                ["lookback_days"] = lookbackDays,
                ["date_range"] = $"{labels[0]} - {labels[^1]}",
                ["days"] = days,
                ["day_labels"] = labels,
                ["timeline_labels"] = new[] { $"{lookbackDays} days ago", "Today" },
            };
            var groupData = OptionalMap(Map(statistics["groups"]), group);
            var repoData = OptionalMap(OptionalMap(groupData, "repositories"), repository);
            var views = OptionalMap(repoData, "view");
            var fetches = OptionalMap(repoData, "fetch");
            var pushes = OptionalMap(repoData, "push");
            var downloads = OptionalMap(repoData, "download");
            var releases = OptionalMap(repoData, "release_download");
            result["views"] = Metric(views);
            result["fetches"] = Metric(fetches);
            result["pushes"] = Metric(pushes);
            result["downloads"] = Metric(downloads);
            result["release_downloads"] = Metric(releases);
            result["downloads_combined"] = Metric(downloads, releases);
            var viewTotal = checked(Total("views") + Total("downloads") + Total("release_downloads"));
            var totalScore = viewTotal * 0.2 + Total("fetches") * 2.0 + Total("pushes") * 5.0;
            result["activity_score"] = checked((long)totalScore);
            var earliest = new[] { views, fetches, pushes }.SelectMany(map => map)
                .Where(pair => MsgPack.ToInt64(pair.Value) > 0)
                .Select(pair => (string)pair.Key).Order(StringComparer.Ordinal).FirstOrDefault();
            var actualDays = lookbackDays;
            if (earliest is not null)
            {
                try
                {
                    var midnight = DateOnly.ParseExact(earliest, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue);
                    var timestamp = new DateTimeOffset(midnight, clock.LocalTimeZone.GetUtcOffset(midnight));
                    actualDays = (int)Math.Clamp(Math.Floor((now - timestamp).TotalSeconds / 86400) + 1, 1, lookbackDays);
                }
                catch (ArgumentException)
                {
                    // Upstream retains the lookback when mktime cannot represent a date.
                }
            }
            result["actual_days"] = actualDays;
            var dailyScore = totalScore / actualDays;
            result["activity_level"] = dailyScore == 0 ? "inactive" : dailyScore < 3 ? "low" : dailyScore < 10 ? "moderate" : "high";
            return result;

            long Total(string metric) => MsgPack.ToInt64(Map(result[metric])["total"]);
            Dictionary<object, object?> Metric(Dictionary<object, object?> primary, Dictionary<object, object?>? additional = null)
            {
                var daily = new object?[days.Length];
                long total = 0;
                long peak = 0;
                string? peakDay = null;
                for (var i = 0; i < days.Length; i++)
                {
                    var count = primary.TryGetValue(days[i], out var value) ? MsgPack.ToInt64(value) : 0;
                    if (additional is not null && additional.TryGetValue(days[i], out var extra))
                        count = checked(count + MsgPack.ToInt64(extra));
                    daily[i] = count;
                    total = checked(total + count);
                    if (count > peak)
                    {
                        peak = count;
                        peakDay = days[i];
                    }
                }
                return new Dictionary<object, object?>
                {
                    ["daily"] = daily, ["total"] = total, ["peak"] = peak, ["peak_day"] = peakDay,
                };
            }
        }
    }

    private static Dictionary<object, object?> OptionalMap(Dictionary<object, object?> parent, string key) =>
        parent.TryGetValue(key, out var value) ? Map(value) : [];

    public bool Flush()
    {
        lock (gate)
        {
            if (!IsAvailable) return false;
            var staging = StagingPath();
            try
            {
                var bytes = MsgPack.Pack(statistics);
                if (bytes.Length > MaxBytes) throw new InvalidDataException("Statistics exceed 16777216 bytes.");
                Write(staging, bytes);
                File.Move(staging, configuration.StatisticsPath, overwrite: true);
                return true;
            }
            catch (Exception exception) when (PersistenceFailure(exception))
            {
                diagnostic?.Invoke($"Could not write rngit statistics '{configuration.StatisticsPath}': {exception.Message}");
                return false;
            }
            finally
            {
                try { File.Delete(staging); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    diagnostic?.Invoke($"Could not clean rngit statistics staging file '{staging}': {exception.Message}");
                }
            }
        }
    }

    private bool RecordRepository(string group, string repository, string counter, ReadOnlySpan<byte> identityHash)
    {
        if (!CanRecord(identityHash, push: counter == "push")) return false;
        // The pinned succeeded wrappers ignore empty repository/group names.
        if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(repository)) return false;
        return Record(group, repository, counter);
    }

    private bool CanRecord(ReadOnlySpan<byte> identityHash, bool push)
    {
        if (!IsAvailable || !configuration.RecordStats) return false;
        if (identityHash.Length is not 0 and not Identity.HashSize)
            throw new ArgumentException("Peer identity hash must be empty or exactly 16 bytes.", nameof(identityHash));
        return push || identityHash.IsEmpty || !configuration.IgnoredIdentities.Contains(Convert.ToHexStringLower(identityHash));
    }

    private bool Record(string? group, string? repository, string counter)
    {
        if (group is not null) ValidateName(group);
        if (repository is not null) ValidateName(repository);
        lock (gate)
        {
            // Reserve the worst-case new path before mutation; never evict persisted history.
            if (entries > MaxEntries - 12)
            {
                diagnostic?.Invoke("rngit statistics entry bound reached; record rejected.");
                return false;
            }
            Dictionary<object, object?> counters;
            if (group is null) counters = Map(Map(statistics["pages"])["front"]);
            else
            {
                var groups = Map(statistics["groups"]);
                if (!groups.TryGetValue(group, out var groupValue))
                {
                    // Unlike upstream's shared mutable STATS_INIT_* objects, each
                    // group/repository owns its counters; unrelated histories stay isolated.
                    groupValue = new Dictionary<object, object?> { ["view"] = new Dictionary<object, object?>(), ["repositories"] = new Dictionary<object, object?>() };
                    groups.Add(group, groupValue);
                    entries += 3;
                }
                var groupStats = Map(groupValue);
                if (repository is null) counters = Counter(groupStats, "view");
                else
                {
                    var repositories = Map(groupStats["repositories"]);
                    if (!repositories.TryGetValue(repository, out var repositoryValue))
                    {
                        repositoryValue = RepositoryCounters.ToDictionary(key => (object)key, _ => (object?)new Dictionary<object, object?>());
                        repositories.Add(repository, repositoryValue);
                        entries += 6;
                    }
                    counters = Counter(Map(repositoryValue), counter);
                }
            }
            var day = clock.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var prior = counters.GetValueOrDefault(day);
            var value = prior is null ? 0 : MsgPack.ToInt64(prior);
            if (value == long.MaxValue)
            {
                diagnostic?.Invoke("rngit statistics counter overflow; record rejected.");
                return false;
            }
            if (prior is null) entries++;
            counters[day] = value + 1;
            return true;
        }
    }

    private Dictionary<object, object?> Counter(Dictionary<object, object?> parent, string name)
    {
        if (!parent.TryGetValue(name, out var value))
        {
            value = new Dictionary<object, object?>();
            parent.Add(name, value);
            entries++;
        }
        return Map(value);
    }

    private static Dictionary<object, object?> Initial() => new()
    {
        ["pages"] = new Dictionary<object, object?> { ["front"] = new Dictionary<object, object?>() },
        ["groups"] = new Dictionary<object, object?>(),
    };

    private static Dictionary<object, object?> Map(object? value) =>
        value as Dictionary<object, object?> ?? throw new FormatException("Expected a statistics map.");

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 256)
            throw new ArgumentException("Statistics names must contain 1 through 256 characters.", nameof(name));
    }

    private static int Validate(Dictionary<object, object?> root)
    {
        var count = 0;
        CheckKeys(root, ["pages", "groups"], required: true);
        var pages = Map(root["pages"]);
        CheckKeys(pages, ["front"], required: true);
        Days(Map(pages["front"]));
        var groups = Map(root["groups"]);
        Add(groups.Count);
        foreach (var (group, groupValue) in groups)
        {
            if (group is not string name) throw new FormatException("Expected a string group name.");
            ValidateName(name);
            var groupStats = Map(groupValue);
            CheckKeys(groupStats, ["view", "repositories"], required: true);
            Days(Map(groupStats["view"]));
            var repositories = Map(groupStats["repositories"]);
            Add(repositories.Count);
            foreach (var (repository, repositoryValue) in repositories)
            {
                if (repository is not string repositoryName) throw new FormatException("Expected a string repository name.");
                ValidateName(repositoryName);
                var repoStats = Map(repositoryValue);
                CheckKeys(repoStats, RepositoryCounters, required: false);
                foreach (var value in repoStats.Values) Days(Map(value));
            }
        }
        return count;

        void Add(int value)
        {
            count = checked(count + value);
            if (count > MaxEntries) throw new FormatException("Statistics exceed 100000 map entries.");
        }
        void CheckKeys(Dictionary<object, object?> map, string[] allowed, bool required)
        {
            Add(map.Count);
            if (required && map.Count != allowed.Length) throw new FormatException("Missing statistics schema keys.");
            foreach (var key in map.Keys)
                if (key is not string text || !allowed.Contains(text, StringComparer.Ordinal))
                    throw new FormatException("Unknown statistics schema key.");
        }
        void Days(Dictionary<object, object?> map)
        {
            Add(map.Count);
            foreach (var (key, value) in map)
                if (key is not string date || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    || value is not long number || number < 0)
                    throw new FormatException("Invalid statistics day or counter.");
        }
    }

    private static bool PersistenceFailure(Exception exception) => exception is
        IOException or InvalidDataException or UnauthorizedAccessException or FormatException or ArgumentException or OverflowException or InvalidOperationException;

    private string StagingPath() => configuration.StatisticsPath + "." + Guid.NewGuid().ToString("N") + ".new";

    private static void Write(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
