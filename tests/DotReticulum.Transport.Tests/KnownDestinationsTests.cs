using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

[Trait("Category", "Unit")]
public sealed class KnownDestinationsTests
{
    private const long Now = 1_800_000_000;
    private static readonly byte[] Hash = new byte[16];

    [Fact]
    public void UsageRetentionAndReannounceFollowReferenceSentinel()
    {
        var time = new FakeTime(Now);
        var known = new KnownDestinations(timeProvider: time);
        using var identity = Identity.Generate();
        known.Remember(Hash, new byte[32], identity.ExportPublicKey(), [1]);
        Assert.Equal(0, known.Get(Hash)!.LastUsedUnix);
        using var recalled = known.Recall(Hash);
        Assert.Equal(Now, known.Get(Hash)!.LastUsedUnix);
        Assert.True(known.RetainDestination(Hash));
        Assert.False(known.UseDestination(Hash));
        time.Advance(10);
        known.Remember(Hash, new byte[32], identity.ExportPublicKey(), [2]);
        Assert.True(known.Get(Hash)!.IsRetained);
        Assert.Equal(new byte[] { 2 }, known.RecallAppData(Hash));
        Assert.True(known.Get(Hash)!.IsRetained);
        Assert.True(known.UnretainDestination(Hash));
        Assert.Equal(Now + 10, known.Get(Hash)!.LastUsedUnix);
        Assert.False(known.RetainDestination(new byte[15]));
        Assert.Throws<ArgumentException>(() => known.RetainIdentity(new byte[15]));
    }

    [Fact]
    public void RecallByIdentityRetainsAllMatchingDestinationsAndMarksUse()
    {
        var known = new KnownDestinations(timeProvider: new FakeTime(Now));
        using var identity = Identity.Generate();
        var other = Enumerable.Repeat((byte)1, 16).ToArray();
        known.Remember(Hash, new byte[32], identity.ExportPublicKey(), null);
        known.Remember(other, new byte[32], identity.ExportPublicKey(), null);
        using var recalled = known.Recall(identity.Hash, fromIdentityHash: true);
        Assert.NotNull(recalled);
        Assert.True(known.RetainIdentity(identity.Hash));
        Assert.All(known.ExportSnapshot(), entry => Assert.True(entry.Entry.IsRetained));
        Assert.False(known.RetainIdentity(new byte[16]));
    }

    [Fact]
    public void RetentionAndExistingPathsProtectDataWhileUseExpiresAtReferenceBoundary()
    {
        var time = new FakeTime(Now);
        var known = new KnownDestinations(timeProvider: time);
        using var identity = Identity.Generate();
        known.Remember(Hash, new byte[32], identity.ExportPublicKey(), null);
        known.UseDestination(Hash);
        time.Advance((long)KnownDestinations.UsedDestinationExpiry.TotalSeconds);
        Assert.Equal(0, known.Cleanup());
        time.Advance(1);
        Assert.Equal(0, known.Cleanup(_ => true));
        Assert.True(known.RetainDestination(Hash));
        Assert.Equal(0, known.Cleanup());
        known.UnretainDestination(Hash);
        time.Advance((long)KnownDestinations.UsedDestinationExpiry.TotalSeconds + 1);
        Assert.Equal(1, known.Cleanup());
        Assert.Null(known.Get(Hash));
    }

    [Fact]
    public void NeverUsedPathlessDataFollowsPinnedReferenceIncludingZeroTimestampFallback()
    {
        var known = new KnownDestinations(timeProvider: new FakeTime(Now));
        using var identity = Identity.Generate();
        known.Remember(Hash, new byte[32], identity.ExportPublicKey(), null);
        // Identity.py:328 also compares now-0 with DESTINATION_TIMEOUT*1.25 in its elif.
        Assert.Equal(1, known.Cleanup());
    }

    [Fact]
    public void SnapshotAndGetDoNotAliasCallerOrLiveArraysAndSameRatchetDoesNotRefreshAge()
    {
        var time = new FakeTime(Now);
        var known = new KnownDestinations(timeProvider: time);
        using var identity = Identity.Generate();
        var publicKey = identity.ExportPublicKey();
        byte[] appData = [3];
        var ratchet = new byte[32];
        known.Remember(Hash, new byte[32], publicKey, appData, ratchet);
        publicKey[0] ^= 1; appData[0] = 9; ratchet[0] = 9;
        Assert.Equal(new byte[] { 3 }, known.Get(Hash)!.AppData);
        var snapshot = known.ExportSnapshot();
        snapshot[0].Entry.PublicKey[0] ^= 1;
        Assert.Equal(identity.ExportPublicKey(), known.GetPublicKey(Hash));
        time.Advance(100);
        known.Remember(Hash, new byte[32], identity.ExportPublicKey(), null, new byte[32]);
        Assert.Equal(Now, known.Get(Hash)!.RatchetReceivedUnix);
        time.Advance((long)KnownDestinations.RatchetExpiry.TotalSeconds - 100);
        Assert.Null(known.GetRatchet(Hash));
    }

    [Fact]
    public void RestoreIsAtomicValidatesInputsAndDropsExpiredPublicRatchets()
    {
        var time = new FakeTime(Now);
        var known = new KnownDestinations(timeProvider: time);
        using var identity = Identity.Generate();
        known.Remember(Hash, new byte[32], identity.ExportPublicKey(), [1], new byte[32]);
        known.RetainDestination(Hash);
        var snapshot = known.ExportSnapshot();
        Assert.Throws<InvalidDataException>(() => known.RestoreSnapshot([snapshot[0], snapshot[0]]));
        Assert.Single(known.ExportSnapshot());
        time.Advance((long)KnownDestinations.RatchetExpiry.TotalSeconds + 1);
        var restarted = new KnownDestinations(timeProvider: time);
        restarted.RestoreSnapshot(snapshot);
        Assert.True(restarted.Get(Hash)!.IsRetained);
        Assert.Null(restarted.GetRatchet(Hash));
    }

    [Fact]
    public void CapacityNeverEvictsExplicitlyRetainedData()
    {
        using var identity = Identity.Generate();
        var known = new KnownDestinations(capacity: 1);
        known.Remember(Hash, new byte[32], identity.ExportPublicKey(), null);
        known.RetainDestination(Hash);
        Assert.Throws<InvalidOperationException>(() => known.Remember(Enumerable.Repeat((byte)1, 16).ToArray(), new byte[32], identity.ExportPublicKey(), null));
        Assert.True(known.Get(Hash)!.IsRetained);
        Assert.Equal(1, known.Count);
    }

    [Fact]
    public async Task ConcurrentRememberRecallSnapshotAndRetentionStayBounded()
    {
        using var identity = Identity.Generate();
        var known = new KnownDestinations(capacity: 32);
        var publicKey = identity.ExportPublicKey();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 200; index++)
            {
                var hash = new byte[16]; hash[0] = (byte)(index % 16);
                known.Remember(hash, new byte[32], publicKey, [(byte)worker]);
                using var recalled = known.Recall(hash);
                Assert.NotNull(recalled);
                Assert.All(known.ExportSnapshot(), entry => Assert.Equal(publicKey, entry.Entry.PublicKey));
            }
        })));
        Assert.Equal(16, known.Count);
    }
}
