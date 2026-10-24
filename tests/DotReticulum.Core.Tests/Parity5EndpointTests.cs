using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Crypto;

namespace DotReticulum.Core.Tests;

/// <summary>Independent vectors: GenerateParity5EndpointVectors.py, RNS a29bfb51c468945c96a1ee22cae0114e993e2391.
/// Exact source hashes are recorded in Fixtures/parity5-endpoints.json. No .NET output generated these fixtures.</summary>
public sealed class Parity5EndpointTests
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity5-endpoints.json")));
    private static byte[] Bytes(JsonElement element, string field) => Convert.FromHexString(element.GetProperty(field).GetString()!);

    [Fact]
    public void DynamicDefaultsEmptyOverridesAndCachedPathResponsesMatchReferencePayloads()
    {
        using var fixture = Fixture();
        using var identity = Identity.FromPrivateKey(Bytes(fixture.RootElement, "private_key"));
        var time = new EndpointTime(1_700_000_000);
        using var endpoint = DestinationEndpoint.Create(DestinationType.Single, DestinationDirection.In, "parity5", ["endpoint"], identity, time);
        var calls = 0;
        endpoint.SetDefaultAppDataProvider(() => System.Text.Encoding.UTF8.GetBytes($"default-{++calls}"));
        byte[] random = [0, 1, 2, 3, 4, 0, 0x65, 0x53, 0xf1, 0];
        Packet[] packets =
        [
            endpoint.CreateAnnounce(tag: "tag"u8.ToArray(), randomHash: random),
            endpoint.CreateAnnounce("ignored-on-cache-hit"u8.ToArray(), pathResponse: true, tag: "tag"u8.ToArray()),
            endpoint.CreateAnnounce([], tag: "empty"u8.ToArray(), randomHash: random),
            endpoint.CreateAnnounce(tag: "new"u8.ToArray(), randomHash: random),
        ];
        var expected = fixture.RootElement.GetProperty("announces").EnumerateArray().ToArray();
        Assert.Equal(Bytes(fixture.RootElement, "destination_hash"), endpoint.Destination.Hash);
        for (var index = 0; index < packets.Length; index++)
        {
            Assert.Equal(Bytes(expected[index], "payload"), packets[index].Payload.ToArray());
            Assert.Equal(expected[index].GetProperty("context").GetByte(), packets[index].Context);
            Assert.True(Announce.Validate(packets[index]).IsValid);
        }
        Assert.Equal(fixture.RootElement.GetProperty("default_calls").GetInt32(), calls);
    }

    [Fact]
    public void CacheExpiresStrictlyAfterReferenceWindowAndIsBounded()
    {
        using var identity = Identity.Generate();
        var time = new EndpointTime(100);
        using var endpoint = DestinationEndpoint.Create(DestinationType.Single, DestinationDirection.In, "test", ["cache"], identity, time, pathResponseCapacity: 2);
        var first = endpoint.CreateAnnounce([1], tag: [1]);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(first.Payload.ToArray(), endpoint.CreateAnnounce([2], pathResponse: true, tag: [1]).Payload.ToArray());
        time.Advance(TimeSpan.FromTicks(1));
        var expired = endpoint.CreateAnnounce([2], pathResponse: true, tag: [1]);
        Assert.NotEqual(first.Payload.ToArray(), expired.Payload.ToArray());
        endpoint.CreateAnnounce(tag: [2]);
        endpoint.CreateAnnounce(tag: [3]);
        Assert.Equal(2, endpoint.CachedPathResponses);
        Assert.Throws<ArgumentException>(() => endpoint.CreateAnnounce(tag: new byte[17]));
    }

    [Fact]
    public void CachedRatchetPayloadKeepsItsContextFlagWithoutRotatingAgain()
    {
        using var identity = Identity.Generate();
        using var ring = new DestinationRatchets { Interval = TimeSpan.Zero };
        var time = new EndpointTime(100);
        using var endpoint = DestinationEndpoint.Create(DestinationType.Single, DestinationDirection.In, "test", ["ratchet"], identity, time);
        endpoint.Ratchets = ring;
        var original = endpoint.CreateAnnounce([1], tag: [7]);
        time.Advance(TimeSpan.FromSeconds(1));
        var cached = endpoint.CreateAnnounce(pathResponse: true, tag: [7]);
        Assert.True(cached.ContextFlag);
        Assert.Equal(original.Payload.ToArray(), cached.Payload.ToArray());
        Assert.Equal(1, ring.Count);
        Assert.True(Announce.Validate(cached).IsValid);
    }

    [Fact]
    public void OversizedAnnounceFailsBeforePersistingOrRotatingRatchets()
    {
        using var identity = Identity.Generate();
        using var ring = new DestinationRatchets();
        var destination = Destination.Create(DestinationType.Single, "test", [], identity);
        Assert.Throws<ArgumentException>(() => Announce.Create(identity, destination, new byte[302], ring));
        Assert.Equal(0, ring.Count);
        var maximum = Announce.Create(identity, destination, new byte[301], ring);
        Assert.Equal(Packet.Mtu - Packet.Header1Size, maximum.Payload.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GroupKeysDecryptIndependentLegacyAndCurrentReferenceTokens(int index)
    {
        using var fixture = Fixture();
        using var identity = Identity.FromPrivateKey(Bytes(fixture.RootElement, "private_key"));
        using var endpoint = DestinationEndpoint.Create(DestinationType.Group, DestinationDirection.Out, "test", ["group"], identity);
        var vector = fixture.RootElement.GetProperty("groups")[index];
        var key = Bytes(vector, "key");
        var token = Bytes(vector, "token");
        Assert.Throws<InvalidOperationException>(() => endpoint.Encrypt("data"u8));
        endpoint.LoadGroupKey(key);
        key[0] ^= 1;
        Assert.Equal(Bytes(vector, "plaintext"), endpoint.Decrypt(token));
        Assert.Throws<ArgumentException>(() => endpoint.LoadGroupKey(new byte[33]));
        Assert.Equal(Bytes(vector, "plaintext"), endpoint.Decrypt(token));
        token[^1] ^= 1;
        Assert.Null(endpoint.Decrypt(token));
        var exported = endpoint.ExportGroupKey();
        exported[0] ^= 1;
        Assert.NotEqual(exported, endpoint.ExportGroupKey());
        endpoint.CreateGroupKey();
        Assert.Equal(64, endpoint.ExportGroupKey().Length);
        Assert.Equal("new"u8.ToArray(), endpoint.Decrypt(endpoint.Encrypt("new"u8)));
        endpoint.Dispose();
        Assert.Throws<ObjectDisposedException>(() => endpoint.ExportGroupKey());
    }

    [Fact]
    public void PlainEndpointIsIdentityFreeAndDoesNotEncryptOrSign()
    {
        using var endpoint = DestinationEndpoint.Create(DestinationType.Plain, DestinationDirection.In, "test", ["plain"]);
        Assert.Null(endpoint.Identity);
        Assert.True(endpoint.AcceptLinks);
        Assert.Equal("data"u8.ToArray(), endpoint.Encrypt("data"u8));
        Assert.Equal("data"u8.ToArray(), endpoint.Decrypt("data"u8));
        Assert.Null(endpoint.Sign("data"u8));
        Assert.Throws<InvalidOperationException>(() => endpoint.CreateGroupKey());
        Assert.Throws<InvalidOperationException>(() => endpoint.CreateAnnounce());
    }

    [Fact]
    public void FactoryValidatesDirectionTypeIdentityAndOwnsOnlyGeneratedIdentities()
    {
        using var fixture = Fixture();
        Assert.True(fixture.RootElement.GetProperty("endpoint_contract").GetProperty("accepts_links_default").GetBoolean());
        foreach (var name in new[] { "duplicate_incoming", "invalid_direction", "plain_with_identity", "outgoing_without_identity" })
            Assert.False(fixture.RootElement.GetProperty("endpoint_contract").GetProperty(name).GetProperty("accepted").GetBoolean());
        using var identity = Identity.Generate();
        Assert.Throws<ArgumentOutOfRangeException>(() => DestinationEndpoint.Create(DestinationType.Single, (DestinationDirection)0, "test", []));
        Assert.Throws<ArgumentOutOfRangeException>(() => DestinationEndpoint.Create(DestinationType.Link, DestinationDirection.In, "test", []));
        Assert.Throws<ArgumentException>(() => DestinationEndpoint.Create(DestinationType.Plain, DestinationDirection.In, "test", [], identity));
        Assert.Throws<ArgumentException>(() => DestinationEndpoint.Create(DestinationType.Single, DestinationDirection.Out, "test", []));
        using var supplied = DestinationEndpoint.Create(DestinationType.Single, DestinationDirection.Out, "test", [], identity);
        Assert.Throws<InvalidOperationException>(() => supplied.CreateAnnounce());
        supplied.Dispose();
        Assert.Equal(64, identity.ExportPrivateKey().Length);
        using var generated = DestinationEndpoint.Create(DestinationType.Single, DestinationDirection.In, "test", []);
        Assert.Equal(generated.Identity!.HexHash, Assert.Single(generated.Destination.Aspects));
        generated.Dispose();
        Assert.Throws<ObjectDisposedException>(() => generated.Identity.ExportPrivateKey());
    }

    [Fact]
    public void DefaultDataCopiesCallerBufferAndProviderFailureDoesNotPopulateCache()
    {
        using var identity = Identity.Generate();
        using var endpoint = DestinationEndpoint.Create(DestinationType.Single, DestinationDirection.In, "test", [], identity);
        byte[] data = [1, 2];
        endpoint.SetDefaultAppData(data);
        data[0] = 9;
        var packet = endpoint.CreateAnnounce();
        Assert.True(Announce.TryParse(packet, out var parsed));
        Assert.Equal(new byte[] { 1, 2 }, parsed.AppData);
        endpoint.SetDefaultAppDataProvider(() => throw new IOException("provider"));
        var before = endpoint.CachedPathResponses;
        Assert.Throws<IOException>(() => endpoint.CreateAnnounce(tag: [9]));
        Assert.Equal(before, endpoint.CachedPathResponses);
        endpoint.SetDefaultAppData(null);
        Assert.True(Announce.TryParse(endpoint.CreateAnnounce(), out parsed));
        Assert.Null(parsed.AppData);
    }

    [Fact]
    public async Task ConcurrentGroupReplacementEncryptionAndDisposalAreSerialized()
    {
        using var identity = Identity.Generate();
        using var endpoint = DestinationEndpoint.Create(DestinationType.Group, DestinationDirection.In, "test", [], identity);
        endpoint.CreateGroupKey();
        var errors = new List<Exception>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 50; index++)
            {
                try
                {
                    if (worker == 0) endpoint.CreateGroupKey();
                    else if (worker == 1 && index == 49) endpoint.Dispose();
                    else Assert.NotEmpty(endpoint.Encrypt("concurrent"u8));
                }
                catch (ObjectDisposedException) { }
                catch (Exception error) { lock (errors) errors.Add(error); }
            }
        })));
        Assert.Empty(errors);
    }

    [Fact]
    public void MalformedReferenceAcceptedKeysAreIntentionallyRejectedAtNativeImportBoundary()
    {
        using var fixture = Fixture();
        var valid = Bytes(fixture.RootElement, "public_key");
        var zeroX = valid.ToArray();
        zeroX.AsSpan(0, 32).Clear();
        var noncanonical = valid.ToArray();
        Convert.FromHexString("edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f").CopyTo(noncanonical, 0);
        var zeroEd = valid.ToArray();
        zeroEd.AsSpan(32).Clear();
        foreach (var key in new[] { zeroX, noncanonical, zeroEd, valid[..63], valid.Concat(new byte[1]).ToArray() })
            Assert.Throws<ArgumentException>(() => Identity.FromPublicKey(key));
        Assert.True(fixture.RootElement.GetProperty("malformed_keys").GetProperty("zero-x25519").GetProperty("result").GetBoolean());
    }

    [Fact]
    public void HkdfMatchesIndependentReferenceAtRfcBoundaryAndRejectsReferenceCounterWrap()
    {
        using var fixture = Fixture();
        var cases = fixture.RootElement.GetProperty("hkdf").EnumerateArray().ToArray();
        foreach (var item in cases.Take(2))
        {
            var output = KeyDerivation.HkdfSha256("material"u8, item.GetProperty("length").GetInt32(), info: "boundary"u8);
            Assert.Equal(item.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(output)));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyDerivation.HkdfSha256("material"u8, 8161));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyDerivation.HkdfSha256("material"u8, 0));
        Assert.Throws<ArgumentException>(() => KeyDerivation.HkdfSha256([], 32));
        Assert.True(cases[2].GetProperty("accepted").GetBoolean());
        Assert.True(cases[4].GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public void StrictPkcs7RejectsAuthenticatedMalformedPaddingAcceptedByReference()
    {
        using var fixture = Fixture();
        using var token = new Token(Bytes(fixture.RootElement, "padding_key"));
        foreach (var item in fixture.RootElement.GetProperty("padding").EnumerateArray())
        {
            var encoded = Bytes(item, "token");
            Assert.True(token.VerifyHmac(encoded));
            if (item.GetProperty("name").GetString() == "valid-two")
                Assert.Equal(Bytes(item, "result"), token.Decrypt(encoded));
            else
                Assert.Throws<CryptographicException>(() => token.Decrypt(encoded));
        }
        var mismatch = fixture.RootElement.GetProperty("padding")[1];
        Assert.True(mismatch.GetProperty("accepted").GetBoolean());
        Assert.True(fixture.RootElement.GetProperty("padding")[2].GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public void IndependentX25519ProvidersAgreeOnCanonicalInputButNativeRejectsAliasesAndLowOrder()
    {
        using var fixture = Fixture();
        var privateKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        foreach (var item in fixture.RootElement.GetProperty("x25519_providers").EnumerateArray())
        {
            var peer = Bytes(item, "peer");
            if (item.GetProperty("name").GetString() == "canonical")
            {
                var shared = X25519.Agree(privateKey, peer);
                Assert.Equal(Bytes(item.GetProperty("pyca"), "result"), shared);
                Assert.Equal(Bytes(item.GetProperty("internal"), "result"), shared);
            }
            else
            {
                Assert.Throws<CryptographicException>(() => X25519.Agree(privateKey, peer));
            }
        }
    }

    private sealed class EndpointTime(long unix) : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(unix);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
