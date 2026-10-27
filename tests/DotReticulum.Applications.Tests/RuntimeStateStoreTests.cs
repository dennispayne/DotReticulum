using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public sealed class WindowsPersistenceFactAttribute : FactAttribute
{
    public WindowsPersistenceFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Win32 delete-pending/sharing retry is specific to Windows; Unix permits renaming open files.";
    }
}

// Fixtures EXECUTE unmodified hosted upstream a29bfb51c468945c96a1ee22cae0114e993e2391 (RNS 1.5.7).
// GenerateRuntimeStateVectors.py records imported path and Identity/Destination/Reticulum/Transport SHA-256.
[Trait("Category", "Unit")]
public sealed class RuntimeStateStoreTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine("TestResults", "runtime-state", Guid.NewGuid().ToString("N")));
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-state-vectors.json"))).RootElement;
    private static byte[] Vector(string name) => Convert.FromHexString(Vectors.GetProperty(name).GetString()!);
    private static readonly byte[] DestinationHash = Vector("destination_hash");
    private sealed class Clock(double unix) : TimeProvider
    {
        public double Unix { get; set; } = unix;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds((long)(Unix * 1000));
    }

    public static IEnumerable<object[]> KnownStages() =>
        Vectors.GetProperty("known_destinations").EnumerateArray().Select(v => new object[] { v.GetProperty("label").GetString()! });

    [Theory]
    [MemberData(nameof(KnownStages))]
    public void KnownDestinationSerializationExactlyMatchesExecutedReference(string label)
    {
        var bytes = Convert.FromHexString(Vectors.GetProperty("known_destinations").EnumerateArray()
            .Single(v => v.GetProperty("label").GetString() == label).GetProperty("hex").GetString()!);
        var entries = PersistenceFormats.DecodeKnownDestinations(bytes);
        var entry = Assert.Single(entries);
        Assert.Equal(DestinationHash, entry.DestinationHash);
        Assert.Equal(Vector("identity_public"), entry.Entry.PublicKey);
        Assert.Equal(Vector("packet_hash"), entry.Entry.PacketHash);
        Assert.Equal(bytes, PersistenceFormats.EncodeKnownDestinations(entries));
    }

    [Fact]
    public void PathAndTunnelSerializationExactlyMatchExecutedReference()
    {
        var paths = PersistenceFormats.DecodePaths(Vector("paths"));
        var path = Assert.Single(paths);
        Assert.Equal(DestinationHash, path.DestinationHash);
        Assert.Equal(2, path.Hops);
        Assert.Equal(1_800_000_010.625, path.Timestamp);
        Assert.Equal(Vector("paths"), PersistenceFormats.EncodePaths(paths));
        var tunnels = PersistenceFormats.DecodeTunnels(Vector("tunnels"));
        Assert.Single(tunnels);
        Assert.Equal(Vector("tunnels"), PersistenceFormats.EncodeTunnels(tunnels));
    }

    [Fact]
    public void SharedClientZeroHopPathCanBePersisted()
    {
        // Transport.py:1937 removes the shared-instance hop; zero is valid for local-client paths.
        var path = PersistenceFormats.DecodePaths(Vector("paths"))[0] with { Hops = 0 };
        var restored = Assert.Single(PersistenceFormats.DecodePaths(PersistenceFormats.EncodePaths([path])));
        Assert.Equal(0, restored.Hops);
        Assert.Equal(path.DestinationHash, restored.DestinationHash);
        Assert.Equal(path.PacketHash, restored.PacketHash);
    }

    [Fact]
    public void LocalRatchetEnvelopeExactlyMatchesExecutedSignedReference()
    {
        using var identity = Identity.FromPrivateKey(Vector("identity_private"));
        using var ring = DestinationRatchets.FromPrivateKeys([Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
            Enumerable.Range(32, 32).Select(i => (byte)i).ToArray()]);
        using var snapshot = ring.ExportSnapshot();
        var packed = PersistenceFormats.EncodeUpstreamRatchets(snapshot, identity);
        try { Assert.Equal(Vector("local_ratchets"), packed); }
        finally { CryptographicOperations.ZeroMemory(packed); }
    }

    [Fact]
    public void KnownCacheOperationsAndCleanupMatchObservedReference()
    {
        var clock = new Clock(1_800_000_000.125);
        var known = new KnownDestinations(timeProvider: clock);
        known.Remember(DestinationHash, Vector("packet_hash"), Vector("identity_public"), "fixture"u8.ToArray());
        Assert.Equal(Vectors.GetProperty("use_result").GetBoolean(), known.UseDestination(DestinationHash));
        Assert.Equal(Vectors.GetProperty("retain_result").GetBoolean(), known.RetainDestination(DestinationHash));
        Assert.Equal(Vectors.GetProperty("retained_use_result").GetBoolean(), known.UseDestination(DestinationHash));
        clock.Unix += 10.5;
        known.Remember(DestinationHash, Vector("packet_hash"), Vector("identity_public"), "again"u8.ToArray());
        var retainedBytes = Convert.FromHexString(Vectors.GetProperty("known_destinations").EnumerateArray()
            .Single(v => v.GetProperty("label").GetString() == "retained_reannounce").GetProperty("hex").GetString()!);
        Assert.Equal(retainedBytes, PersistenceFormats.EncodeKnownDestinations(known.ExportSnapshot()));
        var fresh = new KnownDestinations(timeProvider: clock);
        fresh.Remember(DestinationHash, Vector("packet_hash"), Vector("identity_public"), null);
        fresh.Cleanup();
        Assert.Equal(Vectors.GetProperty("fresh_never_used_survives").GetBoolean(), fresh.Get(DestinationHash) is not null);
    }

    [Fact]
    public async Task RuntimeRestartPreservesExactPublicStateAndExpiresPathsAndRatchets()
    {
        var clock = new Clock(1_800_000_020);
        var store = new RuntimeStateStore(_directory, clock);
        using var identity = Identity.FromPrivateKey(Vector("identity_private"));
        var known = new KnownDestinations(timeProvider: clock);
        var publicRatchet = new byte[32]; publicRatchet[0] = 1;
        known.Remember(DestinationHash, Vector("packet_hash"), identity.ExportPublicKey(), "state"u8.ToArray(), publicRatchet);
        known.RetainDestination(DestinationHash);
        var destination = Destination.Create(DestinationType.Single, "persist", ["test"], identity);
        var announce = Announce.Create(identity, destination, unixSeconds: 1_800_000_020);
        var path = PersistenceFormats.DecodePaths(Vector("paths"))[0] with { PacketHash = announce.CalculateHash() };
        var state = new RuntimeStateSnapshot(known.ExportSnapshot(), [path], PersistenceFormats.DecodeTunnels(Vector("tunnels")),
            [announce.CalculateHash()], [new(announce.CalculateHash(), announce.ToArray(), true)]);
        await store.SaveAsync(state);
        var restarted = new RuntimeStateStore(_directory, clock);
        var loaded = await restarted.LoadAsync();
        Assert.True(Assert.Single(loaded.KnownDestinations).Entry.IsRetained);
        Assert.Equal(publicRatchet, loaded.KnownDestinations[0].Entry.Ratchet);
        Assert.Equal(path.InterfaceHash, Assert.Single(loaded.Paths).InterfaceHash);
        Assert.Equal(announce.ToArray(), Assert.Single(loaded.CachedPackets).Raw);
        Assert.Single(loaded.Tunnels);
        clock.Unix += KnownDestinations.RatchetExpiry.TotalSeconds;
        loaded = await restarted.LoadAsync();
        Assert.Null(Assert.Single(loaded.KnownDestinations).Entry.Ratchet);
        Assert.Empty(loaded.Paths);
        Assert.Empty(loaded.Tunnels);
    }

    [Fact]
    public async Task MissingStateIsEmptyButCorruptionIsExplicitAndNotOverwritten()
    {
        var store = new RuntimeStateStore(_directory);
        Assert.Empty((await store.LoadAsync()).KnownDestinations);
        await store.SaveAsync(RuntimeStateSnapshot.Empty);
        var bytes = await File.ReadAllBytesAsync(store.StatePath);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(store.StatePath, bytes);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Contains(store.StatePath, exception.Message);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.StatePath));
    }

    [Fact]
    public async Task OptionalPacketCacheTimestampSurvivesRestartWithoutChangingLegacyRows()
    {
        var clock = new Clock(1_800_000_020);
        var store = new RuntimeStateStore(_directory, clock);
        var first = Packet.Create(PacketType.Proof, DestinationType.Link, new byte[16], new byte[] { 1 });
        var legacy = Packet.Create(PacketType.Proof, DestinationType.Link, new byte[16], new byte[] { 2 });
        var state = RuntimeStateSnapshot.Empty with
        {
            CachedPackets =
            [
                new PersistedPacket(first.CalculateHash(), first.ToArray(), false, 1_800_000_000.125),
                new PersistedPacket(legacy.CalculateHash(), legacy.ToArray(), false),
            ],
        };
        await store.SaveAsync(state);
        var restored = await new RuntimeStateStore(_directory, clock).LoadAsync();
        Assert.Equal(1_800_000_000.125, restored.CachedPackets[0].CachedAt);
        Assert.Null(restored.CachedPackets[1].CachedAt);
        var committed = await File.ReadAllBytesAsync(store.StatePath);
        var invalid = state with { CachedPackets = [state.CachedPackets[0] with { CachedAt = double.NaN }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(invalid));
        Assert.Equal(committed, await File.ReadAllBytesAsync(store.StatePath));
    }

    [Fact]
    public async Task CancelledSavePreservesCommittedGenerationAndCleansOwnedStaging()
    {
        var store = new RuntimeStateStore(_directory);
        await store.SaveAsync(RuntimeStateSnapshot.Empty);
        var bytes = await File.ReadAllBytesAsync(store.StatePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(RuntimeStateSnapshot.Empty, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.StatePath));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.out.*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConcurrentWritersPublishWholeGenerationsAcrossStoreInstances()
    {
        var stores = Enumerable.Range(0, 3).Select(_ => new RuntimeStateStore(_directory)).ToArray();
        await stores[0].SaveAsync(RuntimeStateSnapshot.Empty);
        await Task.WhenAll(stores.Select(store => Task.Run(async () =>
        {
            for (var index = 0; index < 10; index++)
            {
                await store.SaveAsync(RuntimeStateSnapshot.Empty);
                Assert.Empty((await store.LoadAsync()).PacketHashes);
            }
        })));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.out.*"));
    }

    [WindowsPersistenceFact]
    public async Task CancellationDuringWindowsSharingRetryLeavesCommittedStateIntact()
    {
        var store = new RuntimeStateStore(_directory);
        await store.SaveAsync(RuntimeStateSnapshot.Empty);
        var previous = await File.ReadAllBytesAsync(store.StatePath);
        using (var held = new FileStream(store.StatePath, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(RuntimeStateSnapshot.Empty, cancellation.Token));
        Assert.Equal(previous, await File.ReadAllBytesAsync(store.StatePath));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.out.*"));
    }

    [Fact]
    public async Task IdentityCreationRacesAdoptOneStableCompleteIdentity()
    {
        var path = Path.Combine(_directory, "identity");
        var identities = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => RuntimeStateStore.LoadOrCreateIdentity(path))));
        try
        {
            Assert.All(identities, identity => Assert.Equal(identities[0].Hash, identity.Hash));
            using var reopened = RuntimeStateStore.LoadOrCreateIdentity(path);
            Assert.Equal(identities[0].Hash, reopened.Hash);
            Assert.Equal(64, new FileInfo(path).Length);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Empty(Directory.EnumerateFiles(_directory, "*.out.*"));
        }
        finally { foreach (var identity in identities) identity.Dispose(); }
    }

    [Fact]
    public void CorruptIdentityIsNeverRegenerated()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "identity");
        File.WriteAllBytes(path, [1, 2, 3]);
        var exception = Assert.Throws<InvalidDataException>(() => RuntimeStateStore.LoadOrCreateIdentity(path));
        Assert.Contains(path, exception.Message);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task SignedSecretRatchetsSurviveRestartWithOldKeysAndRotationPolicy()
    {
        using var identity = Identity.Generate();
        using var ring = new DestinationRatchets { Retained = 3, Interval = TimeSpan.FromSeconds(100), Enforce = true };
        var publicKey = ring.Rotate(1_800_000_000);
        var oldCiphertext = identity.Encrypt("still decryptable"u8, publicKey);
        ring.Rotate(1_800_000_101);
        var store = new RuntimeStateStore(_directory);
        Assert.Null(await store.LoadRatchetsAsync(DestinationHash, identity));
        await store.SaveRatchetsAsync(DestinationHash, identity, ring);
        ring.Dispose();
        using var loaded = await new RuntimeStateStore(_directory).LoadRatchetsAsync(DestinationHash, identity);
        Assert.NotNull(loaded);
        Assert.True(loaded.Enforce);
        Assert.Equal(3, loaded.Retained);
        Assert.Equal(1_800_000_101, loaded.LatestRotationUnix);
        Assert.Equal(TimeSpan.FromSeconds(100), loaded.Interval);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("still decryptable"u8.ToArray(), loaded.Decrypt(identity, oldCiphertext));
        Assert.Equal(loaded.CurrentPublicKey, loaded.Rotate(1_800_000_201));
        var path = Directory.GetFiles(Path.Combine(_directory, "destination-ratchets")).Single();
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task OptInPersistentRingSavesAutomaticAnnounceRotationsAndReopensOldKeys()
    {
        using var identity = Identity.FromPrivateKey(Vector("identity_private"));
        var destination = Destination.Create(DestinationType.Single, "persist", ["test"], identity);
        var store = new RuntimeStateStore(_directory);
        using var ring = await store.OpenPersistentRatchetsAsync(destination.Hash, identity);
        Assert.True(ring.HasPersistence);
        Assert.Equal(0, ring.Count);
        ring.Retained = 2;
        ring.Interval = TimeSpan.FromSeconds(10);
        ring.Enforce = true;
        var first = Announce.Create(identity, destination, ratchets: ring, unixSeconds: 1_800_000_000);
        var firstRatchet = Announce.Validate(first).Data!.Ratchet;
        using (var persisted = await store.LoadRatchetsAsync(destination.Hash, identity))
            Assert.Equal(firstRatchet, persisted!.CurrentPublicKey);
        var oldCiphertext = identity.Encrypt("before automatic rotation"u8, firstRatchet);
        var second = Announce.Create(identity, destination, ratchets: ring, unixSeconds: 1_800_000_011);
        var secondRatchet = Announce.Validate(second).Data!.Ratchet;
        Assert.NotEqual(firstRatchet, secondRatchet);
        ring.Dispose();
        using var reopened = await new RuntimeStateStore(_directory).OpenPersistentRatchetsAsync(destination.Hash, identity);
        Assert.True(reopened.HasPersistence);
        Assert.Equal(secondRatchet, reopened.CurrentPublicKey);
        Assert.Equal(2, reopened.Count);
        Assert.True(reopened.Enforce);
        Assert.Equal("before automatic rotation"u8.ToArray(), reopened.Decrypt(identity, oldCiphertext));
        Assert.Equal(1_800_000_011, reopened.LatestRotationUnix);
        Assert.Equal(secondRatchet, reopened.Rotate(1_800_000_021));
    }

    [Fact]
    public async Task FailedAutomaticRatchetSavePreventsAnnouncementAndPreservesPreviousKey()
    {
        using var identity = Identity.FromPrivateKey(Vector("identity_private"));
        var destination = Destination.Create(DestinationType.Single, "persist", ["test"], identity);
        var store = new RuntimeStateStore(_directory);
        using var ring = await store.OpenPersistentRatchetsAsync(destination.Hash, identity);
        ring.Retained = 1;
        ring.Interval = TimeSpan.Zero;
        var original = ring.Rotate(100);
        var path = Directory.GetFiles(Path.Combine(_directory, "destination-ratchets")).Single();
        var savedPath = path + ".saved";
        File.Move(path, savedPath);
        Directory.CreateDirectory(path);
        try
        {
            var exception = Assert.Throws<IOException>(() => Announce.Create(identity, destination, ratchets: ring, unixSeconds: 101));
            Assert.Contains(path, exception.Message);
            Assert.Equal(original, ring.CurrentPublicKey);
            Assert.Equal(100, ring.LatestRotationUnix);
            Assert.Equal(1, ring.Count);
            Assert.Empty(Directory.EnumerateFiles(_directory, "*.out.*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(path);
            File.Move(savedPath, path);
        }
        using var persisted = await store.LoadRatchetsAsync(destination.Hash, identity);
        Assert.Equal(original, persisted!.CurrentPublicKey);
        Assert.Equal(100, persisted.LatestRotationUnix);
    }

    [Fact]
    public async Task PersistentRingOwnsSignerCloneAndOpeningCancellationDoesNotDisableFutureRotation()
    {
        using var identity = Identity.FromPrivateKey(Vector("identity_private"));
        var store = new RuntimeStateStore(_directory);
        using var cancellation = new CancellationTokenSource();
        using var ring = await store.OpenPersistentRatchetsAsync(DestinationHash, identity, cancellation.Token);
        identity.Dispose();
        cancellation.Cancel();
        var publicKey = ring.Rotate(100);
        using var verifier = Identity.FromPrivateKey(Vector("identity_private"));
        using var reopened = await store.LoadRatchetsAsync(DestinationHash, verifier);
        Assert.Equal(publicKey, reopened!.CurrentPublicKey);
        ring.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ring.Rotate(200));
        Assert.True(verifier.Verify(verifier.Sign("caller still owns its identity"u8), "caller still owns its identity"u8));
        using var another = await store.OpenPersistentRatchetsAsync(DestinationHash, verifier);
        another.Dispose();
        Assert.True(verifier.Verify(verifier.Sign("still owned"u8), "still owned"u8));
    }

    [Fact]
    public async Task CancelledPersistentOpenDoesNotCreateStateOrBindPublicOnlyIdentity()
    {
        using var identity = Identity.Generate();
        var store = new RuntimeStateStore(_directory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenPersistentRatchetsAsync(DestinationHash, identity, cancellation.Token));
        using var publicOnly = Identity.FromPublicKey(identity.ExportPublicKey());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenPersistentRatchetsAsync(DestinationHash, publicOnly));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task CorruptWrongIdentityAndSwappedDestinationRatchetsFailContextually()
    {
        using var identity = Identity.Generate();
        using var other = Identity.Generate();
        using var ring = new DestinationRatchets();
        ring.Rotate(100);
        var store = new RuntimeStateStore(_directory);
        await store.SaveRatchetsAsync(DestinationHash, identity, ring);
        var path = Directory.GetFiles(Path.Combine(_directory, "destination-ratchets")).Single();
        var bytes = await File.ReadAllBytesAsync(path);
        try
        {
            var wrongIdentity = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadRatchetsAsync(DestinationHash, other));
            Assert.Contains(path, wrongIdentity.Message);
            var otherHash = DestinationHash.ToArray(); otherHash[0] ^= 1;
            var otherPath = Path.Combine(Path.GetDirectoryName(path)!, Convert.ToHexStringLower(otherHash) + ".v1.msgpack");
            await File.WriteAllBytesAsync(otherPath, bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadRatchetsAsync(otherHash, identity));
            bytes[^1] ^= 1;
            await File.WriteAllBytesAsync(path, bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadRatchetsAsync(DestinationHash, identity));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Fact]
    public async Task LargeRetainedSecretRingIsNotTruncatedToDefaultOnLoad()
    {
        using var identity = Identity.Generate();
        using var ring = DestinationRatchets.FromPrivateKeys(Enumerable.Range(0, 513).Select(_ => new byte[32]), 100, retained: 513);
        var store = new RuntimeStateStore(_directory);
        await store.SaveRatchetsAsync(DestinationHash, identity, ring);
        using var loaded = await store.LoadRatchetsAsync(DestinationHash, identity);
        Assert.Equal(513, loaded!.Count);
    }

    [Fact]
    public async Task OversizedFilesAreRejectedBeforeAllocatingTheirContents()
    {
        Directory.CreateDirectory(_directory);
        var store = new RuntimeStateStore(_directory);
        using (var file = File.Create(store.StatePath)) file.SetLength(RuntimeStateStore.MaximumStateBytes + 1L);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Contains(store.StatePath, exception.Message);
    }

    [Fact]
    public async Task InvalidCachedWireHashIsRejectedBeforeReplacingCommittedState()
    {
        var store = new RuntimeStateStore(_directory);
        await store.SaveAsync(RuntimeStateSnapshot.Empty);
        var previous = await File.ReadAllBytesAsync(store.StatePath);
        using var identity = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "test", ["persist"], identity);
        var announce = Announce.Create(identity, destination);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(RuntimeStateSnapshot.Empty with
        {
            CachedPackets = [new(new byte[32], announce.ToArray(), true)],
        }));
        Assert.Equal(previous, await File.ReadAllBytesAsync(store.StatePath));
    }

    [Fact]
    public void BinaryDestinationKeysAndMalformedPersistenceAreStrictlyValidated()
    {
        var known = new Dictionary<object, object?>
        {
            [Convert.ToHexString(DestinationHash)] = new object?[] { 1d, new byte[32], Vector("identity_public"), null, 0L },
        };
        Assert.Throws<InvalidDataException>(() => PersistenceFormats.DecodeKnownDestinations(MsgPack.Pack(known)));
        Assert.ThrowsAny<Exception>(() => PersistenceFormats.DecodeKnownDestinations([0xdf, 0xff, 0xff, 0xff, 0xff]));
        Assert.Throws<InvalidDataException>(() => PersistenceFormats.DecodePaths(MsgPack.Pack(new object?[] { new object?[] { 1L } })));
        Assert.Throws<InvalidDataException>(() => PersistenceFormats.DecodeTunnels(MsgPack.Pack(new object?[] { new object?[] { 1L } })));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
