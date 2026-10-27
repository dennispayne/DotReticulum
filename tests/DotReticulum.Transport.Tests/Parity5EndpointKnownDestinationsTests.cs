using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

[Trait("Category", "Unit")]
public sealed class Parity5EndpointKnownDestinationsTests
{
    [Fact]
    public void LocalRecallReturnsDetachedPublicIdentityWithoutPersistingOrInventingAppData()
    {
        using var identity = Identity.Generate();
        var hash = Destination.Create(DestinationType.Single, "parity5", ["local"], identity).Hash;
        var known = new KnownDestinations();
        known.RegisterLocal(hash, identity);
        using var recalled = known.Recall(hash);
        Assert.NotNull(recalled);
        Assert.False(recalled.HasPrivateKey);
        Assert.Equal(identity.ExportPublicKey(), recalled.ExportPublicKey());
        Assert.Null(recalled.AppData);
        Assert.Null(known.RecallAppData(hash));
        using var byIdentity = known.Recall(identity.Hash, fromIdentityHash: true);
        Assert.Null(byIdentity); // Pinned upstream searches remote announcements only for identity-hash lookup.
        Assert.Empty(known.ExportSnapshot());
        Assert.Throws<InvalidOperationException>(() => known.RegisterLocal(hash, identity));
        Assert.True(known.UnregisterLocal(hash));
        Assert.Null(known.Recall(hash));
    }

    [Fact]
    public void RemoteEntryTakesPrecedenceAndRecalledAppDataDoesNotAliasLiveEntry()
    {
        using var local = Identity.Generate();
        using var remote = Identity.Generate();
        var known = new KnownDestinations();
        var hash = new byte[16];
        known.RegisterLocal(hash, local);
        known.Remember(hash, new byte[32], remote.ExportPublicKey(), [1, 2]);
        using var recalled = known.Recall(hash, markUsed: false);
        Assert.Equal(remote.ExportPublicKey(), recalled!.ExportPublicKey());
        recalled.AppData![0] = 9;
        Assert.Equal(new byte[] { 1, 2 }, known.RecallAppData(hash));
        Assert.True(known.Forget(hash));
        using var fallback = known.Recall(hash);
        Assert.Equal(local.ExportPublicKey(), fallback!.ExportPublicKey());
        Assert.Null(fallback.AppData);
    }

    [Fact]
    public void LocalRegistryIsBoundedAndIndependentOfRemoteRetentionCapacity()
    {
        using var identity = Identity.Generate();
        var known = new KnownDestinations(capacity: 1);
        var first = new byte[16];
        var second = Enumerable.Repeat((byte)1, 16).ToArray();
        known.RegisterLocal(first, identity);
        Assert.Throws<InvalidOperationException>(() => known.RegisterLocal(second, identity));
        known.Remember(second, new byte[32], identity.ExportPublicKey(), null);
        Assert.True(known.RetainDestination(second));
        Assert.Equal(1, known.Count);
        Assert.True(known.UnregisterLocal(first));
        known.RegisterLocal(second, identity);
    }

    [Fact]
    public void CleanupUsesSnapshotPathCallbacksOutsideCacheLockAndRespectsConcurrentRetention()
    {
        using var identity = Identity.Generate();
        var known = new KnownDestinations();
        var hash = new byte[16];
        known.Remember(hash, new byte[32], identity.ExportPublicKey(), null);
        Assert.Equal(0, known.Cleanup(destination =>
        {
            Assert.True(known.RetainDestination(destination));
            return false;
        }));
        Assert.True(known.Get(hash)!.IsRetained);
    }

    [Fact]
    public void LocalRegistrationDoesNotRetainStaleRemoteAnnouncementData()
    {
        using var local = Identity.Generate();
        using var remote = Identity.Generate();
        var known = new KnownDestinations();
        var hash = new byte[16];
        known.RegisterLocal(hash, local);
        known.Remember(hash, new byte[32], remote.ExportPublicKey(), [9]);
        // Identity.py:328-330 uses only explicit retention and paths, not local registration.
        // Its zero-use timestamp fallback also expires unused pathless entries immediately at current Unix time.
        Assert.Equal(1, known.Cleanup());
        Assert.Null(known.RecallAppData(hash));
        using var recalled = known.Recall(hash);
        Assert.NotNull(recalled);
        Assert.Equal(local.ExportPublicKey(), recalled.ExportPublicKey());
        Assert.Null(recalled.AppData);
    }

    [Fact]
    public async Task ConcurrentLocalUnregistrationAndRecallNeverExposePrivateKeysOrDisposedOwner()
    {
        using var identity = Identity.Generate();
        var known = new KnownDestinations();
        var hash = new byte[16];
        await Task.WhenAll(Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
            {
                known.RegisterLocal(hash, identity);
                known.UnregisterLocal(hash);
            }
        }), Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
            {
                using var recalled = known.Recall(hash);
                if (recalled is not null)
                {
                    Assert.False(recalled.HasPrivateKey);
                    Assert.Equal(identity.ExportPublicKey(), recalled.ExportPublicKey());
                }
            }
        }));
    }
}
