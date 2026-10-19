using System.Text.Json;
using System.Security.Cryptography;
using DotReticulum.Applications.Rngit;

namespace DotReticulum.Applications.Tests;

public sealed class RngitMediaTests : IDisposable
{
    // Actual pinned media.py header/argument results, not live-node interoperability.
    private readonly PresentationProcessFixture fixture = new();
    private readonly JsonDocument vectors = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "rngit-media-templates.json")));

    [Fact]
    public void EncoderFamiliesOptionsAndHeaderDimensionsMatchUpstream()
    {
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", vectors.RootElement.GetProperty("upstream").GetString());
        foreach (var vector in vectors.RootElement.GetProperty("configured").EnumerateArray())
            Assert.Equal(vector.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()),
                RngitMedia.EncoderArguments(vector.GetProperty("backend").GetString()!, 120, 64));
        foreach (var vector in vectors.RootElement.GetProperty("webp_headers").EnumerateArray())
        {
            var actual = RngitMedia.WebpInfo(Convert.FromBase64String(vector.GetProperty("data").GetString()!));
            var expected = vector.GetProperty("dimensions");
            if (expected.ValueKind == JsonValueKind.Null) Assert.Null(actual);
            else Assert.Equal((expected[0].GetInt32(), expected[1].GetInt32()), actual!.Value);
        }
        Assert.DoesNotContain("-quality", RngitMedia.EncoderArguments("magick", null, 0));
        Assert.DoesNotContain("-resize", RngitMedia.EncoderArguments("magick", null, -1));
        Assert.Throws<ArgumentException>(() => RngitMedia.EncoderArguments("pillow"));
    }

    [Fact]
    public async Task BypassAndSupportedFailureAreDistinguishedAndPreserveOriginalBytes()
    {
        var diagnostics = new List<string>();
        var media = new RngitMedia(_ => null, diagnostics.Add);
        var data = new byte[] { 1, 2, 3 };
        foreach (var name in new[] { "x.webp", "x.WEBP", "x.svg", "x.txt" })
        {
            var result = await media.ConvertAsync(data, name);
            Assert.Equal(RngitMediaStatus.Bypassed, result.Status);
            Assert.Same(data, result.Data);
            Assert.Equal(name, result.FileName);
            Assert.Null(result.Error);
        }
        var disabled = await media.ConvertAsync(data, "x.png", new(Enabled: false));
        Assert.Equal(RngitMediaStatus.Bypassed, disabled.Status);
        var failed = await media.ConvertAsync(data, "x.png");
        Assert.Equal(RngitMediaStatus.Failed, failed.Status);
        Assert.Same(data, failed.Data);
        Assert.Equal("x.png", failed.FileName);
        Assert.NotEmpty(diagnostics);
        Assert.Equal(RngitMediaStatus.Failed, (await media.ConvertAsync(data, "second.png")).Status);
        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task ForcedUnknownAndUnavailableBackendNeverFallThroughToAnotherEncoder()
    {
        var discovered = new List<string>();
        var media = new RngitMedia(name => { discovered.Add(name); return name == "ffmpeg" ? fixture.PowerShell : null; });
        Assert.Equal(RngitMediaStatus.Failed, (await media.ConvertAsync([1], "x.png", new(Backend: "pillow"))).Status);
        Assert.Empty(discovered);
        Assert.Equal(RngitMediaStatus.Failed, (await media.ConvertAsync([1], "x.png", new(Backend: "magick"))).Status);
        Assert.Equal(new[] { "magick" }, discovered);
    }

    [Fact]
    public async Task CrashingMissingAndInvalidOutputEncodersAreDiagnosedWithoutSilentSuccess()
    {
        var media = new RngitMedia(_ => Path.Combine(fixture.Directory, "missing"));
        var result = await media.ConvertAsync([1], "x.png", new(Backend: "magick"));
        Assert.Equal(RngitMediaStatus.Failed, result.Status);
        Assert.Contains("(-1)", result.Error);
        // PowerShell is not an encoder: supplied upstream arguments must fail.
        media = new RngitMedia(_ => fixture.PowerShell);
        result = await media.ConvertAsync([1], "x.png", new(Backend: "ffmpeg", Timeout: TimeSpan.FromSeconds(15)));
        Assert.Equal(RngitMediaStatus.Failed, result.Status);
        Assert.Contains("failed", result.Error);
    }

    [Fact]
    public async Task OwnedBackendSuccessCrashInvalidTimeoutOverflowAndCancelAreBounded()
    {
        var data = new byte[] { 1, 2, 3 };
        var expected = Convert.FromBase64String(vectors.RootElement.GetProperty("webp_headers")[0].GetProperty("data").GetString()!);
        var media = new RngitMedia(_ => fixture.Backend);
        fixture.BackendMode("success", expected);
        var result = await media.ConvertAsync(data, "π.PNG", new(Backend: "magick"));
        Assert.Equal(RngitMediaStatus.Converted, result.Status);
        Assert.Equal(expected, result.Data);
        Assert.Equal("π.webp", result.FileName);
        Assert.Equal(RngitMedia.WebpInfo(expected)!.Value.Width, result.Width);
        foreach (var mode in new[] { "fail", "invalid", "timeout", "overflow" })
        {
            fixture.BackendMode(mode);
            result = await media.ConvertAsync(data, "x.png", new(Backend: "magick",
                Timeout: TimeSpan.FromMilliseconds(700), MaxOutputBytes: 512));
            Assert.Equal(RngitMediaStatus.Failed, result.Status);
            Assert.Same(data, result.Data);
            Assert.Equal("x.png", result.FileName);
            Assert.NotEmpty(result.Error!);
        }
        fixture.BackendMode("timeout");
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => media.ConvertAsync(data, "x.png",
            new(Backend: "magick"), stop.Token));
    }

    [Fact]
    public async Task DefaultSelectionUsesTheWinnerWithoutTryingLaterBackendsAfterFailure()
    {
        var names = new List<string>();
        var media = new RngitMedia(name => { names.Add(name); return name is "magick" or "ffmpeg" ? fixture.Backend : null; });
        fixture.BackendMode("fail");
        var result = await media.ConvertAsync([1], "x.png", new(Backend: ""));
        Assert.Equal("magick", result.Backend);
        Assert.Equal(new[] { "magick" }, names);
        names.Clear();
        result = await media.ConvertAsync([1], "x.png", new(Backend: ""));
        Assert.Equal("magick", result.Backend);
        Assert.Equal(new[] { "magick" }, names);
    }

    [Fact]
    public async Task InputLimitAndCancellationAreEnforcedEvenForBypass()
    {
        var media = new RngitMedia(_ => null);
        Assert.Equal(RngitMediaStatus.Failed,
            (await media.ConvertAsync(new byte[1025], "x.webp", new(MaxInputBytes: 1024))).Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => media.ConvertAsync([1], "x.png", stop: new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => media.ConvertAsync([1], "x.png", new(MaxOutputBytes: 0)));
    }

    [Fact]
    public void AvailableBackendsMaintainExactUpstreamOrderAndDoNotInventPillow()
    {
        var backends = new RngitMedia(name => name == "gm" ? "gm" : null).AvailableBackends();
        Assert.Equal(new[] { "magick", "convert", "gm", "ffmpeg", "avconv" }, backends.Select(value => value.Name));
        Assert.True(backends.Single(value => value.Name == "gm").Available);
        Assert.Equal(1, backends.Count(value => value.Available));
    }

    [RngitMediaNativeFact]
    public async Task ActualPinnedFfmpegConversionBytesDimensionsQualityAndInvalidInputMatchUpstream()
    {
        var backend = Environment.GetEnvironmentVariable("DOTRETICULUM_MEDIA_BACKEND")!;
        Assert.True(File.Exists(backend), "Configured FFmpeg executable is missing.");
        var native = vectors.RootElement.GetProperty("native");
        Assert.Equal(native.GetProperty("encoder_sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(backend))));
        var root = Path.GetDirectoryName(Path.GetDirectoryName(fixture.Directory))!;
        var data = await File.ReadAllBytesAsync(Path.Combine(root, "assets", "branding", "dotreticulum.png"));
        Assert.Equal(native.GetProperty("source_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(data)));
        var media = new RngitMedia(name => name == "ffmpeg" ? backend : null);
        foreach (var reference in native.GetProperty("cases").EnumerateArray())
        {
            var result = await media.ConvertAsync(data, "owned.png", new(Backend: "ffmpeg",
                Quality: reference.GetProperty("quality").ValueKind == JsonValueKind.Null ? null : reference.GetProperty("quality").GetInt32(),
                MaxDimension: reference.GetProperty("max_dimension").GetInt32()));
            Assert.Equal(RngitMediaStatus.Converted, result.Status);
            Assert.Equal("ffmpeg", result.Backend);
            Assert.Equal(reference.GetProperty("dimensions")[0].GetInt32(), result.Width);
            Assert.Equal(reference.GetProperty("dimensions")[1].GetInt32(), result.Height);
            Assert.Equal(Convert.FromBase64String(reference.GetProperty("output").GetString()!), result.Data);
        }
        var failed = await media.ConvertAsync("not an image"u8.ToArray(), "invalid.png", new(Backend: "ffmpeg"));
        Assert.Equal(RngitMediaStatus.Failed, failed.Status);
        Assert.Contains("failed", failed.Error);
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.webp"));
    }

    public void Dispose() { vectors.Dispose(); fixture.Dispose(); }
}

public sealed class RngitMediaNativeFactAttribute : FactAttribute
{
    public RngitMediaNativeFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTRETICULUM_MEDIA_BACKEND")))
            Skip = "Actual encoder acceptance is opt-in: set DOTRETICULUM_MEDIA_BACKEND to the pinned owned FFmpeg executable recorded in rngit-media-templates.json.";
    }
}
