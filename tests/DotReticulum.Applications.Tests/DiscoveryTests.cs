using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Applications.Runtime;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;

namespace DotReticulum.Applications.Tests;

/// <summary>
/// Independent vectors execute hosted RNS a29bfb51c468945c96a1ee22cae0114e993e2391 and LXMF 1.2.1.
/// Discovery SHA256 ae26445adf6325ed3e47f2b7c4b412060994d75ae583a58162849507ed7a9950.
/// Reproduce with GenerateDiscoveryVectors.py; no production Python dependency.
/// </summary>
public sealed class DiscoveryTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "DiscoveryVectors.json")));
    private static byte[] Hex(JsonElement root, string key) => Convert.FromHexString(root.GetProperty(key).GetString()!);
    private static Identity ReferenceIdentity()
    {
        using var vectors = Vectors();
        return Identity.FromPrivateKey(Hex(vectors.RootElement, "private_key"));
    }
    private static byte[] ReferencePayload(bool encrypted = false)
    {
        using var vectors = Vectors();
        return Hex(vectors.RootElement, encrypted ? "encrypted" : "plain");
    }
    private static DiscoveryOptions Options(int maximum = 0, int capacity = 2048) => new()
    {
        Enabled = true, RequiredStampValue = 8, MaxAutoConnected = maximum, Capacity = capacity,
    };
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Time { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        public override DateTimeOffset GetUtcNow() => Time;
        public void Advance(double seconds) => Time += TimeSpan.FromSeconds(seconds);
    }
    private sealed class DirectoryLease : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "discovery-" + Guid.NewGuid().ToString("N"));
        public DirectoryLease() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private static DiscoveryAdvertisement Advertisement(Identity identity, string name = "Test TCP") => new()
    {
        InterfaceType = "BackboneInterface", Name = name, TransportIdentity = identity.Hash,
        Transport = true, Implementation = "RNS", Version = "1.5.7",
        ReachableOn = "test.example", Port = 4242, StampValue = 8,
    };

    [Fact]
    public void PythonWorkblockStampEncryptedAndDiscoveryHashMatch()
    {
        using var vectors = Vectors();
        var root = vectors.RootElement;
        using var identity = ReferenceIdentity();
        var packed = Hex(root, "packed");
        var block = DiscoveryCodec.Workblock(SHA256.HashData(packed));
        Assert.Equal(root.GetProperty("workblock_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(block)));
        Assert.Equal(root.GetProperty("value").GetInt32(), DiscoveryCodec.StampValue(block, Hex(root, "stamp")));
        var plain = DiscoveryCodec.Decode(Hex(root, "plain"), identity, 2, 8);
        var encrypted = DiscoveryCodec.Decode(Hex(root, "encrypted"), identity, 2, 8, identity);
        Assert.Equal(root.GetProperty("discovery_hash").GetString(), plain.DiscoveryHash);
        Assert.Equal(plain.Packed, encrypted.Packed);
        Assert.Equal(plain.StampValue, encrypted.StampValue);
        Assert.Equal("Reference TCP", plain.Name);
        Assert.Equal("public", plain.NetworkName);
        Assert.Equal("test-only", plain.Passphrase);
        Assert.Equal(2, plain.Hops);
    }

    [Fact]
    public async Task ActualReferenceReceiverAcceptanceAndSourceVectorsMatch()
    {
        using var identity = ReferenceIdentity();
        using var vectors = Vectors();
        foreach (var entry in vectors.RootElement.GetProperty("receiver_cases").EnumerateArray())
        {
            using var directory = new DirectoryLease();
            var options = Options() with
            {
                RequiredStampValue = entry.GetProperty("required").GetInt32(),
                AllowedSources = entry.GetProperty("sources").EnumerateArray().Select(s => Convert.FromHexString(s.GetString()!)).ToArray(),
            };
            await using var service = new DiscoveryService(directory.Path, options, networkIdentity: identity);
            var actual = await service.ReceiveAsync(identity, Convert.FromHexString(entry.GetProperty("payload").GetString()!), 2);
            Assert.Equal(entry.GetProperty("accepted").GetBoolean(), actual);
        }
    }

    [Fact]
    public void PublishingUsesExactKeysOrderingAndHonestImplementation()
    {
        using var identity = ReferenceIdentity();
        var record = DiscoveryCodec.Decode(ReferencePayload(), identity, 2, 8);
        var advertisement = new DiscoveryAdvertisement
        {
            InterfaceType = record.InterfaceType, Transport = true, TransportIdentity = identity.Hash,
            Name = record.Name, Implementation = "RNS", Version = "1.5.7",
            ReachableOn = record.ReachableOn!, Port = record.Port!.Value,
            Latitude = record.Latitude, Longitude = record.Longitude, Height = record.Height,
            NetworkName = record.NetworkName, Passphrase = record.Passphrase, PublishIfac = true,
            OperatorAddress = record.OperatorAddress,
        };
        using var vectors = Vectors();
        Assert.Equal(Hex(vectors.RootElement, "advertised_packed"), DiscoveryCodec.PackAdvertisement(advertisement));
        var map = Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(DiscoveryCodec.PackAdvertisement(advertisement)));
        var original = Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(record.Packed));
        foreach (var item in original)
        {
            Assert.True(map.ContainsKey(item.Key));
            if (item.Value is byte[] bytes) Assert.Equal(bytes, Assert.IsType<byte[]>(map[item.Key]));
            else Assert.Equal(item.Value, map[item.Key]);
        }
        var honest = Advertisement(identity) with { Implementation = "DotReticulum" };
        Assert.False(DiscoveryCodec.CanAutoConnect(DiscoveryCodec.Decode(DiscoveryCodec.Create(honest), identity, 0, 8), false));
        Assert.Equal("DotReticulum", new DiscoveryAdvertisement { TransportIdentity = identity.Hash, Name = "x", ReachableOn = "x.example" }.Implementation);
        Assert.Throws<NotSupportedException>(() => DiscoveryCodec.PackAdvertisement(honest with { InterfaceType = "UnsupportedInterface" }));
    }

    [Theory]
    [InlineData("I2PInterface")]
    [InlineData("RNodeInterface")]
    [InlineData("WeaveInterface")]
    [InlineData("KISSInterface")]
    [InlineData("TCPClientInterface")]
    public async Task BearerPublicationMatchesExecutedReferenceAndMetadataDoesNotTreatRadioAsTcp(string type)
    {
        using var vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "DiscoveryBearerVectors.json")));
        var expected = vectors.RootElement.GetProperty("cases").EnumerateArray().Single(entry => entry.GetProperty("type").GetString() == type);
        using var identity = ReferenceIdentity();
        var config = RnsdConfigParser.Parse($"""
            [reticulum]
            share_instance=no
            [interfaces]
            [[Published]]
            type={type}
            enabled=yes
            discoverable=yes
            {(type == "I2PInterface" ? "connectable=yes" : type == "TCPClientInterface" ? "kiss_framing=yes\ntarget_host=localhost\ntarget_port=4242" : "port=owned-simulator")}
            {(type == "RNodeInterface" ? "frequency=868000000\nbandwidth=125000\nspreadingfactor=7\ncodingrate=5" : "")}
            discovery_name=Reference TCP
            latitude=51.5
            longitude=-0.1
            publish_ifac=yes
            networkname=public
            passphrase=test-only
            discovery_lxmf_address=000102030405060708090a0b0c0d0e0f
            {(type is "KISSInterface" or "TCPClientInterface" ? "discovery_frequency=144800000\ndiscovery_bandwidth=12000" : "")}
            """).Interfaces.Single();
        var advertisement = DiscoveryAdvertisementFactory.Create(config, identity, true, i2pAddress: "reference.b32.i2p") with
        {
            Implementation = "RNS", Version = "1.5.7", StampValue = 1,
        };
        Assert.Equal(Hex(expected, "packed"), DiscoveryCodec.PackAdvertisement(advertisement));
        Assert.Equal(advertisement, await DiscoveryMetadata.ResolveAsync(advertisement));
        var decoded = DiscoveryCodec.Decode(DiscoveryCodec.Create(advertisement), identity, 1, 1);
        Assert.Equal(type == "TCPClientInterface" ? "KISSInterface" : type, decoded.InterfaceType);
        Assert.Null(decoded.Port);
    }

    [Fact]
    public void PublicationRejectsNonconnectableI2pAndUnframedTcpAndDiagnosesPinnedKissModulationDefect()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "DiscoveryBearerVectors.json")));
        Assert.Equal("AttributeError", Assert.Single(vectors.RootElement.GetProperty("failures").EnumerateArray()).GetProperty("error").GetString());
        using var identity = ReferenceIdentity();
        var configuration = new RnsdInterfaceConfig("test", RnsdInterfaceType.I2p, true, DotReticulum.Transport.InterfaceMode.Full,
            new Dictionary<string, string>()) { Discovery = new() { Enabled = true } };
        Assert.Throws<NotSupportedException>(() => DiscoveryAdvertisementFactory.Create(configuration, identity, true));
        Assert.Throws<NotSupportedException>(() => DiscoveryAdvertisementFactory.Create(configuration with { Type = RnsdInterfaceType.TcpClient }, identity, true));
        Assert.Throws<NotSupportedException>(() => DiscoveryCodec.PackAdvertisement(Advertisement(identity) with
        {
            InterfaceType = "KISSInterface", Modulation = 1,
        }));
        Assert.Throws<InvalidOperationException>(() => DiscoveryAdvertisementFactory.Create(configuration with
        {
            Options = new Dictionary<string, string> { ["connectable"] = "yes" },
        }, identity, true));
    }

    [Fact]
    public void RejectsInsufficientMalformedTruncatedAndBadEncryption()
    {
        using var identity = ReferenceIdentity();
        using var wrong = Identity.Generate();
        Assert.Throws<FormatException>(() => DiscoveryCodec.Decode(ReferencePayload(), identity, 0, 13));
        Assert.Throws<FormatException>(() => DiscoveryCodec.Decode(ReferencePayload(true), identity, 0, 8));
        Assert.Throws<FormatException>(() => DiscoveryCodec.Decode(ReferencePayload(true), identity, 0, 8, wrong));
        var broken = ReferencePayload(true);
        broken[^1] ^= 1;
        Assert.Throws<FormatException>(() => DiscoveryCodec.Decode(broken, identity, 0, 8, identity));
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[33], new byte[DiscoveryCodec.MaximumPayload + 1] })
            Assert.Throws<FormatException>(() => DiscoveryCodec.Decode(bytes, identity, 0, 8));
        Assert.Throws<InvalidOperationException>(() => DiscoveryCodec.Create(Advertisement(identity) with { Encrypt = true }));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => DiscoveryCodec.GenerateStamp(new byte[32], 8, cancel.Token));
    }

    [Theory]
    [InlineData(1, 1L)]
    [InlineData(3, 1L)]
    [InlineData(254, "bad")]
    [InlineData(2, "invalid host!")]
    [InlineData(6, -1L)]
    [InlineData(240, "bad")]
    public void RejectsStampedButMalformedMetadata(int key, object replacement)
    {
        using var identity = ReferenceIdentity();
        using var vectors = Vectors();
        var map = Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(Hex(vectors.RootElement, "packed")));
        map[(long)key] = replacement;
        var packed = MsgPack.Pack(map);
        var stamp = DiscoveryCodec.GenerateStamp(SHA256.HashData(packed), 1);
        var payload = new byte[] { 0 }.Concat(packed).Concat(stamp).ToArray();
        Assert.Throws<FormatException>(() => DiscoveryCodec.Decode(payload, identity, 0, 1));
    }

    [Theory]
    [InlineData("1.5.2", true)]
    [InlineData("1.5.2-beta", true)]
    [InlineData("1.5", false)]
    [InlineData("2", true)]
    [InlineData("bad", false)]
    [InlineData("1.4.99", false)]
    public void ReferenceVersionPolicy(string version, bool expected)
    {
        using var identity = ReferenceIdentity();
        var record = DiscoveryCodec.Decode(ReferencePayload(), identity, 0, 8);
        Assert.Equal(expected, DiscoveryCodec.CanAutoConnect(record with { Version = version }, false));
        Assert.False(DiscoveryCodec.CanAutoConnect(record with { Transport = false }, true));
        Assert.False(DiscoveryCodec.CanAutoConnect(record with { InterfaceType = "TCPServerInterface" }, true));
        Assert.False(DiscoveryCodec.CanAutoConnect(record with { ReachableOn = "127.0.0.1" }, true));
        Assert.False(DiscoveryCodec.CanAutoConnect(record with { ReachableOn = "some.onion" }, true));
        Assert.False(DiscoveryCodec.CanAutoConnect(record with { ReachableOn = "0200::1" }, true));
        Assert.False(DiscoveryCodec.CanAutoConnect(record with { Implementation = "DotReticulum" }, false));
        Assert.True(DiscoveryCodec.CanAutoConnect(record with { Implementation = "DotReticulum" }, true));
    }

    [Fact]
    public void ReferenceNameSanitizationAndHostRules()
    {
        using var vectors = Vectors();
        foreach (var property in vectors.RootElement.GetProperty("sanitized").EnumerateObject())
            Assert.Equal(property.Value.GetString(), DiscoveryCodec.SanitizeName(property.Name) ?? "");
        foreach (var host in new[] { "host.example", "127.0.0.1", "::1", "example.", "localhost" }) Assert.True(DiscoveryCodec.IsHost(host));
        foreach (var host in new[] { "", "a..b", "-bad.example", "a.123", "bad host", "a/b", "127.1", "127.00.0.1", "[::1]", "host..", "::ffff:127.1" })
            Assert.False(DiscoveryCodec.IsHost(host));
    }

    [Fact]
    public void SignedAndReservedFlagBitsRemainUpstreamPlaceholders()
    {
        using var identity = ReferenceIdentity();
        var payload = ReferencePayload();
        payload[0] = 0x81;
        Assert.Equal("Reference TCP", DiscoveryCodec.Decode(payload, identity, 0, 8).Name);
        // No signature is invented for FLAG_SIGNED; the surrounding RNS announce authenticates its source.
    }

    [Fact]
    public async Task ClockDrivenPersistenceDuplicatesStatusAndRemoval()
    {
        using var directory = new DirectoryLease();
        using var identity = ReferenceIdentity();
        var clock = new Clock();
        await using (var service = new DiscoveryService(directory.Path, Options(), timeProvider: clock))
        {
            Assert.True(await service.ReceiveAsync(identity, ReferencePayload(), 2));
            clock.Advance(1);
            Assert.True(await service.ReceiveAsync(identity, ReferencePayload(), 3));
            var item = Assert.Single(await service.SnapshotAsync());
            Assert.Equal(1, item.HeardCount);
            Assert.Equal(1, item.LastHeard - item.Discovered);
        }
        await using var restarted = new DiscoveryService(directory.Path, Options(), timeProvider: clock);
        Assert.Equal(1, Assert.Single(await restarted.SnapshotAsync()).HeardCount);
        clock.Advance(86400);
        Assert.Equal("available", Assert.Single(await restarted.SnapshotAsync()).Status);
        clock.Advance(0.001);
        Assert.Equal("unknown", Assert.Single(await restarted.SnapshotAsync()).Status);
        Assert.Empty(await restarted.SnapshotAsync(onlyAvailable: true));
        clock.Advance(2 * 86400);
        Assert.Equal("stale", Assert.Single(await restarted.SnapshotAsync()).Status);
        clock.Advance(4 * 86400);
        Assert.Empty(await restarted.SnapshotAsync());
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task SourcePolicyAppliesBeforeValidationOnCacheHitsAndAfterRestart()
    {
        using var directory = new DirectoryLease();
        using var identity = ReferenceIdentity();
        using var other = Identity.Generate();
        var blocked = new HashSet<string>();
        var options = Options() with { AllowedSources = [identity.Hash] };
        await using (var service = new DiscoveryService(directory.Path, options, isBlocked: h => blocked.Contains(Convert.ToHexStringLower(h.Span))))
        {
            Assert.False(await service.ReceiveAsync(other, ReferencePayload()));
            Assert.True(await service.ReceiveAsync(identity, ReferencePayload()));
            Assert.False(await service.ReceiveAsync(other, ReferencePayload()));
            blocked.Add(identity.HexHash);
            Assert.Empty(await service.SnapshotAsync());
        }
        await using (var service = new DiscoveryService(directory.Path, Options()))
            Assert.True(await service.ReceiveAsync(identity, ReferencePayload()));
        await using var unauthorizedRestart = new DiscoveryService(directory.Path, Options() with { AllowedSources = [other.Hash] });
        Assert.Empty(await unauthorizedRestart.SnapshotAsync());
    }

    [Fact]
    public async Task CapacityEvictsOldestAndCorruptStateIsDiagnosed()
    {
        using var directory = new DirectoryLease();
        using var identity = ReferenceIdentity();
        var clock = new Clock();
        var diagnostics = new List<string>();
        await using (var service = new DiscoveryService(directory.Path, Options(capacity: 1), timeProvider: clock, diagnostic: diagnostics.Add))
        {
            Assert.True(await service.ReceiveAsync(identity, ReferencePayload()));
            clock.Advance(1);
            Assert.True(await service.ReceiveAsync(identity, DiscoveryCodec.Create(Advertisement(identity))));
            Assert.Equal("Test TCP", Assert.Single(await service.SnapshotAsync()).Name);
            Assert.Single(Directory.EnumerateFiles(directory.Path));
        }
        File.WriteAllBytes(Path.Combine(directory.Path, new string('a', 64)), [0xff]);
        await using var restarted = new DiscoveryService(directory.Path, Options(capacity: 2), timeProvider: clock, diagnostic: diagnostics.Add);
        Assert.Single(await restarted.SnapshotAsync());
        Assert.Contains(diagnostics, message => message.Contains("Could not load"));
    }

    [Fact]
    public async Task ConcurrentAttachIsBoundedDeduplicatedAndDetachDisposesOnce()
    {
        using var directory = new DirectoryLease();
        using var identity = ReferenceIdentity();
        var clock = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attachCount = 0;
        var detachCount = 0;
        var online = true;
        var attached = true;
        DiscoveryConnectRequest? request = null;
        await using var service = new DiscoveryService(directory.Path, Options(1), async (r, token) =>
        {
            request = r;
            Interlocked.Increment(ref attachCount);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(r.Name, () => online, () => { detachCount++; return ValueTask.CompletedTask; }, () => attached);
        }, clock, interfaceNames: () => ["Reference TCP"]);
        var first = service.ReceiveAsync(identity, ReferencePayload()).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var extras = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.ReceiveAsync(identity, ReferencePayload()).AsTask()));
        Assert.All(extras, result => Assert.False(result));
        release.TrySetResult();
        Assert.True(await first);
        Assert.Equal("Reference TCP (2)", request!.Name);
        Assert.Equal(5_000_000, request.ConfiguredBitrate);
        Assert.True(await service.ReceiveAsync(identity, ReferencePayload()));
        Assert.Equal(1, attachCount);
        online = false;
        await service.TickAsync();
        clock.Advance(11.999);
        await service.TickAsync();
        Assert.Equal(0, detachCount);
        clock.Advance(0.001);
        await service.TickAsync();
        Assert.Equal(1, detachCount);
        online = true;
        await service.TickAsync();
        Assert.Equal(2, attachCount);
        attached = false;
        await service.TickAsync();
        Assert.Equal(2, detachCount);
        await service.DisposeAsync();
        Assert.Equal(2, detachCount);
    }

    [Fact]
    public async Task FailedConnectObserverAndCancellationAreIsolated()
    {
        using var directory = new DirectoryLease();
        using var identity = ReferenceIdentity();
        var diagnostics = new List<string>();
        var attempts = 0;
        await using var service = new DiscoveryService(directory.Path, Options(1), (r, token) =>
        {
            attempts++;
            throw new IOException("test connect failure");
        }, diagnostic: diagnostics.Add, discovered: _ => throw new InvalidOperationException("observer"));
        Assert.True(await service.ReceiveAsync(identity, ReferencePayload()));
        Assert.True(await service.ReceiveAsync(identity, ReferencePayload()));
        Assert.Equal(2, attempts);
        Assert.Contains(diagnostics, s => s.Contains("connection") && s.Contains("failed"));
        Assert.Contains(diagnostics, s => s.Contains("observer failed"));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReceiveAsync(identity, ReferencePayload(), cancellationToken: cancel.Token).AsTask());
    }

    [Fact]
    public async Task DisposalCancelsPendingConnectAndDoesNotLeakReturnedLease()
    {
        using var directory = new DirectoryLease();
        using var identity = ReferenceIdentity();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detached = 0;
        var service = new DiscoveryService(directory.Path, Options(1), async (r, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { }
            return new(r.Name, () => true, () => { detached++; return ValueTask.CompletedTask; });
        });
        var receive = service.ReceiveAsync(identity, ReferencePayload()).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
        Assert.Equal(1, detached);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task BootstrapTeardownAndRecoveryFollowOnlineCount()
    {
        using var directory = new DirectoryLease();
        using var identity = ReferenceIdentity();
        var clock = new Clock();
        var online = true;
        var bootstrapDetached = 0;
        var bootstrapRestored = 0;
        await using var service = new DiscoveryService(directory.Path, Options(1),
            (r, _) => ValueTask.FromResult<DiscoveryConnection?>(new(r.Name, () => online, () => ValueTask.CompletedTask)),
            clock, bootstrap: _ =>
            {
                bootstrapRestored++;
                return ValueTask.FromResult<IReadOnlyList<DiscoveryConnection>>([new("bootstrap", () => true,
                    () => { bootstrapDetached++; return ValueTask.CompletedTask; })]);
            });
        service.RegisterBootstrap(new("bootstrap", () => true, () => { bootstrapDetached++; return ValueTask.CompletedTask; }));
        Assert.True(await service.ReceiveAsync(identity, ReferencePayload()));
        await service.TickAsync();
        Assert.Equal(1, bootstrapDetached);
        Assert.Equal(0, bootstrapRestored);
        online = false;
        await service.TickAsync();
        Assert.Equal(1, bootstrapRestored);
        clock.Advance(12);
        await service.TickAsync();
        Assert.Equal(1, bootstrapRestored);
    }

    [Fact]
    public async Task AdvertiserSchedulesOneDuePerMinuteEncryptsAndContinuesAfterUnsupportedBearer()
    {
        using var identity = ReferenceIdentity();
        var clock = new Clock();
        var payloads = new List<byte[]>();
        var diagnostics = new List<string>();
        await using var announcer = new DiscoveryAnnouncer((data, _) =>
        {
            payloads.Add(data.ToArray());
            return ValueTask.CompletedTask;
        }, identity, clock, diagnostics.Add);
        announcer.Register("unsupported", TimeSpan.FromMinutes(60), _ =>
            ValueTask.FromResult(Advertisement(identity) with { InterfaceType = "UnsupportedInterface" }));
        announcer.Register("tcp", TimeSpan.FromMinutes(60), _ =>
            ValueTask.FromResult(Advertisement(identity) with { Encrypt = true }));
        await announcer.TickAsync();
        Assert.Empty(payloads);
        Assert.Single(diagnostics);
        clock.Advance(60);
        await announcer.TickAsync();
        var payload = Assert.Single(payloads);
        Assert.Equal(2, payload[0]);
        Assert.Equal("Test TCP", DiscoveryCodec.Decode(payload, identity, 0, 8, identity).Name);
        await announcer.TickAsync();
        Assert.Single(payloads);
        clock.Advance(3600);
        await announcer.TickAsync();
        Assert.Equal(2, diagnostics.Count);
    }

    [Fact]
    public async Task ExistingEndpointAndNonTransportNeverAttach()
    {
        using var directory = new DirectoryLease();
        using var identity = ReferenceIdentity();
        var count = 0;
        await using var service = new DiscoveryService(directory.Path, Options(1), (r, _) =>
        {
            count++;
            return ValueTask.FromResult<DiscoveryConnection?>(null);
        }, endpointExists: _ => true);
        Assert.True(await service.ReceiveAsync(identity, ReferencePayload()));
        Assert.Equal(0, count);
        Assert.True(await service.ReceiveAsync(identity, DiscoveryCodec.Create(Advertisement(identity) with { Transport = false })));
        Assert.Equal(0, count);
        Assert.Single(await service.SnapshotAsync(onlyTransport: true));
    }

    [Fact]
    public async Task BlackholingConnectedSourceDetachesAndRemovesPersistedEntry()
    {
        using var directory = new DirectoryLease();
        using var identity = ReferenceIdentity();
        var blocked = false;
        var detachCount = 0;
        await using var service = new DiscoveryService(directory.Path, Options(1),
            (request, _) => ValueTask.FromResult<DiscoveryConnection?>(new(request.Name, () => true,
                () => { detachCount++; return ValueTask.CompletedTask; })),
            isBlocked: _ => blocked);
        Assert.True(await service.ReceiveAsync(identity, ReferencePayload()));
        blocked = true;
        await service.TickAsync();
        Assert.Equal(1, detachCount);
        Assert.Empty(await service.SnapshotAsync());
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task DisabledDiscoveryNeverReceivesOrAutoconnectsPersistedEntries()
    {
        using var directory = new DirectoryLease();
        using var identity = ReferenceIdentity();
        await using (var enabled = new DiscoveryService(directory.Path, Options()))
            Assert.True(await enabled.ReceiveAsync(identity, ReferencePayload()));
        var connected = 0;
        await using var disabled = new DiscoveryService(directory.Path, Options(1) with { Enabled = false },
            (_, _) => { connected++; return ValueTask.FromResult<DiscoveryConnection?>(null); });
        Assert.False(await disabled.ReceiveAsync(identity, ReferencePayload()));
        await disabled.TickAsync();
        Assert.Equal(0, connected);
        Assert.Single(await disabled.SnapshotAsync());
    }

    [Fact]
    public async Task TypedTcpServerConfigurationDrivesAdvertisementsWithoutInventingNameOrIdentity()
    {
        using var identity = ReferenceIdentity();
        var configuration = RnsdConfigParser.Parse("""
            [reticulum]
              share_instance = no
            [interfaces]
              [[Native listener]]
                type = TCPServerInterface
                enabled = yes
                listen_port = 0
                discoverable = yes
                reachable_on = native.example
                announce_interval = 2
                discovery_stamp_value = 8
                latitude = 51.5
                discovery_lxmf_address = 000102030405060708090a0b0c0d0e0f
            """);
        var iface = Assert.Single(configuration.Interfaces);
        Assert.Equal(TimeSpan.FromMinutes(5), iface.Discovery.AnnounceInterval);
        Assert.Throws<InvalidOperationException>(() => DiscoveryAdvertisementFactory.Create(iface, identity, false));
        var advertisement = DiscoveryAdvertisementFactory.Create(iface, identity, false, 4242);
        Assert.Null(advertisement.Name);
        Assert.Equal(identity.Hash, advertisement.TransportIdentity);
        Assert.Equal("DotReticulum", advertisement.Implementation);
        Assert.Equal(4242, advertisement.Port);
        var payloads = new List<byte[]>();
        await using var announcer = new DiscoveryAnnouncer((payload, _) =>
        {
            payloads.Add(payload.ToArray());
            return ValueTask.CompletedTask;
        });
        DiscoveryAdvertisementFactory.Register(announcer, iface, identity, false, () => 4242);
        await announcer.TickAsync();
        var decoded = DiscoveryCodec.Decode(Assert.Single(payloads), identity, 0, 8);
        Assert.Equal("Discovered TCPServerInterface", decoded.Name);
        Assert.Equal(51.5, decoded.Latitude);
        Assert.Equal(Enumerable.Range(0, 16).Select(n => (byte)n), decoded.OperatorAddress!);
        Assert.True(await announcer.UnregisterAsync(iface.Name));
        Assert.False(await announcer.UnregisterAsync(iface.Name));
        Assert.Throws<NotSupportedException>(() => DiscoveryAdvertisementFactory.Create(iface with { Type = RnsdInterfaceType.Udp }, identity, true, 4242));
    }
}
