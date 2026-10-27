using System.Text.Json;
using DotReticulum.Applications.Rngit;
using DotReticulum.Core;

namespace DotReticulum.Applications.Tests;

// Source: pinned RNS a29bfb51c468945c96a1ee22cae0114e993e2391 server.py:2076-2097,4641-4908.
// SHA-256: 6257193aed6785359f4400da8d5413e7717ac5542e59666ac0f7f79e20975693.
// Fixture executes actual recording methods. Generator discloses the Windows
// os.rename workaround and avoids the upstream shared-default dictionary bug.
public sealed class RngitHostStatisticsTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef";
    private readonly string directory = Path.Combine(Directory.GetCurrentDirectory(), "rngit-host-stats-" + Guid.NewGuid().ToString("N"));
    private readonly RngitHostConfiguration configuration;
    private readonly FixedClock clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    public RngitHostStatisticsTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config"), $"""
            [rngit]
            record_stats = yes
            stats_ignore_identities = {Hash}
            stats_push_ignore_identities = {Hash}
            """);
        configuration = RngitHostConfiguration.Load(directory);
    }

    public void Dispose() => TestDirectory.DeleteWithRetry(directory);
    private RngitHostStatistics Create(Action<string>? diagnostic = null) => new(configuration, diagnostic, clock);
    private static Dictionary<object, object?> Map(object? value) => Assert.IsType<Dictionary<object, object?>>(value);
    private static long Count(RngitHostStatistics statistics, params string[] path)
    {
        object? value = statistics.Snapshot();
        foreach (var key in path) value = Map(value)[key];
        return Assert.IsType<long>(value);
    }
    private static readonly string[] PushPath = ["groups", "group", "repositories", "repo", "push", "2026-10-19"];

    [Fact]
    public void DifferentialCountersAndMessagePackMatchActualPinnedMethods()
    {
        var stats = Create();
        Assert.True(stats.IsAvailable);
        Assert.True(stats.RecordView());
        Assert.True(stats.RecordView("group"));
        Assert.True(stats.RecordView("group", "repo"));
        Assert.True(stats.RecordFetch("group", "repo"));
        Assert.True(stats.RecordPush("group", "repo"));
        Assert.True(stats.RecordDownload("group", "repo"));
        Assert.True(stats.RecordReleaseDownload("group", "repo"));
        var ignored = Convert.FromHexString(Hash);
        Assert.False(stats.RecordView("group", "repo", ignored));
        Assert.False(stats.RecordFetch("group", "repo", ignored));
        Assert.True(stats.RecordPush("group", "repo", ignored));
        Assert.False(stats.RecordDownload("group", "repo", ignored));
        Assert.False(stats.RecordReleaseDownload("group", "repo", ignored));
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rngit-host.json")));
        var expected = Convert.FromHexString(fixture.RootElement.GetProperty("statistics_msgpack").GetString()!);
        Assert.Equal(expected, stats.SnapshotMessagePack());
        Assert.True(stats.Flush());
        Assert.Equal(expected, File.ReadAllBytes(configuration.StatisticsPath));
        Assert.Equal(expected, Create().SnapshotMessagePack());
    }

    [Fact]
    public void DifferentialSummariesMatchActualPinnedAggregation()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rngit-host.json")));
        File.WriteAllBytes(configuration.StatisticsPath,
            Convert.FromHexString(fixture.RootElement.GetProperty("statistics_msgpack").GetString()!));
        var stats = Create();
        Assert.Equal(Convert.FromHexString(fixture.RootElement.GetProperty("summary_msgpack").GetString()!),
            MsgPack.Pack(stats.RepositorySummary("group", "repo", 3)));
        Assert.Equal(Convert.FromHexString(fixture.RootElement.GetProperty("empty_summary_msgpack").GetString()!),
            MsgPack.Pack(stats.RepositorySummary("unknown", "unknown", 3)));
    }

    [Fact]
    public void SummaryDistinguishesUnavailableFromDisabledAndBoundsLookbacks()
    {
        var stats = Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => stats.RepositorySummary("group", "repo", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => stats.RepositorySummary("group", "repo", 3651));
        Assert.Equal(14, stats.RepositorySummary("group", "repo")!["lookback_days"]);
        File.WriteAllText(configuration.ConfigPath, "");
        var disabled = new RngitHostStatistics(RngitHostConfiguration.Load(directory), clock: clock);
        Assert.NotNull(disabled.RepositorySummary("group", "repo"));
        File.WriteAllBytes(configuration.StatisticsPath, [0xc1]);
        Assert.Null(Create().RepositorySummary("group", "repo"));
    }

    [Fact]
    public void SummaryPeaksUseFirstTieAndActivitySpanIgnoresDownloadOnlyHistory()
    {
        var stats = Create();
        stats.RecordDownload("group", "repo");
        var downloadOnly = stats.RepositorySummary("group", "repo", 3)!;
        Assert.Equal(3, downloadOnly["actual_days"]);
        Assert.Equal("low", downloadOnly["activity_level"]);
        stats.RecordPush("group", "repo");
        clock.Now = clock.Now.AddDays(1);
        stats.RecordPush("group", "repo");
        var summary = stats.RepositorySummary("group", "repo", 3)!;
        Assert.Equal(2, summary["actual_days"]);
        Assert.Equal("2026-10-19", Map(summary["pushes"])["peak_day"]);
        Assert.Equal(10L, summary["activity_score"]);
        Assert.Equal("moderate", summary["activity_level"]);
        // Summary is detached from live counters.
        Map(summary["pushes"])["total"] = 500L;
        Assert.Equal(2L, Map(stats.RepositorySummary("group", "repo", 3)!["pushes"])["total"]);
    }

    [Fact]
    public void DisabledRecordingDoesNotMutateButLoadsExistingStatistics()
    {
        var first = Create();
        Assert.True(first.RecordPush("group", "repo"));
        Assert.True(first.Flush());
        File.WriteAllText(configuration.ConfigPath, "");
        var stats = new RngitHostStatistics(RngitHostConfiguration.Load(directory), clock: clock);
        Assert.False(stats.RecordView());
        Assert.False(stats.RecordFetch("group", "repo"));
        Assert.False(stats.RecordPush("group", "repo"));
        Assert.False(stats.RecordDownload("group", "repo"));
        Assert.False(stats.RecordReleaseDownload("group", "repo"));
        Assert.Equal(1, Count(stats, PushPath));
    }

    [Fact]
    public void GroupsAndRepositoriesDoNotShareUpstreamMutableDefaultDictionaries()
    {
        var stats = Create();
        stats.RecordPush("group", "repo");
        stats.RecordPush("group", "other");
        stats.RecordPush("other-group", "repo");
        stats.RecordView("group");
        Assert.Equal(1, Count(stats, PushPath));
        Assert.Equal(1, Count(stats, "groups", "group", "repositories", "other", "push", "2026-10-19"));
        Assert.Equal(1, Count(stats, "groups", "other-group", "repositories", "repo", "push", "2026-10-19"));
        Assert.Empty(Map(Map(Map(stats.Snapshot()["groups"])["other-group"])["view"]));
    }

    [Fact]
    public void SnapshotsAreDetached()
    {
        var stats = Create();
        stats.RecordView();
        var snapshot = stats.Snapshot();
        Map(Map(snapshot["pages"])["front"])["2026-10-19"] = 999L;
        var bytes = stats.SnapshotMessagePack();
        bytes[0] = 0;
        Assert.Equal(1, Count(stats, "pages", "front", "2026-10-19"));
        Assert.True(stats.Flush());
    }

    [Fact]
    public void UsesInjectedLocalDateIncludingDayRollover()
    {
        var stats = Create();
        stats.RecordPush("group", "repo");
        clock.Now = clock.Now.AddDays(1);
        stats.RecordPush("group", "repo");
        Assert.Equal(1, Count(stats, PushPath));
        Assert.Equal(1, Count(stats, "groups", "group", "repositories", "repo", "push", "2026-10-20"));
    }

    [Fact]
    public void DayBucketsUseLocalTimezoneRatherThanUtc()
    {
        clock.Now = new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);
        clock.Zone = TimeZoneInfo.CreateCustomTimeZone("fixture-plus-two", TimeSpan.FromHours(2), "fixture-plus-two", "fixture-plus-two");
        var stats = Create();
        Assert.True(stats.RecordPush("group", "repo"));
        Assert.Equal(1, Count(stats, "groups", "group", "repositories", "repo", "push", "2026-10-20"));
    }

    [Fact]
    public async Task ConcurrentRecordsSnapshotsAndFlushesSurviveRestart()
    {
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var stats = Create(errors.Enqueue);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                Assert.True(stats.RecordPush("group", "repo"));
                if (i % 25 == 0)
                {
                    Assert.NotEmpty(stats.Snapshot());
                    Assert.True(stats.Flush(), string.Join(Environment.NewLine, errors));
                }
            }
        })));
        Assert.Equal(800, Count(stats, PushPath));
        Assert.True(stats.Flush());
        Assert.Equal(800, Count(Create(), PushPath));
        Assert.Empty(Directory.GetFiles(directory, "*.new"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("c1")]
    [InlineData("80")]
    [InlineData("82a5706167657380a667726f75707380")]
    public void CorruptOrWrongSchemaIsDiagnosedAndNeverReset(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        File.WriteAllBytes(configuration.StatisticsPath, bytes);
        var diagnostics = new List<string>();
        var stats = Create(diagnostics.Add);
        Assert.False(stats.IsAvailable);
        Assert.False(stats.RecordPush("group", "repo"));
        Assert.False(stats.Flush());
        Assert.Equal(bytes, File.ReadAllBytes(configuration.StatisticsPath));
        Assert.Single(diagnostics);
        Assert.Contains("writes disabled", diagnostics[0]);
    }

    [Fact]
    public void UnreadableStatsPathIsNotReplaced()
    {
        Directory.CreateDirectory(configuration.StatisticsPath);
        var diagnostics = new List<string>();
        var stats = Create(diagnostics.Add);
        Assert.False(stats.IsAvailable);
        Assert.False(stats.Flush());
        Assert.True(Directory.Exists(configuration.StatisticsPath));
        Assert.NotEmpty(diagnostics);
        Assert.Empty(Directory.GetFiles(directory, "*.new"));
    }

    [Fact]
    public void UnknownDaysWrongTypesAndUnknownCountersAreRejectedWithoutReset()
    {
        var root = new Dictionary<object, object?>
        {
            ["pages"] = new Dictionary<object, object?> { ["front"] = new Dictionary<object, object?>() },
            ["groups"] = new Dictionary<object, object?>
            {
                ["group"] = new Dictionary<object, object?>
                {
                    ["view"] = new Dictionary<object, object?>(),
                    ["repositories"] = new Dictionary<object, object?>
                    {
                        ["repo"] = new Dictionary<object, object?> { ["fetch"] = new Dictionary<object, object?>() },
                    },
                },
            },
        };
        var fetch = Map(Map(Map(Map(root["groups"])["group"])["repositories"])["repo"]);
        var days = Map(fetch["fetch"]);
        foreach (var (day, count) in new (string, object?)[] { ("not-a-day", 1L), ("2026-10-19", "1"), ("2026-10-19", null) })
        {
            days.Clear();
            days[day] = count;
            var bytes = MsgPack.Pack(root);
            File.WriteAllBytes(configuration.StatisticsPath, bytes);
            var stats = Create();
            Assert.False(stats.IsAvailable);
            Assert.False(stats.Flush());
            Assert.Equal(bytes, File.ReadAllBytes(configuration.StatisticsPath));
        }
        days.Clear();
        fetch.Add("unknown", new Dictionary<object, object?>());
        File.WriteAllBytes(configuration.StatisticsPath, MsgPack.Pack(root));
        Assert.False(Create().IsAvailable);
    }

    [Fact]
    public void FailedReplacementPreservesOldFileAndCountersForRetry()
    {
        var diagnostics = new List<string>();
        var stats = Create(diagnostics.Add);
        stats.RecordPush("group", "repo");
        Assert.True(stats.Flush());
        var old = File.ReadAllBytes(configuration.StatisticsPath);
        stats.RecordPush("group", "repo");
        File.Move(configuration.StatisticsPath, configuration.StatisticsPath + ".saved");
        Directory.CreateDirectory(configuration.StatisticsPath);
        // The already loaded instance must not reset in-memory state on failure.
        Assert.False(stats.Flush());
        Assert.Equal(old, File.ReadAllBytes(configuration.StatisticsPath + ".saved"));
        Directory.Delete(configuration.StatisticsPath);
        File.Move(configuration.StatisticsPath + ".saved", configuration.StatisticsPath);
        Assert.True(stats.Flush());
        Assert.Equal(2, Count(Create(), PushPath));
        Assert.Empty(Directory.GetFiles(directory, "*.new"));
        Assert.Single(diagnostics);
        Assert.Contains("Could not write", diagnostics[0]);
    }

    [Fact]
    public void FileSizeAndCounterSchemaAreBounded()
    {
        using (var stream = File.Create(configuration.StatisticsPath)) stream.SetLength(16 * 1024 * 1024 + 1);
        Assert.False(Create().IsAvailable);
        var root = new Dictionary<object, object?>
        {
            ["pages"] = new Dictionary<object, object?> { ["front"] = new Dictionary<object, object?> { ["2026-10-19"] = -1L } },
            ["groups"] = new Dictionary<object, object?>(),
        };
        File.WriteAllBytes(configuration.StatisticsPath, MsgPack.Pack(root));
        Assert.False(Create().IsAvailable);
        Map(Map(root["pages"])["front"])["2026-10-19"] = long.MaxValue;
        File.WriteAllBytes(configuration.StatisticsPath, MsgPack.Pack(root));
        var stats = Create();
        Assert.True(stats.IsAvailable);
        Assert.False(stats.RecordView());
        Assert.Equal(long.MaxValue, Count(stats, "pages", "front", "2026-10-19"));
    }

    [Fact]
    public void EntryLimitRejectsOversizedHistoryWithoutReset()
    {
        var days = new Dictionary<object, object?>();
        var start = new DateOnly(1700, 1, 1);
        for (var i = 0; i < 100_000; i++)
            days.Add(start.AddDays(i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), 1L);
        var root = new Dictionary<object, object?>
        {
            ["pages"] = new Dictionary<object, object?> { ["front"] = days },
            ["groups"] = new Dictionary<object, object?>(),
        };
        var bytes = MsgPack.Pack(root);
        File.WriteAllBytes(configuration.StatisticsPath, bytes);
        var diagnostics = new List<string>();
        var stats = Create(diagnostics.Add);
        Assert.False(stats.IsAvailable);
        Assert.False(stats.Flush());
        Assert.Equal(bytes, File.ReadAllBytes(configuration.StatisticsPath));
        Assert.Contains("100000", Assert.Single(diagnostics));
    }

    [Fact]
    public void InvalidNamesHashesAndMissingRepositoryAreHandledExplicitly()
    {
        var stats = Create();
        Assert.Throws<ArgumentException>(() => stats.RecordView(null, "repo"));
        Assert.Throws<ArgumentException>(() => stats.RecordView(new string('x', 257)));
        Assert.Throws<ArgumentException>(() => stats.RecordPush("group", "repo", new byte[15]));
        Assert.False(stats.RecordFetch("", "repo"));
        Assert.False(stats.RecordPush("group", ""));
        Assert.Empty(Map(stats.Snapshot()["groups"]));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => Zone;
    }
}
