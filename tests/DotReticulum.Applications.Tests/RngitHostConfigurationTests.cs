using System.Text.Json;
using DotReticulum.Applications.Rngit;
using DotReticulum.Core;

namespace DotReticulum.Applications.Tests;

// Source: RNS a29bfb51c468945c96a1ee22cae0114e993e2391 server.py:1990-2260,4912-5081.
// SHA-256: 6257193aed6785359f4400da8d5413e7717ac5542e59666ac0f7f79e20975693.
// GenerateRngitHostVectors.py executes actual __apply_config, not a port.
public sealed class RngitHostConfigurationTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef";
    private readonly string directory = Path.Combine(Directory.GetCurrentDirectory(), "rngit-host-config-" + Guid.NewGuid().ToString("N"));

    public RngitHostConfigurationTests() => Directory.CreateDirectory(directory);
    public void Dispose() => TestDirectory.DeleteWithRetry(directory);
    private RngitHostConfiguration Load(string text, Action<string>? diagnostic = null)
    {
        File.WriteAllText(Path.Combine(directory, "config"), text);
        return RngitHostConfiguration.Load(directory, diagnostic);
    }

    [Fact]
    public void DefaultsPersistWithoutIdentityAndMissingGroupPathsAreDiagnosed()
    {
        var diagnostics = new List<string>();
        var config = RngitHostConfiguration.Load(directory, diagnostics.Add);
        Assert.Equal("Anonymous Git Node", config.NodeName);
        Assert.Equal(TimeSpan.FromHours(6), config.AnnounceInterval);
        Assert.Equal(TimeSpan.FromDays(1), config.MirrorInterval);
        Assert.False(config.RecordStats);
        Assert.False(config.ServeNomadnet);
        Assert.False(config.UnicodeIcons);
        Assert.True(config.MediaConversion);
        Assert.Equal(4, config.LogLevel);
        Assert.Equal(directory, config.DirectoryPath);
        Assert.Equal(Path.Combine(directory, "repositories_identity"), config.IdentityPath);
        Assert.Equal(Path.Combine(directory, "server_log"), config.LogPath);
        Assert.False(File.Exists(config.IdentityPath));
        Assert.False(File.Exists(config.StatisticsPath));
        Assert.Empty(config.Groups);
        Assert.Equal(3, diagnostics.Count);
        Assert.All(diagnostics, value => Assert.Contains("does not exist", value));
        Assert.Equal(["r:all", "w:9710b86ba12c42d1d8f30f74fe509286"], config.Access["public"]);
        Assert.Equal(TimeSpan.FromHours(6), RngitHostConfiguration.Load(directory).AnnounceInterval);
        Assert.False(Directory.Exists(Path.Combine(directory, "public")));
    }

    [Fact]
    public void DifferentialConfigurationMatchesActualPinnedMethods()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rngit-host.json")));
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", fixture.RootElement.GetProperty("upstream").GetString());
        foreach (var vector in fixture.RootElement.GetProperty("configurations").EnumerateArray())
        {
            var config = Load(vector.GetProperty("config").GetString()!);
            Assert.Equal(vector.GetProperty("node_name").GetString(), config.NodeName);
            Assert.Equal(vector.GetProperty("announce_seconds").GetInt32(), config.AnnounceInterval.TotalSeconds);
            Assert.Equal(vector.GetProperty("mirror_seconds").GetInt32(), config.MirrorInterval.TotalSeconds);
            Assert.Equal(vector.GetProperty("record_stats").GetBoolean(), config.RecordStats);
            Assert.Equal(vector.GetProperty("serve_nomadnet").GetBoolean(), config.ServeNomadnet);
            Assert.Equal(vector.GetProperty("loglevel").GetInt32(), config.LogLevel);
            Assert.Equal(vector.GetProperty("aliases").EnumerateObject().Select(pair => new KeyValuePair<string, string>(pair.Name, pair.Value.GetString()!)),
                config.IdentityAliases);
            Assert.Equal(vector.GetProperty("ignored").EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal),
                config.IgnoredIdentities.Order(StringComparer.Ordinal));
            Assert.Equal(vector.GetProperty("push_ignored").EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal),
                config.PushIgnoredIdentities.Order(StringComparer.Ordinal));
            Assert.Equal(vector.GetProperty("blocked").EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal),
                config.BlockedIdentities.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void DiscoveryPreservesSystemPriorityAndPinnedXdgLegacyDestination()
    {
        var system = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "etc", "rngit"));
        var xdg = Path.Combine(directory, ".config", "rngit");
        Assert.Equal(directory, RngitHostConfiguration.ResolveDirectory(directory, directory, _ => throw new Exception(), _ => throw new Exception()));
        Assert.Equal(system, RngitHostConfiguration.ResolveDirectory(null, directory, _ => true, _ => true));
        Assert.Equal(Path.Combine(directory, ".rngit", "reticulum"), RngitHostConfiguration.ResolveDirectory(null, directory,
            path => path == xdg, path => path == Path.Combine(xdg, "config")));
        Assert.Equal(Path.Combine(directory, ".rngit"), RngitHostConfiguration.ResolveDirectory(null, directory, _ => true, _ => false));
    }

    [Fact]
    public void GroupsAreExistingPathsAndAccessListsRemainRawReadOnlyAndCaseSensitive()
    {
        var group = Directory.CreateDirectory(Path.Combine(directory, "group # one")).FullName;
        var config = Load($"""
            [repositories]
            Public = "{group}"
            missing = "{Path.Combine(directory, "missing")}"
            [aliases]
            Alice = {Hash.ToUpperInvariant()}
            [access]
            Public = "r:all", 'w:Alice', "invalid,raw", s:none,
            empty = ,
            quoted_empty = "",
            inner_empty = r:all, '', w:Alice
            scalar = rw:Alice
            [pages]
            unicode_icons = yes
            media_conversion = off
            """);
        Assert.Single(config.Groups);
        Assert.Equal(group, config.Groups["Public"]);
        Assert.False(config.Groups.ContainsKey("public"));
        Assert.Equal(Hash, config.IdentityAliases["Alice"]);
        Assert.False(config.IdentityAliases.ContainsKey("alice"));
        Assert.Equal(["r:all", "w:Alice", "invalid,raw", "s:none"], config.Access["Public"]);
        Assert.Empty(config.Access["empty"]);
        Assert.Equal([""], config.Access["quoted_empty"]);
        Assert.Equal(["r:all", "", "w:Alice"], config.Access["inner_empty"]);
        Assert.Equal(["rw:Alice"], config.Access["scalar"]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.Access["Public"])[0] = "changed");
        Assert.True(config.UnicodeIcons);
        Assert.False(config.MediaConversion);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("NO", false)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    public void ConfigObjBooleans(string value, bool expected) =>
        Assert.Equal(expected, Load($"[rngit]\nrecord_stats = {value}").RecordStats);

    [Theory]
    [InlineData("[[rngit]]")]
    [InlineData("[rngit] junk")]
    [InlineData("[]")]
    [InlineData("key = value")]
    [InlineData("[rngit]\nnode_name = x\nnode_name = y")]
    [InlineData("[rngit]\n[rngit]")]
    [InlineData("[rngit]\nnode_name = 'unterminated")]
    [InlineData("[rngit]\nnode_name = 'closed' garbage")]
    [InlineData("[rngit]\nnode_name = '''multiline'''")]
    [InlineData("[rngit]\nnode_name = %(interpolation)s")]
    [InlineData("[rngit]\nnode_name = one,two")]
    [InlineData("[access]\ngroup = a,,b")]
    [InlineData("[rngit]\nrecord_stats = maybe")]
    [InlineData("[rngit]\nannounce_interval = 2147483648")]
    [InlineData("[rngit]\nmirror_interval = bad")]
    [InlineData("[rngit]\nmirror_interval = 2147483647")]
    [InlineData("[logging]\nloglevel = 1,")]
    public void MalformedAndUnsupportedConfigFailsWithoutReplacingFile(string text)
    {
        Assert.Throws<FormatException>(() => Load(text));
        Assert.Equal(text, File.ReadAllText(Path.Combine(directory, "config")));
    }

    [Fact]
    public void InputIsBoundedAndUtf8IsStrict()
    {
        Assert.Throws<FormatException>(() => Load(new string(' ', 1024 * 1024 + 1)));
        Assert.Throws<FormatException>(() => Load(new string(' ', 8193)));
        Assert.Throws<FormatException>(() => Load(new string('\n', 16385)));
        File.WriteAllBytes(Path.Combine(directory, "config"), [0xff]);
        Assert.Throws<System.Text.DecoderFallbackException>(() => RngitHostConfiguration.Load(directory));
    }

    [Fact]
    public void IntegerBoundsNegativeAnnounceAndIdentityListsAreExplicit()
    {
        var diagnostics = new List<string>();
        var config = Load($"""
            [rngit]
            announce_interval = -2147483648
            mirror_interval = -2147483648
            blocked_identities = Alice, {Hash.ToUpperInvariant()}, invalid
            stats_ignore_identities = alice
            [aliases]
            Alice = {Hash}
            all = {Hash}
            broken = nope
            [logging]
            loglevel = -2147483648
            [unknown]
            ignored = "flat", "list"
            """, diagnostics.Add);
        Assert.Equal(TimeSpan.FromMinutes(int.MinValue), config.AnnounceInterval);
        Assert.Equal(TimeSpan.Zero, config.MirrorInterval);
        Assert.Equal(-1, config.LogLevel);
        Assert.Single(config.BlockedIdentities);
        Assert.Contains(Hash, config.BlockedIdentities);
        Assert.Empty(config.IgnoredIdentities);
        Assert.Single(config.IdentityAliases);
        Assert.Equal(4, diagnostics.Count);
    }

    [Fact]
    public void LiteralHashesAndReservedWordsTakePrecedenceOverAliasLookup()
    {
        const string other = "ffffffffffffffffffffffffffffffff";
        var config = Load($"""
            [aliases]
            {Hash} = {other}
            ALL = {other}
            [rngit]
            blocked_identities = {Hash}, ALL
            """);
        Assert.Equal(other, config.IdentityAliases[Hash]);
        Assert.Equal(other, config.IdentityAliases["ALL"]);
        Assert.Single(config.BlockedIdentities);
        Assert.Contains(Hash, config.BlockedIdentities);
    }

    [Fact]
    public void IdentityIsRawPersistentAndSeparateFromClientIdentity()
    {
        var config = Load("");
        using var client = Identity.Generate();
        IdentityFile.SaveNew(client, Path.Combine(directory, "client_identity"));
        using var first = config.LoadOrCreateIdentity();
        using var restart = RngitHostConfiguration.Load(directory).LoadOrCreateIdentity();
        Assert.Equal(first.Hash, restart.Hash);
        Assert.NotEqual(client.HexHash, first.HexHash);
        Assert.Equal(64, new FileInfo(config.IdentityPath).Length);
        Assert.Empty(Directory.GetFiles(directory, "*.new"));
    }

    [Fact]
    public void WindowsIdentityCreationWarnsAboutInheritedDirectoryAcls()
    {
        var diagnostics = new List<string>();
        var config = Load("", diagnostics.Add);
        using var identity = config.LoadOrCreateIdentity();
        if (OperatingSystem.IsWindows())
            Assert.Contains("inherits directory ACLs", Assert.Single(diagnostics));
        else
            Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(65)]
    public void CorruptIdentityIsNeverReplaced(int size)
    {
        var config = Load("");
        var bytes = new byte[size];
        File.WriteAllBytes(config.IdentityPath, bytes);
        Assert.Throws<InvalidDataException>(() => config.LoadOrCreateIdentity());
        Assert.Equal(bytes, File.ReadAllBytes(config.IdentityPath));
    }

    [Fact]
    public async Task ConcurrentCreatorsConvergeWithoutPartialIdentitiesOrStagingFiles()
    {
        var configs = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => RngitHostConfiguration.Load(directory))));
        var hashes = await Task.WhenAll(configs.Select(config => Task.Run(() =>
        {
            using var identity = config.LoadOrCreateIdentity();
            return identity.HexHash;
        })));
        Assert.Single(hashes.Distinct());
        Assert.Empty(Directory.GetFiles(directory, "*.new"));
    }

    [Fact]
    public void PublicationFailureCleansOwnedStaging()
    {
        var config = Load("");
        Directory.CreateDirectory(config.IdentityPath);
        Assert.ThrowsAny<IOException>(() => config.LoadOrCreateIdentity());
        Assert.Empty(Directory.GetFiles(directory, "*.new"));
    }

    [RngitUnixFact]
    public void UnixIdentityIsOwnerOnly()
    {
        var config = Load("");
        using var identity = config.LoadOrCreateIdentity();
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config.IdentityPath));
    }
}
