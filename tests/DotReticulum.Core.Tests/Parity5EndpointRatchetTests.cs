using System.Security.Cryptography;
using System.Text.Json;

namespace DotReticulum.Core.Tests;

public sealed class Parity5EndpointRatchetTests
{
    [Fact]
    public void SelectedRatchetMetadataMatchesReferenceForOlderKeysBaseFallbackEnforcementAndEmptyPlaintext()
    {
        // Identity.py:852-900 assigns ratchet_id_receiver.latest_ratchet_id; Destination.py:413-414 copies it to the packet.
        // Generator executes those methods from verified a29bfb51c468945c96a1ee22cae0114e993e2391.
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity5-endpoints.json")));
        using var identity = Identity.FromPrivateKey(Convert.FromHexString(fixture.RootElement.GetProperty("private_key").GetString()!));
        var keys = fixture.RootElement.GetProperty("ratchet_keys").EnumerateArray()
            .Select(value => Convert.FromHexString(value.GetString()!)).ToArray();
        try
        {
            foreach (var vector in fixture.RootElement.GetProperty("ratchet_metadata").EnumerateArray())
            {
                var ciphertext = Convert.FromHexString(vector.GetProperty("ciphertext").GetString()!);
                var enforce = vector.GetProperty("enforce").GetBoolean();
                using var ring = DestinationRatchets.FromPrivateKeys(keys);
                ring.Enforce = enforce;
                using var endpoint = DestinationEndpoint.Create(DestinationType.Single, DestinationDirection.In, "parity5", ["metadata"], identity);
                endpoint.Ratchets = ring;
                var result = endpoint.DecryptWithMetadata(ciphertext);
                var direct = identity.DecryptWithMetadata(ciphertext, keys, enforce);
                if (vector.GetProperty("plaintext").ValueKind == JsonValueKind.Null)
                {
                    Assert.Null(result);
                    Assert.Null(direct);
                    continue;
                }
                Assert.NotNull(result);
                Assert.NotNull(direct);
                var plaintext = Convert.FromHexString(vector.GetProperty("plaintext").GetString()!);
                var ratchetId = vector.GetProperty("ratchet_id").ValueKind == JsonValueKind.Null
                    ? null : Convert.FromHexString(vector.GetProperty("ratchet_id").GetString()!);
                Assert.Equal(plaintext, result.Plaintext);
                Assert.Equal(plaintext, direct.Plaintext);
                Assert.Equal(plaintext, endpoint.Decrypt(ciphertext));
                Assert.Equal(ratchetId, result.RatchetId);
                Assert.Equal(ratchetId, direct.RatchetId);
                if (result.RatchetId is { } exportedId)
                {
                    exportedId[0] ^= 1;
                    Assert.Equal(ratchetId, result.RatchetId);
                }
                Assert.Null(endpoint.DecryptWithMetadata([0]));
                Assert.Equal(ratchetId, result.RatchetId);
            }
        }
        finally { foreach (var key in keys) CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task ConcurrentEndpointDecryptionsHaveIndependentSelectedRatchetMetadata()
    {
        using var identity = Identity.Generate();
        using var ring = new DestinationRatchets { Interval = TimeSpan.Zero };
        var older = ring.Rotate(100);
        ring.Rotate(101);
        using var endpoint = DestinationEndpoint.Create(DestinationType.Single, DestinationDirection.In, "parity5", ["concurrent-metadata"], identity);
        endpoint.Ratchets = ring;
        var ratcheted = identity.Encrypt("ratcheted"u8, older);
        var fallback = identity.Encrypt("fallback"u8);
        var expectedId = DestinationRatchets.GetRatchetId(older);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            var result = endpoint.DecryptWithMetadata(index % 2 == 0 ? ratcheted : fallback);
            Assert.NotNull(result);
            Assert.Equal(index % 2 == 0 ? expectedId : null, result.RatchetId);
            Assert.Equal(index % 2 == 0 ? "ratcheted"u8.ToArray() : "fallback"u8.ToArray(), result.Plaintext);
        })));
    }

    [Fact]
    public void SignedRatchetCodecMatchesIndependentReferenceAndRejectsTamperingAndWrongSigner()
    {
        // GenerateParity5EndpointVectors.py, pinned a29bfb51c468945c96a1ee22cae0114e993e2391.
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity5-endpoints.json")));
        using var identity = Identity.FromPrivateKey(Convert.FromHexString(fixture.RootElement.GetProperty("private_key").GetString()!));
        var encoded = Convert.FromHexString(fixture.RootElement.GetProperty("signed_ratchets").GetString()!);
        var keys = SignedDestinationRatchetFile.Decode(identity, encoded);
        try
        {
            Assert.Equal(2, keys.Count);
            Assert.Equal(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(), keys[0]);
            Assert.Equal(encoded, SignedDestinationRatchetFile.Encode(identity, keys));
            using var wrong = Identity.Generate();
            Assert.Throws<InvalidDataException>(() => SignedDestinationRatchetFile.Decode(wrong, encoded));
            encoded[^1] ^= 1;
            Assert.Throws<InvalidDataException>(() => SignedDestinationRatchetFile.Decode(identity, encoded));
            Assert.Throws<InvalidDataException>(() => SignedDestinationRatchetFile.Decode(identity, new byte[SignedDestinationRatchetFile.MaximumBytes + 1]));
        }
        finally { foreach (var key in keys) CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public void SignedButMalformedKeysAreRejectedAfterSignatureVerification()
    {
        using var identity = Identity.Generate();
        var packed = MsgPack.Pack(new object?[] { new byte[31] });
        var envelope = MsgPack.Pack(new Dictionary<object, object?>
        {
            ["signature"] = identity.Sign(packed), ["ratchets"] = packed,
        });
        Assert.Throws<InvalidDataException>(() => SignedDestinationRatchetFile.Decode(identity, envelope));
        Assert.Throws<InvalidDataException>(() => SignedDestinationRatchetFile.Decode(identity, [0x81]));
    }

    [Fact]
    public async Task ReloadRotationAndDecryptSerializeWithoutPublishingUnsavedKeys()
    {
        using var identity = Identity.Generate();
        using var ring = new DestinationRatchets { Retained = 2, Interval = TimeSpan.Zero, Enforce = true };
        var stored = Array.Empty<byte[]>();
        ring.EnablePersistence(snapshot =>
        {
            foreach (var key in stored) CryptographicOperations.ZeroMemory(key);
            stored = snapshot.PrivateKeys.Select(key => key.ToArray()).ToArray();
        });
        ring.EnableReload(() => stored.Select(key => key.ToArray()).ToArray());
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                for (var index = 0; index < 25; index++)
                {
                    if (worker == 0) ring.Rotate(100 + index);
                    else ring.Reload();
                    using var snapshot = ring.ExportSnapshot();
                    Assert.InRange(snapshot.PrivateKeys.Count, 0, 2);
                }
            })));
            Assert.Equal(ring.ExportPrivateKeys().Select(key => Convert.ToHexString(key)), stored.Select(key => Convert.ToHexString(key)));
            var token = identity.Encrypt("persisted"u8, ring.CurrentPublicKey);
            Assert.Equal("persisted"u8.ToArray(), ring.Decrypt(identity, token));
        }
        finally { foreach (var key in stored) CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public void FailedReloadPreservesKeysAndCannotMutateOrDisposeRingReentrantly()
    {
        using var ring = new DestinationRatchets();
        var original = ring.Rotate(100);
        ring.EnableReload(() =>
        {
            Assert.Throws<InvalidOperationException>(() => ring.Rotate(200));
            Assert.Throws<InvalidOperationException>(() => ring.Dispose());
            return [new byte[31]];
        });
        Assert.Throws<InvalidDataException>(() => ring.Reload());
        Assert.Equal(original, ring.CurrentPublicKey);
        Assert.Equal(1, ring.Count);
    }

    [Fact]
    public void FailedDecryptReloadsSignedStoreAndRetriesWithNewPrivateKeys()
    {
        using var identity = Identity.Generate();
        using var first = new DestinationRatchets { Enforce = true };
        using var replacement = new DestinationRatchets();
        first.Rotate(100);
        var publicKey = replacement.Rotate(200);
        var ciphertext = identity.Encrypt("replacement"u8, publicKey);
        first.EnableReload(() => replacement.ExportPrivateKeys());
        Assert.Equal("replacement"u8.ToArray(), first.Decrypt(identity, ciphertext));
        Assert.Equal(publicKey, first.CurrentPublicKey);
    }

    [Fact]
    public void SignedFileSurvivesRestartRejectsConcurrentWriterAndRollsBackFailedSave()
    {
        using var identity = Identity.Generate();
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "parity5-ratchet-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "ratchets");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] original;
            using (var ring = SignedDestinationRatchetFile.Open(path, identity))
            {
                original = ring.Rotate(100);
                Assert.Throws<IOException>(() => SignedDestinationRatchetFile.Open(path, identity));
                Assert.Equal("data"u8.ToArray(), ring.Decrypt(identity, identity.Encrypt("data"u8, original)));
            }
            using (var restarted = SignedDestinationRatchetFile.Open(path, identity))
            {
                Assert.Equal(original, restarted.CurrentPublicKey);
                File.Delete(path);
                Directory.CreateDirectory(path);
                if (OperatingSystem.IsWindows())
                    Assert.Throws<UnauthorizedAccessException>(() => restarted.Rotate(2000));
                else
                    Assert.ThrowsAny<IOException>(() => restarted.Rotate(2000));
                Assert.Equal(original, restarted.CurrentPublicKey);
                Directory.Delete(path);
            }
            Assert.Empty(Directory.GetFiles(directory, "*.out"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
