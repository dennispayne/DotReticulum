using DotReticulum.Applications.Runtime;
using DotReticulum.Core;

namespace DotReticulum.Applications.Tests;

public sealed class DiscoveryUnixFactAttribute : FactAttribute
{
    public DiscoveryUnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix shell fixture; explicitly authorized Windows native providers are covered separately.";
    }
}

public sealed class DiscoveryWindowsFactAttribute : FactAttribute
{
    public DiscoveryWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Deliberate Windows-native provider extension; Unix providers are covered separately.";
    }
}

public sealed class DiscoveryMetadataTests
{
    private static DiscoveryAdvertisement Advertisement(string host = "native.example") => new()
    { Name = "metadata", TransportIdentity = new byte[16], ReachableOn = host, Port = 4242 };

    [Theory]
    [InlineData(" 51.5, -0.1, 10\r\n", 51.5, -0.1, 10)]
    [InlineData("-90,-180,-4000", -90, -180, -4000)]
    [InlineData("90,180,1000000", 90, 180, 1000000)]
    public void ParsesExecutableCoordinateOutput(string output, double latitude, double longitude, double height)
    {
        var coordinates = DiscoveryMetadata.ParseLocation(output);
        Assert.Equal(latitude, coordinates.Latitude);
        Assert.Equal(longitude, coordinates.Longitude);
        Assert.Equal(height, coordinates.Height);
    }

    [Theory]
    [InlineData("1,2")]
    [InlineData("91,0,0")]
    [InlineData("0,181,0")]
    [InlineData("0,0,-4001")]
    [InlineData("0,0,1000001")]
    [InlineData("NaN,0,0")]
    [InlineData("0,Infinity,0")]
    [InlineData("bad,0,0")]
    public void RejectsMalformedAndNonfiniteCoordinateOutput(string output) =>
        Assert.Throws<FormatException>(() => DiscoveryMetadata.ParseLocation(output));

    [Fact]
    public async Task StaticMetadataDoesNotInvokeAnExternalProcess()
    {
        var advertisement = Advertisement();
        Assert.Equal(advertisement, await DiscoveryMetadata.ResolveAsync(advertisement));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiscoveryMetadata.ResolveAsync(advertisement, cancellationToken: cancel.Token).AsTask());
    }

    [Fact]
    public async Task ConfiguredLocationMustExistAndNonExecutableFilesAreNotEndpoints()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "discovery-invalid-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                DiscoveryMetadata.ResolveAsync(Advertisement(), Path.Combine(directory, "missing-provider")).AsTask());
            var text = Path.Combine(directory, "not-executable.txt");
            File.WriteAllText(text, "native.example");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(text, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await Assert.ThrowsAsync<IOException>(() =>
                DiscoveryMetadata.ResolveAsync(Advertisement(), text, authorizeExecutable: _ => true).AsTask());
            await Assert.ThrowsAsync<IOException>(() =>
                DiscoveryMetadata.ResolveAsync(Advertisement(text), authorizeExecutable: _ => true).AsTask());
        }
        finally { Directory.Delete(directory, true); }
    }

    [DiscoveryWindowsFact]
    public async Task NativeWindowsProviderRequiresExplicitAuthorization()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "hostname.exe");
        Assert.True(File.Exists(executable));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => DiscoveryMetadata.ResolveAsync(Advertisement(executable)).AsTask());
        var resolved = await DiscoveryMetadata.ResolveAsync(Advertisement(executable), authorizeExecutable: _ => true);
        Assert.True(DiscoveryCodec.IsHost(resolved.ReachableOn));
        Assert.NotEqual(executable, resolved.ReachableOn);
    }

    [Fact]
    public async Task ExplicitHostMetadataPolicyCanSupplyResolvedValuesWithoutDefaultExecution()
    {
        using var identity = Identity.Generate();
        byte[]? published = null;
        var invoked = false;
        await using var announcer = new DiscoveryAnnouncer((payload, _) =>
        {
            published = payload.ToArray();
            return ValueTask.CompletedTask;
        }, metadataResolver: (advertisement, location, _) =>
        {
            invoked = true;
            Assert.Equal("configured-location-provider", location);
            return ValueTask.FromResult(advertisement with { ReachableOn = "resolved.example", Latitude = 1.5 });
        });
        announcer.Register("policy", TimeSpan.FromHours(6),
            _ => ValueTask.FromResult(Advertisement("configured-endpoint-provider") with { StampValue = 1 }),
            "configured-location-provider");
        await announcer.TickAsync();
        Assert.True(invoked);
        Assert.NotNull(published);
        var decoded = DiscoveryCodec.Decode(published, identity, 0, 1);
        Assert.Equal("resolved.example", decoded.ReachableOn);
        Assert.Equal(1.5, decoded.Latitude);
    }

    [DiscoveryUnixFact]
    public async Task ExecutableMetadataIsBoundedCancelableAndFailureIsExplicit()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "discovery-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string Script(string name, string command)
            {
                var path = Path.Combine(directory, name);
                File.WriteAllText(path, "#!/bin/sh\n" + command + "\n");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return path;
            }
            var host = Script("host", "printf 'reference.example\\n'");
            var location = Script("location", "printf '51.5,-0.1,10\\n'");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => DiscoveryMetadata.ResolveAsync(Advertisement(host), location).AsTask());
            var result = await DiscoveryMetadata.ResolveAsync(Advertisement(host), location, authorizeExecutable: _ => true);
            Assert.Equal("reference.example", result.ReachableOn);
            Assert.Equal(51.5, result.Latitude);
            Assert.Equal(-0.1, result.Longitude);
            Assert.Equal(10d, result.Height);
            var bad = Script("bad", "exit 7");
            await Assert.ThrowsAsync<IOException>(() => DiscoveryMetadata.ResolveAsync(Advertisement(bad), authorizeExecutable: _ => true).AsTask());
            var invalid = Script("invalid", "printf 'bad host!'");
            await Assert.ThrowsAsync<FormatException>(() => DiscoveryMetadata.ResolveAsync(Advertisement(invalid), authorizeExecutable: _ => true).AsTask());
            var slow = Script("slow", "sleep 5");
            await Assert.ThrowsAsync<TimeoutException>(() => DiscoveryMetadata.ResolveAsync(Advertisement(slow),
                commandTimeout: TimeSpan.FromMilliseconds(50), authorizeExecutable: _ => true).AsTask());
            var large = Script("large", "i=0; while [ $i -lt 5000 ]; do printf x; i=$((i+1)); done");
            await Assert.ThrowsAsync<IOException>(() => DiscoveryMetadata.ResolveAsync(Advertisement(large), authorizeExecutable: _ => true).AsTask());
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiscoveryMetadata.ResolveAsync(Advertisement(slow),
                cancellationToken: cancel.Token, authorizeExecutable: _ => true).AsTask());
        }
        finally { Directory.Delete(directory, true); }
    }
}
