using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotReticulum.Applications.Rngit;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

// Actual RNS 1.5.7 pages.py at a29bfb51c468945c96a1ee22cae0114e993e2391.
// GenerateRngitPageVectors.py records source SHA-256, actual imports and visible fixture shims.
public sealed class RngitPageTests : IDisposable
{
    private readonly string directory = Path.Combine(Directory.GetCurrentDirectory(), "rngit-pages-" + Guid.NewGuid().ToString("N"));
    private readonly JsonDocument fixture;
    private readonly string groupPath;
    private readonly string repoPath;
    public RngitPageTests()
    {
        fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rngit-pages.json")));
        Directory.CreateDirectory(directory);
        groupPath = Path.Combine(directory, "repositories");
        Directory.CreateDirectory(groupPath);
        repoPath = Path.Combine(groupPath, "demo");
        var bundle = Path.Combine(directory, "fixture.bundle");
        File.WriteAllBytes(bundle, Convert.FromHexString(fixture.RootElement.GetProperty("bundle").GetString()!));
        Run(["clone", "--bare", bundle, repoPath]);
        Run(["--git-dir=" + repoPath, "config", "repository.description", "Page fixture"]);
        Configure();
    }
    private void Configure(string extra = "") => File.WriteAllText(Path.Combine(directory, "config"),
        $"[rngit]\nnode_name = Test Node\n{extra}\n[repositories]\npublic = {groupPath}\n[access]\npublic = r:all\n[pages]\nunicode_icons = true\nmedia_conversion = false\n");
    private string Run(string[] args, byte[]? stdin = null, int[]? expectedCodes = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
            if (item.Key is string key && key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) environment[key] = null;
        environment["GIT_TERMINAL_PROMPT"] = "0";
        var result = RngitProcess.RunAsync("git", args, directory, stdin, TimeSpan.FromSeconds(8),
            1024 * 1024, environment: environment).GetAwaiter().GetResult();
        Assert.True(expectedCodes?.Contains(result.Code) ?? result.Code == 0, result.Error);
        return Encoding.UTF8.GetString(result.Output).Trim();
    }
    public void Dispose()
    {
        fixture.Dispose();
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
    private static object? Request(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => (object)p.Name, p => Request(p.Value)),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.GetInt64(),
        JsonValueKind.Array => element.EnumerateArray().Select(Request).ToArray(),
        _ => throw new InvalidDataException(),
    };
    private void Seed()
    {
        foreach (var file in fixture.RootElement.GetProperty("seed").EnumerateObject())
        {
            var path = Path.Combine(groupPath, file.Name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Convert.FromHexString(file.Value.GetString()!));
        }
    }
    [Fact]
    public async Task ActualPinnedPagesAndDownloadsMatchDifferentialBytes()
    {
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", fixture.RootElement.GetProperty("upstream").GetString());
        using var caller = Identity.FromPrivateKey(Convert.FromHexString(fixture.RootElement.GetProperty("private_key").GetString()!));
        var seeded = false;
        var differences = new List<string>();
        var initialConfiguration = RngitHostConfiguration.Load(directory);
        var standardService = new RngitPageService(new(initialConfiguration), initialConfiguration) { RepositoryDestinationHash = new byte[16] };
        foreach (var vector in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            if (vector.TryGetProperty("seeded", out _) && !seeded) { Seed(); seeded = true; }
            var hasMetadata = vector.TryGetProperty("upstream_config", out var upstreamConfig);
            if (hasMetadata)
            {
                foreach (var entry in upstreamConfig.EnumerateObject())
                {
                    Run(["--git-dir=" + repoPath, "config", "--unset-all", entry.Name], expectedCodes: [0, 5]);
                    if (entry.Value.ValueKind != JsonValueKind.Null)
                        Run(["--git-dir=" + repoPath, "config", "--replace-all", "--", entry.Name, entry.Value.GetString()!]);
                }
                Run(["--git-dir=" + repoPath, "symbolic-ref", "HEAD", "refs/heads/" + vector.GetProperty("head_ref").GetString()]);
            }
            var allowedPath = repoPath + ".allowed";
            if (vector.TryGetProperty("repository_allowed", out var allowed)) File.WriteAllText(allowedPath, allowed.GetString());
            else File.Delete(allowedPath);
            Configure(vector.TryGetProperty("blocked_anonymous", out _) ? "blocked_identities = " +
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[64]).AsSpan(0, 16)) : "");
            var configuration = RngitHostConfiguration.Load(directory);
            var diagnostics = new List<string>();
            var service = hasMetadata || vector.TryGetProperty("blocked_anonymous", out _) || vector.TryGetProperty("statistics", out _)
                ? new RngitPageService(new(configuration), configuration, diagnostics.Add) { RepositoryDestinationHash = new byte[16] } : standardService;
            if (vector.TryGetProperty("clock_timestamp", out var clock))
                service.Clock = new FixtureClock(clock.GetInt64());
            if (vector.TryGetProperty("known_source", out var knownSource))
            {
                var known = new KnownDestinations();
                known.Remember(Convert.FromHexString(knownSource.GetString()!), new byte[32], caller.ExportPublicKey(), null);
                service.KnownDestinations = known;
            }
            if (vector.TryGetProperty("statistics", out var statistics))
            {
                File.WriteAllBytes(configuration.StatisticsPath, Convert.FromHexString(statistics.GetString()!));
                service.Statistics = new(configuration, clock: new FixtureClock(vector.GetProperty("stats_timestamp").GetInt64()));
            }
            var result = await service.HandleAsync(vector.GetProperty("route").GetString()!, Request(vector.GetProperty("request")),
                vector.GetProperty("identified").GetBoolean() ? caller : null, CancellationToken.None,
                vector.TryGetProperty("thanks_link", out var linkId) ? Convert.FromHexString(linkId.GetString()!) : null);
            if (vector.TryGetProperty("response", out var expected))
            {
                var bytes = Assert.IsType<byte[]>(result);
                var expectedText = Encoding.UTF8.GetString(Convert.FromHexString(expected.GetString()!));
                var actualText = NormalizeGenerationTime(Encoding.UTF8.GetString(bytes));
                if (vector.TryGetProperty("redact_source", out var redaction))
                {
                    Assert.Contains(redaction.GetProperty("from").GetString()!, expectedText, StringComparison.Ordinal);
                    expectedText = expectedText.Replace(redaction.GetProperty("from").GetString()!, redaction.GetProperty("to").GetString()!, StringComparison.Ordinal);
                    foreach (var value in vector.GetProperty("sensitive_values").EnumerateArray())
                    {
                        Assert.DoesNotContain(value.GetString()!, actualText, StringComparison.Ordinal);
                        Assert.All(diagnostics, message => Assert.DoesNotContain(value.GetString()!, message, StringComparison.Ordinal));
                    }
                    Assert.Equal(redaction.GetProperty("from").GetString(),
                        Run(["--git-dir=" + repoPath, "config", "--get", "repository.rngit.upstream.source"]));
                }
                if (hasMetadata && vector.GetProperty("route").GetString() != "/page/repo.mu")
                {
                    Assert.DoesNotContain("Forked from", actualText);
                    Assert.DoesNotContain("Mirrored from", actualText);
                }
                if (expectedText != actualText)
                {
                    var at = Enumerable.Range(0, Math.Min(expectedText.Length, actualText.Length)).FirstOrDefault(i => expectedText[i] != actualText[i], Math.Min(expectedText.Length, actualText.Length));
                    differences.Add($"{name} differs at {at}\nEXPECTED: {expectedText[Math.Max(0, at - 40)..Math.Min(expectedText.Length, at + 200)]}\nACTUAL: {actualText[Math.Max(0, at - 40)..Math.Min(actualText.Length, at + 200)]}");
                }
                Assert.NotEqual((byte)0, bytes[0]);
            }
            else if (vector.TryGetProperty("file", out var expectedFile))
            {
                var response = Assert.IsType<FileResponse>(result);
                Assert.Equal(Convert.FromHexString(expectedFile.GetString()!), response.Data);
                if (vector.TryGetProperty("workdoc", out _)) Assert.Equal(vector.GetProperty("name").GetString(), response.Metadata);
                else
                {
                    var metadata = Assert.IsType<Dictionary<object, object?>>(response.Metadata);
                    Assert.Equal(vector.GetProperty("name").GetString(), Encoding.UTF8.GetString(Assert.IsType<byte[]>(metadata["name"])));
                }
            }
            else if (vector.TryGetProperty("upstream_exception", out _))
            {
                // Pinned workdoc's missing-root diagnostic slices an int and raises TypeError.
                Assert.Null(result);
            }
            else if (vector.GetProperty("value").ValueKind == JsonValueKind.False) Assert.False(Assert.IsType<bool>(result));
            else Assert.Null(result);
        }
        Assert.True(differences.Count == 0, string.Join("\n\n", differences));
    }
    private sealed class FixtureClock(long timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(timestamp);
    }
    private static string NormalizeGenerationTime(string text)
    {
        var match = Regex.Match(text, @" - Generated in (?<duration>[^\r\n]+)`f\z");
        Assert.True(match.Success, "Page must contain actual generation timing, not an unknown placeholder.");
        Assert.Matches(@"\A(?:0s|(?:[0-9]+[dhm]|[0-9]+\.[0-9]{1,2}s)(?:(?:, | and )(?:[0-9]+[dhm]|[0-9]+\.[0-9]{1,2}s))*)\z",
            match.Groups["duration"].Value);
        return text[..match.Index] + " - Unknown generation time`f";
    }
    [Fact]
    public void GenerationTimesMatchPinnedPrettytimeRoundingAndUnits()
    {
        foreach (var vector in fixture.RootElement.GetProperty("generation_times").EnumerateArray())
            Assert.Equal(vector.GetProperty("output").GetString(), RngitPageService.GenerationTime(vector.GetProperty("seconds").GetDouble()));
        Assert.Throws<ArgumentOutOfRangeException>(() => RngitPageService.GenerationTime(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => RngitPageService.GenerationTime(double.PositiveInfinity));
    }
    [Fact]
    public void ProvenanceSyncAgesMatchPinnedCompactPrettytime()
    {
        foreach (var vector in fixture.RootElement.GetProperty("sync_ages").EnumerateArray())
            Assert.Equal(vector.GetProperty("output").GetString(), RngitPageService.SyncAge(vector.GetProperty("seconds").GetDouble()));
    }
    [Fact]
    public async Task ConcurrentRequestsMeasureGenerationTimeAndPreserveLiteralUnknownText()
    {
        var templatePath = Path.Combine(directory, "templates");
        Directory.CreateDirectory(templatePath);
        File.WriteAllText(Path.Combine(templatePath, "base.mu"), "{GEN_TIME}|{GEN_TIME}|Unknown generation time|{PAGE_CONTENT}");
        File.WriteAllText(Path.Combine(templatePath, "front.mu"), "Unknown generation time\n{PAGE_CONTENT}");
        var config = RngitHostConfiguration.Load(directory);
        var service = new RngitPageService(new(config), config);
        var responses = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ =>
            service.HandleAsync("/page/index.mu", null, null, CancellationToken.None)));
        foreach (var response in responses)
        {
            var text = Encoding.UTF8.GetString(Assert.IsType<byte[]>(response));
            var parts = text.Split('|', 4);
            Assert.StartsWith("Generated in ", parts[0]);
            Assert.Equal(parts[0], parts[1]);
            Assert.Equal("Unknown generation time", parts[2]);
            Assert.StartsWith("Unknown generation time\n", parts[3]);
        }
    }
    [Fact]
    public void MarkdownUsesActualPinnedBlockInlineAndPlainCodeVectors()
    {
        foreach (var vector in fixture.RootElement.GetProperty("markdown").EnumerateArray())
            Assert.Equal(vector.GetProperty("output").GetString(),
                MarkdownMicron.Format(vector.GetProperty("input").GetString()!, vector.GetProperty("scope").GetString()));
    }
    [Fact]
    public void StatsChartsUseActualPinnedColorGradientAndPixelVectors()
    {
        static long[] Data(JsonElement value) => value.EnumerateArray().Select(i => i.GetInt64()).ToArray();
        static string[] Labels(JsonElement value) => value.EnumerateArray().Select(i => i.GetString()!).ToArray();
        foreach (var chart in fixture.RootElement.GetProperty("charts").EnumerateArray())
            Assert.Equal(chart.GetProperty("output").GetString(),
                RngitPageService.RenderChart(Data(chart.GetProperty("data")), Labels(chart.GetProperty("labels")), "10b981", "1c5e71"));
        var combined = fixture.RootElement.GetProperty("combined_chart");
        Assert.Equal(combined.GetProperty("output").GetString(), RngitPageService.RenderCombinedChart(
            Data(combined.GetProperty("views")), Data(combined.GetProperty("fetches")), Data(combined.GetProperty("pushes")),
            Data(combined.GetProperty("downloads")), Labels(combined.GetProperty("labels"))));
    }
    [Fact]
    public void SizeFormattingMatchesPythonBinaryFloatRounding()
    {
        foreach (var size in fixture.RootElement.GetProperty("sizes").EnumerateArray())
            Assert.Equal(size.GetProperty("output").GetString(), RngitPageService.Size(size.GetProperty("bytes").GetInt64()));
    }
    [Fact]
    public async Task AllRoutesAreBoundedAndRejectInvalidUnidentifiedFileRequests()
    {
        var config = RngitHostConfiguration.Load(directory);
        var service = new RngitPageService(new(config), config);
        Assert.Equal(17, RngitPageService.Paths.Count);
        Assert.Null(await service.HandleAsync("/unknown", null, null, CancellationToken.None));
        foreach (var route in RngitPageService.Paths.Where(p => p.StartsWith("/file/", StringComparison.Ordinal)))
            Assert.Null(await service.HandleAsync(route, new Dictionary<object, object?>(), null, CancellationToken.None));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.HandleAsync("/page/index.mu", null, null, stop.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownMicron.Format(new string('x', 1024 * 1024 + 1)));
    }
    [Theory]
    [InlineData("../config")]
    [InlineData("%2e%2e%2fconfig")]
    [InlineData("directory%2f..%2fhello.txt")]
    [InlineData("C%3a%5cWindows%5cwin.ini")]
    [InlineData("%00")]
    public async Task DownloadsRejectUnsafePaths(string path)
    {
        var config = RngitHostConfiguration.Load(directory);
        var service = new RngitPageService(new(config), config);
        var request = new Dictionary<object, object?> { ["var_g"] = "public", ["var_r"] = "demo", ["var_path"] = path };
        Assert.Null(await service.HandleAsync("/file/download", request, null, CancellationToken.None));
    }
    [Fact]
    public async Task SourceStatsUnavailableAndPermissionGateNeverLeakCounters()
    {
        Configure();
        File.WriteAllText(repoPath + ".allowed", "stats:all\n");
        var config = RngitHostConfiguration.Load(directory);
        var service = new RngitPageService(new(config), config);
        var request = new Dictionary<object, object?> { ["var_g"] = "public", ["var_r"] = "demo" };
        var response = Assert.IsType<byte[]>(await service.HandleAsync("/page/stats.mu", request, null, CancellationToken.None));
        Assert.Contains("Stats Unavailable", Encoding.UTF8.GetString(response));
        service.Statistics = new(config);
        response = Assert.IsType<byte[]>(await service.HandleAsync("/page/stats.mu", request, null, CancellationToken.None));
        Assert.Contains("No development activity recorded", Encoding.UTF8.GetString(response));
        File.WriteAllText(repoPath + ".allowed", "stats:none\n");
        response = Assert.IsType<byte[]>(await service.HandleAsync("/page/stats.mu", request, null, CancellationToken.None));
        Assert.DoesNotContain("total", Encoding.UTF8.GetString(response));
    }
    [Fact]
    public async Task DiagnosticCallbackFailureDoesNotDisablePageProcessing()
    {
        var config = RngitHostConfiguration.Load(directory);
        var service = new RngitPageService(new(config), config, _ => throw new InvalidOperationException("Observer failure"));
        var request = new Dictionary<object, object?> { ["var_g"] = "public", ["var_r"] = "demo", ["var_thanks"] = "y" };
        var response = Assert.IsType<byte[]>(await service.HandleAsync("/page/repo.mu", request, null, CancellationToken.None));
        Assert.Contains("Thanks (0)", Encoding.UTF8.GetString(response));
        Assert.DoesNotContain("Could not process request", Encoding.UTF8.GetString(response));
    }
    [Fact]
    public async Task MediaComponentsPreserveUtf8PlusAndSlashInGitReferences()
    {
        var config = RngitHostConfiguration.Load(directory);
        var service = new RngitPageService(new(config), config);
        var blob = new Dictionary<object, object?> { ["var_g"] = "public", ["var_r"] = "demo", ["var_ref"] = "feature/é+", ["var_path"] = "image.webp" };
        var page = Assert.IsType<byte[]>(await service.HandleAsync("/page/blob.mu", blob, null, CancellationToken.None));
        const string path = "/media/public/demo/feature%2F%C3%A9%2B/image.webp";
        // Unlike the reference's unescaped ref component, this cannot split a branch into path components.
        Assert.Contains(path, Encoding.UTF8.GetString(page));
        var media = Assert.IsType<FileResponse>(await service.HandleAsync("/media",
            new Dictionary<object, object?> { ["key"] = "k", ["path"] = path }, null, CancellationToken.None));
        Assert.Equal(Convert.FromHexString(fixture.RootElement.GetProperty("files").GetProperty("image.webp").GetString()!), media.Data);
    }
    [Fact]
    public async Task GitSymlinksDisplayTargetsButAreNeverFollowedOrDownloaded()
    {
        var target = Run(["--git-dir=" + repoPath, "hash-object", "-w", "--stdin"], Encoding.UTF8.GetBytes("../outside-secret"));
        Run(["--git-dir=" + repoPath, "read-tree", "HEAD"]);
        Run(["--git-dir=" + repoPath, "update-index", "--add", "--cacheinfo", "120000," + target + ",link"]);
        var tree = Run(["--git-dir=" + repoPath, "write-tree"]);
        var parent = Run(["--git-dir=" + repoPath, "rev-parse", "HEAD"]);
        Run(["--git-dir=" + repoPath, "config", "user.name", "Pages"]);
        Run(["--git-dir=" + repoPath, "config", "user.email", "pages@example.invalid"]);
        var commit = Run(["--git-dir=" + repoPath, "commit-tree", tree, "-p", parent], Encoding.UTF8.GetBytes("Symlink fixture"));
        Run(["--git-dir=" + repoPath, "update-ref", "refs/heads/main", commit]);
        var config = RngitHostConfiguration.Load(directory);
        var service = new RngitPageService(new(config), config);
        var request = new Dictionary<object, object?> { ["var_g"] = "public", ["var_r"] = "demo", ["var_path"] = "link" };
        Assert.Null(await service.HandleAsync("/file/download", request, null, CancellationToken.None));
        var response = Assert.IsType<byte[]>(await service.HandleAsync("/page/blob.mu", request, null, CancellationToken.None));
        Assert.Contains("Symlink → ../outside-secret", Encoding.UTF8.GetString(response));
        Assert.Null(await service.HandleAsync("/file/download",
            new Dictionary<object, object?> { ["var_g"] = "public", ["var_r"] = "demo", ["var_path"] = "hello.txt", ["var_ref"] = "--output=outside" }, null, CancellationToken.None));
    }
    [RngitUnixFact]
    public async Task DownloadAndArtifactRejectFilesystemLinkEscapes()
    {
        Seed();
        var outside = Path.Combine(directory, "outside");
        File.WriteAllText(outside, "must not be returned");
        var artifact = Path.Combine(repoPath + ".releases", "v1", "artifacts", "escape");
        File.CreateSymbolicLink(artifact, outside);
        var config = RngitHostConfiguration.Load(directory);
        var service = new RngitPageService(new(config), config);
        Assert.Null(await service.HandleAsync("/file/artifact",
            new Dictionary<object, object?> { ["var_g"] = "public", ["var_r"] = "demo", ["var_t"] = "v1", ["var_a"] = "escape" }, null, CancellationToken.None));
    }
}
