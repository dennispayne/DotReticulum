using System.Security.Cryptography;

namespace DotReticulum.Core.Tests;

[Trait("Category", "Unit")]
public sealed class DestinationRatchetsTests
{
    private sealed class Lifetime : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void SnapshotOwnsKeysAndAtomicallyPreservesPolicyAcrossRestart()
    {
        using var ratchets = new DestinationRatchets { Enforce = true, Retained = 3, Interval = TimeSpan.FromSeconds(10) };
        var first = ratchets.Rotate(100);
        using var snapshot = ratchets.ExportSnapshot();
        using var restarted = DestinationRatchets.FromSnapshot(snapshot);
        Assert.True(restarted.Enforce);
        Assert.Equal(3, restarted.Retained);
        Assert.Equal(TimeSpan.FromSeconds(10), restarted.Interval);
        Assert.Equal(100, restarted.LatestRotationUnix);
        Assert.Equal(first, restarted.Rotate(110));
        using var clone = snapshot.Clone();
        snapshot.Dispose();
        Assert.All(snapshot.PrivateKeys, key => Assert.Equal(new byte[32], key));
        Assert.NotEqual(new byte[32], clone.PrivateKeys[0]);
        Assert.Equal(first, restarted.CurrentPublicKey);
        Assert.Throws<ObjectDisposedException>(() => snapshot.Clone());
    }

    [Fact]
    public void DisposeIsTerminalAndDoesNotResurrectKeys()
    {
        var ratchets = new DestinationRatchets();
        ratchets.Rotate(100);
        ratchets.Dispose();
        ratchets.Dispose();
        Assert.Equal(0, ratchets.Count);
        Assert.Throws<ObjectDisposedException>(() => ratchets.Rotate(200));
        Assert.Throws<ObjectDisposedException>(() => ratchets.ExportSnapshot());
        Assert.Throws<ObjectDisposedException>(() => ratchets.ExportPrivateKeys());
        Assert.Throws<ObjectDisposedException>(() => ratchets.Retained = 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DestinationRatchets { Interval = TimeSpan.FromSeconds(-1) });
    }

    [Fact]
    public async Task SnapshotsRemainUsableWhileLiveRingRotatesAndTrims()
    {
        using var identity = Identity.Generate();
        using var ratchets = new DestinationRatchets { Retained = 1, Interval = TimeSpan.Zero, Enforce = true };
        ratchets.Rotate(1);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
            {
                using var snapshot = ratchets.ExportSnapshot();
                using var independent = DestinationRatchets.FromSnapshot(snapshot);
                var ciphertext = identity.Encrypt("owned"u8, independent.CurrentPublicKey);
                ratchets.Rotate(2 + index + worker * 100);
                Assert.Equal("owned"u8.ToArray(), independent.Decrypt(identity, ciphertext));
            }
        })));
        Assert.Equal(1, ratchets.Count);
    }

    [Fact]
    public void ImportPreservesExplicitRetentionAboveDefaultAndClonesSecrets()
    {
        var key = Enumerable.Repeat((byte)7, 32).ToArray();
        using var ratchets = DestinationRatchets.FromPrivateKeys(Enumerable.Repeat(key, 513), 100, retained: 513);
        CryptographicOperations.ZeroMemory(key);
        Assert.Equal(513, ratchets.Count);
        using var snapshot = ratchets.ExportSnapshot();
        Assert.All(snapshot.PrivateKeys, privateKey => Assert.All(privateKey, value => Assert.Equal(7, value)));
        Assert.Throws<ArgumentException>(() => DestinationRatchets.FromPrivateKeys([new byte[31]]));
    }

    [Fact]
    public void MandatoryPersistenceSavesBeforeRotationCommitsAndRollsBackOnFailure()
    {
        using var ring = new DestinationRatchets { Retained = 1, Interval = TimeSpan.Zero };
        var original = ring.Rotate(100);
        var fail = false;
        DestinationRatchetSnapshot? borrowed = null;
        var writes = 0;
        ring.EnablePersistence(snapshot =>
        {
            borrowed = snapshot;
            writes++;
            Assert.Equal(original, ring.CurrentPublicKey);
            if (fail) throw new IOException("Injected mandatory persistence failure.");
        });
        Assert.True(ring.HasPersistence);
        Assert.All(borrowed!.PrivateKeys, key => Assert.Equal(new byte[32], key));
        fail = true;
        Assert.Throws<IOException>(() => ring.Rotate(101));
        Assert.Equal(original, ring.CurrentPublicKey);
        Assert.Equal(100, ring.LatestRotationUnix);
        Assert.Equal(1, ring.Count);
        Assert.All(borrowed.PrivateKeys, key => Assert.Equal(new byte[32], key));
        fail = false;
        Assert.NotEqual(original, ring.Rotate(102));
        Assert.Equal(102, ring.LatestRotationUnix);
        Assert.Equal(3, writes);
        Assert.All(borrowed.PrivateKeys, key => Assert.Equal(new byte[32], key));
    }

    [Fact]
    public void FailedMandatoryPolicySaveDoesNotTrimKeysOrChangePolicy()
    {
        using var ring = new DestinationRatchets { Retained = 2, Interval = TimeSpan.FromSeconds(10) };
        ring.Rotate(100);
        ring.Rotate(111);
        var fail = false;
        ring.EnablePersistence(_ =>
        {
            if (fail) throw new IOException("Injected policy save failure.");
        });
        fail = true;
        Assert.Throws<IOException>(() => ring.Retained = 1);
        Assert.Throws<IOException>(() => ring.Interval = TimeSpan.FromSeconds(30));
        Assert.Throws<IOException>(() => ring.Enforce = true);
        Assert.Equal(2, ring.Count);
        Assert.Equal(2, ring.Retained);
        Assert.Equal(TimeSpan.FromSeconds(10), ring.Interval);
        Assert.False(ring.Enforce);
    }

    [Fact]
    public void PersistenceLifetimeTransfersOnlyOnSuccessfulBindAndIsDisposedOnce()
    {
        using var ring = new DestinationRatchets();
        var rejectedLifetime = new Lifetime();
        Assert.Throws<IOException>(() => ring.EnablePersistence(_ => throw new IOException("No storage."), rejectedLifetime));
        Assert.False(ring.HasPersistence);
        Assert.Equal(0, rejectedLifetime.DisposeCount);
        rejectedLifetime.Dispose();
        var ownedLifetime = new Lifetime();
        ring.EnablePersistence(_ => { }, ownedLifetime);
        Assert.Throws<InvalidOperationException>(() => ring.EnablePersistence(_ => { }));
        ring.Dispose();
        ring.Dispose();
        Assert.Equal(1, ownedLifetime.DisposeCount);
        Assert.False(ring.HasPersistence);
    }

    [Fact]
    public void PersistenceCallbackCannotReenterMutationOrDisposeTheRing()
    {
        using var ring = new DestinationRatchets();
        ring.EnablePersistence(_ =>
        {
            Assert.Throws<InvalidOperationException>(() => ring.Rotate(100));
            Assert.Throws<InvalidOperationException>(() => ring.Interval = TimeSpan.Zero);
            Assert.Throws<InvalidOperationException>(() => ring.Dispose());
        });
        Assert.NotNull(ring.Rotate(100));
        Assert.Equal(1, ring.Count);
    }
}
