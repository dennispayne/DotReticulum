using DotReticulum.Applications.Rngit;
using DotReticulum.Core;

namespace DotReticulum.Applications.Tests;

public sealed class RngitUnixFactAttribute : FactAttribute
{
    public RngitUnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix file permissions do not apply on Windows; protect inherited directory ACLs.";
    }
}

// Contracts: pinned RNS a29bfb51c468945c96a1ee22cae0114e993e2391, Utilities/rngit/client.py:55-213,679-718.
// client.py SHA-256: 78358331ddac087860fb8a1f350632d3875917782dc000681d48597affd8936b.
public sealed class RngitClientConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Directory.GetCurrentDirectory(), "rngit-config-tests-" + Guid.NewGuid().ToString("N"));
    private const string Hash = "0123456789abcdef0123456789abcdef";

    public RngitClientConfigurationTests() => Directory.CreateDirectory(directory);
    public void Dispose() => TestDirectory.DeleteWithRetry(directory);

    private RngitClientConfiguration Load(string text)
    {
        File.WriteAllText(Path.Combine(directory, "client_config"), text);
        return RngitClientConfiguration.Load(directory);
    }

    [Fact]
    public void DefaultsArePersistedWithoutCreatingAnIdentity()
    {
        var config = RngitClientConfiguration.Load(directory);
        Assert.Equal(directory, config.DirectoryPath);
        Assert.Equal(25, config.RefBatchSize);
        Assert.Equal(4, config.LogLevel);
        Assert.Equal(Path.Combine(directory, "client_config"), config.ConfigPath);
        Assert.Equal(Path.Combine(directory, "client_identity"), config.IdentityPath);
        Assert.Equal(Path.Combine(directory, "client_log"), config.LogPath);
        Assert.True(File.Exists(config.ConfigPath));
        Assert.False(File.Exists(config.IdentityPath));
        Assert.Equal(25, RngitClientConfiguration.Load(directory).RefBatchSize);
    }

    [Fact]
    public void DirectoryResolutionIsPureAndPreservesPinnedDefaultPath()
    {
        var explicitDirectory = Path.Combine(directory, "explicit");
        var environmentDirectory = Path.Combine(directory, "environment");
        Assert.Equal(explicitDirectory, RngitClientConfiguration.ResolveDirectory(explicitDirectory, environmentDirectory, directory, _ => throw new Exception(), _ => throw new Exception()));
        Assert.Equal(environmentDirectory, RngitClientConfiguration.ResolveDirectory(null, environmentDirectory, directory, _ => throw new Exception(), _ => throw new Exception()));
        var xdg = Path.Combine(directory, ".config", "rngit");
        Assert.Equal(Path.Combine(directory, ".rngit", "reticulum"),
            RngitClientConfiguration.ResolveDirectory(null, null, directory, p => p == xdg, p => p == Path.Combine(xdg, "config")));
        Assert.Equal(Path.Combine(directory, ".rngit"),
            RngitClientConfiguration.ResolveDirectory(null, null, directory, _ => false, _ => true));
        Assert.Equal(Path.Combine(directory, ".rngit"),
            RngitClientConfiguration.ResolveDirectory(null, null, directory, _ => true, _ => false));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("'25' # batch", 25)]
    [InlineData("+1024", 1024)]
    [InlineData("99999", 1024)]
    public void ReadsAndClampsBatchSize(string value, int expected) =>
        Assert.Equal(expected, Load($"[client]\nref_batch_size = {value}\n").RefBatchSize);

    [Theory]
    [InlineData("-999", -1)]
    [InlineData("-1", -1)]
    [InlineData("0", 0)]
    [InlineData("\"7\" # comment", 7)]
    [InlineData("8", 8)]
    [InlineData("99", 8)]
    public void UsesActualUpstreamLoggingConstants(string value, int expected) =>
        Assert.Equal(expected, Load($"[logging]\nloglevel = {value}").LogLevel);

    [Fact]
    public void ResolvesAliasesCaseSensitivelyAndLiteralHashesTakePrecedence()
    {
        var config = Load($"""
            # comment
            [aliases] # destinations
            "Node" = "{Hash.ToUpperInvariant()}"# trailing comment
            {Hash} = ffffffffffffffffffffffffffffffff
            [unknown]
            ignored = yes
            """);
        Assert.Equal($"rns://{Hash}/group/repository", config.ResolveUrl("rns://Node/group/repository"));
        Assert.Equal($"rns://{Hash}/group/repository", config.ResolveUrl($"rns://{Hash}/group/repository"));
        Assert.Throws<ArgumentException>(() => config.ResolveUrl("rns://node/group/repository"));
        Assert.Throws<ArgumentException>(() => config.ResolveUrl("https://Node/group/repository"));
        Assert.Throws<ArgumentException>(() => config.ResolveUrl("rns://Node/group/../repository"));
        Assert.Throws<ArgumentException>(() => config.ResolveUrl("rns://Node/group/repository?query"));
    }

    [Theory]
    [InlineData("[client]\nref_batch_size = 0")]
    [InlineData("[client]\nref_batch_size = -1")]
    [InlineData("[client]\nref_batch_size = 2147483648")]
    [InlineData("[client]\nref_batch_size = twenty")]
    [InlineData("[logging]\nloglevel = invalid")]
    [InlineData("[aliases]\nnode = nope")]
    [InlineData("[aliases]\nnode = 0000000000000000000000000000000g")]
    [InlineData("[aliases]\nnode = 000000000000000000000000000000")]
    [InlineData("[client]\nref_batch_size = 1\nref_batch_size = 2")]
    [InlineData("[client]\n[client]")]
    [InlineData("[[client]]")]
    [InlineData("[client")]
    [InlineData("[]")]
    [InlineData("[client] garbage")]
    [InlineData("ref_batch_size = 1")]
    [InlineData("[client]\nbroken")]
    [InlineData("[client]\n= value")]
    [InlineData("[client]\nref_batch_size = '1")]
    [InlineData("[client]\nref_batch_size = '1' garbage")]
    [InlineData("[client]\nref_batch_size = 1,2")]
    [InlineData("[client]\nref_batch_size = '''1'''")]
    [InlineData("[client]\nref_batch_size = %(other)s")]
    public void MalformedOrUnsupportedConfigurationHasADiagnostic(string text)
    {
        var error = Assert.Throws<FormatException>(() => Load(text));
        Assert.Contains("client_config line", error.Message);
        Assert.Equal(text, File.ReadAllText(Path.Combine(directory, "client_config")));
    }

    [Fact]
    public void SettingsAreCaseSensitiveLikeConfigObj()
    {
        Assert.Equal(25, Load("[Client]\nref_batch_size = 100\n[logging]\nLoglevel = 0").RefBatchSize);
        Assert.Equal(4, RngitClientConfiguration.Load(directory).LogLevel);
    }

    [Fact]
    public void ConfigurationInputIsBounded()
    {
        Assert.Throws<FormatException>(() => Load(new string(' ', 1024 * 1024 + 1)));
        Assert.Throws<FormatException>(() => Load(new string(' ', 8193)));
        Assert.Throws<FormatException>(() => Load(new string('\n', 16385)));
    }

    [Fact]
    public void IdentityIsRawPersistentAndDoesNotMigrateLegacyStorage()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(directory, "storage")).FullName;
        using var legacy = Identity.Generate();
        IdentityFile.SaveNew(legacy, Path.Combine(legacyDirectory, "rngit"));
        var config = RngitClientConfiguration.Load(directory);
        using var first = config.LoadOrCreateIdentity();
        using var second = config.LoadOrCreateIdentity();
        Assert.Equal(first.Hash, second.Hash);
        Assert.NotEqual(legacy.HexHash, first.HexHash);
        Assert.Equal(Identity.KeySize, new FileInfo(config.IdentityPath).Length);
        using var direct = IdentityFile.Load(config.IdentityPath);
        Assert.Equal(first.Hash, direct.Hash);
        Assert.Empty(Directory.GetFiles(directory, "*.new"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(65)]
    public void CorruptIdentityIsNeverReplaced(int size)
    {
        var config = RngitClientConfiguration.Load(directory);
        var original = new byte[size];
        File.WriteAllBytes(config.IdentityPath, original);
        Assert.Throws<InvalidDataException>(() => config.LoadOrCreateIdentity());
        Assert.Equal(original, File.ReadAllBytes(config.IdentityPath));
        Assert.Empty(Directory.GetFiles(directory, "*.new"));
    }

    [Fact]
    public async Task ConcurrentCreatorsConvergeOnOneCompleteIdentity()
    {
        var configs = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => RngitClientConfiguration.Load(directory))));
        using var start = new ManualResetEventSlim();
        var tasks = configs.Select(config => Task.Run(() =>
        {
            start.Wait();
            using var identity = config.LoadOrCreateIdentity();
            return identity.HexHash;
        })).ToArray();
        start.Set();
        var hashes = await Task.WhenAll(tasks);
        Assert.Single(hashes.Distinct());
        Assert.Empty(Directory.GetFiles(directory, "*.new"));
        using var persisted = IdentityFile.Load(configs[0].IdentityPath);
        Assert.Equal(hashes[0], persisted.HexHash);
    }

    [Fact]
    public void FailedIdentityPublicationCleansStagingFile()
    {
        var config = RngitClientConfiguration.Load(directory);
        Directory.CreateDirectory(config.IdentityPath);
        Assert.ThrowsAny<IOException>(() => config.LoadOrCreateIdentity());
        Assert.Empty(Directory.GetFiles(directory, "*.new"));
    }

    [RngitUnixFact]
    public void UnixIdentityCreationIsOwnerOnly()
    {
        var config = RngitClientConfiguration.Load(directory);
        using var identity = config.LoadOrCreateIdentity();
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config.IdentityPath));
    }
}
