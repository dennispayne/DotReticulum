using System.Security.Cryptography;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;

namespace DotReticulum.Applications.Tests;

// RNS Destination.py:622-650 reloads persisted keys after failed ratchet decryption;
// Identity.py:852-900 reports the actual selected ratchet. Pin a29bfb51c468945c96a1ee22cae0114e993e2391.
public sealed class Parity5EndpointRuntimeRatchetTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine("TestResults", "parity5-runtime-ratchets", Guid.NewGuid().ToString("N")));
    private static readonly byte[] Hash = Enumerable.Repeat((byte)5, 16).ToArray();

    [Fact]
    public async Task FailedDecryptReloadsSignedReplacementAndPreservesLivePolicyAfterOpeningCancellation()
    {
        using var identity = Identity.Generate();
        using var opening = new CancellationTokenSource();
        var store = new RuntimeStateStore(_directory);
        using var original = await store.OpenPersistentRatchetsAsync(Hash, identity, opening.Token);
        original.Retained = 3;
        original.Interval = TimeSpan.FromSeconds(90);
        original.Enforce = true;
        original.Rotate(100);
        await opening.CancelAsync();
        using var replacement = new DestinationRatchets { Retained = 1, Interval = TimeSpan.FromSeconds(1) };
        var publicKey = replacement.Rotate(200);
        await store.SaveRatchetsAsync(Hash, identity, replacement);
        var ciphertext = identity.Encrypt("reloaded"u8, publicKey);
        var privateKey = identity.ExportPrivateKey();
        Identity decryptor;
        try { decryptor = Identity.FromPrivateKey(privateKey); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
        using var ownedDecryptor = decryptor;
        identity.Dispose();
        var result = original.DecryptWithMetadata(decryptor, ciphertext);
        Assert.NotNull(result);
        Assert.Equal("reloaded"u8.ToArray(), result.Plaintext);
        Assert.Equal(DestinationRatchets.GetRatchetId(publicKey), result.RatchetId);
        Assert.Equal(publicKey, original.CurrentPublicKey);
        Assert.Equal(3, original.Retained);
        Assert.Equal(TimeSpan.FromSeconds(90), original.Interval);
        Assert.True(original.Enforce);
        Assert.Equal(100, original.LatestRotationUnix);
    }

    [Fact]
    public async Task CorruptSignedReloadPreservesPreviousKeysAndDoesNotOverwriteCorruptFile()
    {
        using var identity = Identity.Generate();
        var store = new RuntimeStateStore(_directory);
        using var ring = await store.OpenPersistentRatchetsAsync(Hash, identity);
        ring.Enforce = true;
        var publicKey = ring.Rotate(100);
        var goodToken = identity.Encrypt("still valid"u8, publicKey);
        var path = RatchetPath(store);
        var corrupt = await File.ReadAllBytesAsync(path);
        corrupt[^1] ^= 1;
        await File.WriteAllBytesAsync(path, corrupt);
        var failedToken = identity.Encrypt("not ratcheted"u8);
        var error = Assert.Throws<InvalidDataException>(() => ring.DecryptWithMetadata(identity, failedToken));
        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Equal(publicKey, ring.CurrentPublicKey);
        Assert.Equal("still valid"u8.ToArray(), ring.Decrypt(identity, goodToken));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(path));
        CryptographicOperations.ZeroMemory(corrupt);
    }

    [Fact]
    public async Task MissingReloadFileIsExplicitAndNeverSilentlyReinitializesTheRing()
    {
        using var identity = Identity.Generate();
        var store = new RuntimeStateStore(_directory);
        using var ring = await store.OpenPersistentRatchetsAsync(Hash, identity);
        ring.Enforce = true;
        var original = ring.Rotate(100);
        var path = RatchetPath(store);
        File.Delete(path);
        Assert.Throws<IOException>(() => ring.Decrypt(identity, identity.Encrypt("base key rejected"u8)));
        Assert.Equal(original, ring.CurrentPublicKey);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ConcurrentPersistedRotationAndReloadRemainSignedBoundedAndDeadlockFree()
    {
        using var identity = Identity.Generate();
        var store = new RuntimeStateStore(_directory);
        using var ring = await store.OpenPersistentRatchetsAsync(Hash, identity);
        ring.Retained = 3;
        ring.Interval = TimeSpan.Zero;
        ring.Rotate(100);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 10; index++)
            {
                if (worker == 0) ring.Rotate(101 + index);
                else Assert.True(ring.Reload());
            }
        }))).WaitAsync(TimeSpan.FromSeconds(15));
        using var loaded = await store.LoadRatchetsAsync(Hash, identity);
        Assert.NotNull(loaded);
        Assert.Equal(ring.CurrentPublicKey, loaded.CurrentPublicKey);
        Assert.InRange(loaded.Count, 1, 3);
        var token = identity.Encrypt("persisted metadata"u8, loaded.CurrentPublicKey);
        var result = ring.DecryptWithMetadata(identity, token);
        Assert.NotNull(result);
        Assert.Equal(DestinationRatchets.GetRatchetId(loaded.CurrentPublicKey!), result.RatchetId);
    }

    private static string RatchetPath(RuntimeStateStore store) => Path.Combine(store.StorageDirectory,
        "destination-ratchets", Convert.ToHexStringLower(Hash) + ".v1.msgpack");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
