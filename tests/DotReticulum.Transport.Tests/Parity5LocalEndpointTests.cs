using System.Collections.Concurrent;
using DotReticulum.Core;
using static DotReticulum.Transport.Tests.Parity4LinkMtuTests;

namespace DotReticulum.Transport.Tests;

/// <summary>Managed integration acceptance for the bounded endpoint contracts observed by
/// GenerateParity5EndpointVectors.py at a29bfb51c468945c96a1ee22cae0114e993e2391.
/// These tests exercise the router and actual packet-interface sends, not live Python interoperability.</summary>
[Trait("Category", "Unit")]
public sealed class Parity5LocalEndpointTests
{
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<T> Completed<T>(TaskCompletionSource<T> completion) => completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    private static Packet Data(LocalDestination destination, ReadOnlySpan<byte> plaintext) =>
        Packet.Create(PacketType.Data, destination.Destination.Type, destination.Destination.Hash, destination.Encrypt(plaintext));
    private static Task Deliver(Harness node, Packet packet) => node.Router.HandleAsync(Received(packet, node.Interface)).AsTask();
    private static LocalDestination Single(Identity identity, string aspect = "local", ProofStrategy proof = ProofStrategy.None) =>
        new(identity, Destination.Create(DestinationType.Single, "parity5", [aspect], identity)) { ProofStrategy = proof };

    [Fact]
    public async Task RegistrationRejectsDuplicateAndOutgoingEndpointsAndDeregistrationPreservesActualOwner()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        using var local = Single(identity);
        using var duplicate = Single(identity);
        using var outgoing = new LocalDestination(identity, Destination.Create(DestinationType.Single, "parity5", ["outgoing"], identity), DestinationDirection.Out);
        Assert.True(local.AcceptLinks);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalDestination(identity, local.Destination, (DestinationDirection)0));
        using var otherIdentity = Identity.Generate();
        Assert.Throws<ArgumentException>(() => new LocalDestination(otherIdentity, local.Destination));
        Assert.Throws<ArgumentException>(() => node.Router.RegisterLocalDestination(outgoing));
        node.Router.RegisterLocalDestination(local);
        Assert.Throws<InvalidOperationException>(() => node.Router.RegisterLocalDestination(local));
        Assert.Throws<InvalidOperationException>(() => node.Router.RegisterLocalDestination(duplicate));
        node.Router.DeregisterLocalDestination(duplicate);
        using (var recalled = node.Router.Engine.Known.Recall(local.Destination.Hash))
        {
            Assert.NotNull(recalled);
            Assert.False(recalled.HasPrivateKey);
            Assert.Equal(identity.ExportPublicKey(), recalled.ExportPublicKey());
            Assert.Null(recalled.AppData);
        }
        Assert.Empty(node.Router.Engine.Known.ExportSnapshot());
        node.Router.DeregisterLocalDestination(local);
        Assert.Null(node.Router.Engine.Known.Recall(local.Destination.Hash));
        node.Router.RegisterLocalDestination(duplicate);
        using var replacement = node.Router.Engine.Known.Recall(duplicate.Destination.Hash);
        Assert.NotNull(replacement);
    }

    [Fact]
    public async Task DefaultLinkAcceptanceActuallyRespondsToIndependentReferenceRequest()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
        using var local = new LocalDestination(identity,
            Destination.Create(DestinationType.Single, "dotreticulum", ["parity4", "link"], identity));
        node.Router.RegisterLocalDestination(local);
        await Deliver(node, Packet.Parse(Hex(MtuVector(500), "requestRaw")));
        await LinkTests.Until(() => node.Interface.Sent.Any(packet => packet.Context == LinkContext.LrProof));
        Assert.Single(node.Interface.Sent, packet => packet.Context == LinkContext.LrProof);
    }

    [Fact]
    public async Task ExplicitLinkRejectionSendsNoHandshakeProof()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
        using var local = new LocalDestination(identity,
            Destination.Create(DestinationType.Single, "dotreticulum", ["parity4", "link"], identity)) { AcceptLinks = false };
        node.Router.RegisterLocalDestination(local);
        await Deliver(node, Packet.Parse(Hex(MtuVector(500), "requestRaw")));
        Assert.Empty(node.Interface.Sent);
    }

    [Fact]
    public async Task ValidSingleDataIsAutomaticallyProvedWithoutAnySubscriber()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        using var local = Single(identity, proof: ProofStrategy.All);
        node.Router.RegisterLocalDestination(local);
        var data = Data(local, "no subscriber"u8);
        await Deliver(node, data);
        var proof = Assert.Single(node.Interface.Sent, packet => packet.PacketType == PacketType.Proof);
        Assert.Equal(data.CalculateTruncatedHash(), proof.DestinationHash.ToArray());
        Assert.True(identity.Verify(proof.Payload.Span, data.CalculateHash()));
    }

    [Fact]
    public async Task SelectedRatchetMetadataReachesBothObserversAndDoesNotLeakIntoBaseFallback()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        using var ring = new DestinationRatchets { Interval = TimeSpan.Zero };
        var older = ring.Rotate(100);
        ring.Rotate(101);
        using var local = new LocalDestination(identity, Destination.Create(DestinationType.Single, "parity5", ["received-metadata"], identity))
        {
            Ratchets = ring,
        };
        var ratcheted = Packet.Create(PacketType.Data, DestinationType.Single, local.Destination.Hash, identity.Encrypt("ratcheted"u8, older));
        var fallback = Packet.Create(PacketType.Data, DestinationType.Single, local.Destination.Hash, identity.Encrypt("fallback"u8));
        var rawRatcheted = Completion<ReceivedPacket>();
        var rawFallback = Completion<ReceivedPacket>();
        var decryptedRatcheted = Completion<byte[]?>();
        var decryptedFallback = Completion<byte[]?>();
        local.PacketReceived = received =>
        {
            if (received.Packet.CalculateHash().AsSpan().SequenceEqual(ratcheted.CalculateHash())) rawRatcheted.TrySetResult(received);
            else rawFallback.TrySetResult(received);
            return ValueTask.CompletedTask;
        };
        local.PacketDecrypted = (plaintext, received) =>
        {
            if (plaintext.AsSpan().SequenceEqual("ratcheted"u8)) decryptedRatcheted.TrySetResult(received.RatchetId);
            else decryptedFallback.TrySetResult(received.RatchetId);
            return ValueTask.CompletedTask;
        };
        node.Router.RegisterLocalDestination(local);
        await Deliver(node, ratcheted);
        await Deliver(node, fallback);
        var expected = DestinationRatchets.GetRatchetId(older);
        var ratchetedDelivery = await Completed(rawRatcheted);
        Assert.Equal(expected, ratchetedDelivery.RatchetId);
        Assert.Equal(expected, await Completed(decryptedRatcheted));
        Assert.Null((await Completed(rawFallback)).RatchetId);
        Assert.Null(await Completed(decryptedFallback));
        var exported = ratchetedDelivery.RatchetId!;
        exported[0] ^= 1;
        Assert.Equal(expected, ratchetedDelivery.RatchetId);
    }

    [Fact]
    public async Task UndecryptableSingleDataHasNeitherProofNorRawOrDecryptedDelivery()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        var rawCalls = 0;
        var decryptedCalls = 0;
        using var local = Single(identity, proof: ProofStrategy.All);
        local.PacketReceived = _ => { Interlocked.Increment(ref rawCalls); return ValueTask.CompletedTask; };
        local.PacketDecrypted = (_, _) => { Interlocked.Increment(ref decryptedCalls); return ValueTask.CompletedTask; };
        node.Router.RegisterLocalDestination(local);
        var valid = Data(local, "authenticated"u8);
        var corrupted = valid.Payload.ToArray();
        corrupted[^1] ^= 1;
        await Deliver(node, Packet.Create(PacketType.Data, DestinationType.Single, local.Destination.Hash, corrupted));
        Assert.Empty(node.Interface.Sent);
        // A valid packet is a work-barrier for the same endpoint. Its callbacks must run, the corrupted packet's must not.
        var observed = Completion<bool>();
        local.PacketDecrypted = (_, _) => { Interlocked.Increment(ref decryptedCalls); observed.TrySetResult(true); return ValueTask.CompletedTask; };
        await Deliver(node, valid);
        await Completed(observed);
        await LinkTests.Until(() => Volatile.Read(ref rawCalls) == 1);
        Assert.Equal(1, Volatile.Read(ref decryptedCalls));
        Assert.Single(node.Interface.Sent, packet => packet.PacketType == PacketType.Proof);
    }

    [Fact]
    public async Task DestinationTypeMismatchCannotBypassTheRegisteredEndpointContract()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        using var local = Single(identity, proof: ProofStrategy.All);
        var observed = new ConcurrentQueue<DestinationType>();
        var delivered = Completion<bool>();
        local.PacketDecrypted = (_, received) =>
        {
            observed.Enqueue(received.Packet.DestinationType);
            if (received.Packet.DestinationType == DestinationType.Single) delivered.TrySetResult(true);
            return ValueTask.CompletedTask;
        };
        node.Router.RegisterLocalDestination(local);
        var valid = Data(local, "correct type"u8);
        await Deliver(node, Packet.Create(PacketType.Data, DestinationType.Plain, local.Destination.Hash, valid.Payload));
        Assert.Empty(node.Interface.Sent);
        await Deliver(node, valid);
        await Completed(delivered);
        Assert.Equal(DestinationType.Single, Assert.Single(observed));
        Assert.Single(node.Interface.Sent, packet => packet.PacketType == PacketType.Proof);
    }

    [Theory]
    [InlineData(DestinationType.Single)]
    [InlineData(DestinationType.Group)]
    [InlineData(DestinationType.Plain)]
    public async Task RawAndDecryptedCallbacksSeeTheirDistinctPayloadsForEveryEndpointType(DestinationType type)
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        var destination = type == DestinationType.Plain
            ? Destination.Create(type, "parity5", ["plain"])
            : Destination.Create(type, "parity5", ["encrypted"], identity);
        using var local = new LocalDestination(identity, destination);
        if (type == DestinationType.Group)
        {
            Assert.Throws<InvalidOperationException>(() => local.Encrypt("payload"u8));
            local.LoadGroupKey(Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
            var exported = local.ExportGroupKey();
            exported[0] ^= 1;
            Assert.NotEqual(exported, local.ExportGroupKey());
        }
        var raw = Completion<byte[]>();
        var decrypted = Completion<byte[]>();
        local.PacketReceived = received => { raw.TrySetResult(received.Packet.Payload.ToArray()); return ValueTask.CompletedTask; };
        local.PacketDecrypted = (plaintext, _) => { decrypted.TrySetResult(plaintext); return ValueTask.CompletedTask; };
        node.Router.RegisterLocalDestination(local);
        var packet = Data(local, "payload"u8);
        await Deliver(node, packet);
        Assert.Equal(packet.Payload.ToArray(), await Completed(raw));
        Assert.Equal("payload"u8.ToArray(), await Completed(decrypted));
        Assert.Empty(node.Interface.Sent);
        if (type == DestinationType.Plain)
            Assert.Equal("payload"u8.ToArray(), packet.Payload.ToArray());
        else
            Assert.NotEqual("payload"u8.ToArray(), packet.Payload.ToArray());
        if (type == DestinationType.Group)
        {
            local.CreateGroupKey();
            Assert.Equal(64, local.ExportGroupKey().Length);
            Assert.Null(local.Decrypt(packet.Payload.Span));
        }
        local.Dispose();
        Assert.Throws<ObjectDisposedException>(() => local.Encrypt("disposed"u8));
    }

    [Fact]
    public async Task ThrowingRawObserverCannotSuppressDecryptedObserverProofOrNextPacket()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        using var local = Single(identity, proof: ProofStrategy.All);
        var logs = new ConcurrentQueue<string>();
        node.Router.Log = logs.Enqueue;
        var rawSecond = Completion<bool>();
        var decrypted = new ConcurrentQueue<byte[]>();
        var count = 0;
        local.PacketReceived = _ =>
        {
            if (Interlocked.Increment(ref count) == 1) throw new IOException("parity5-observer");
            rawSecond.TrySetResult(true);
            return ValueTask.CompletedTask;
        };
        local.PacketDecrypted = (data, _) => { decrypted.Enqueue(data); return ValueTask.CompletedTask; };
        node.Router.RegisterLocalDestination(local);
        await Deliver(node, Data(local, "first"u8));
        await Deliver(node, Data(local, "second"u8));
        await Completed(rawSecond);
        await LinkTests.Until(() => decrypted.Count == 2 && logs.Any(line => line.Contains("parity5-observer", StringComparison.Ordinal)));
        Assert.Contains(decrypted, bytes => bytes.AsSpan().SequenceEqual("first"u8));
        Assert.Contains(decrypted, bytes => bytes.AsSpan().SequenceEqual("second"u8));
        Assert.Equal(2, node.Interface.Sent.Count(packet => packet.PacketType == PacketType.Proof));
    }

    [Fact]
    public async Task StalledRawObserverDoesNotBlockCoreProofsOrUnrelatedEndpointDelivery()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        using var stalled = Single(identity, "stalled", ProofStrategy.All);
        using var healthy = Single(identity, "healthy", ProofStrategy.All);
        var entered = Completion<bool>();
        var release = Completion<bool>();
        var delivered = Completion<byte[]>();
        stalled.PacketReceived = async _ => { entered.TrySetResult(true); await release.Task; };
        healthy.PacketDecrypted = (data, _) => { delivered.TrySetResult(data); return ValueTask.CompletedTask; };
        node.Router.RegisterLocalDestination(stalled);
        node.Router.RegisterLocalDestination(healthy);
        try
        {
            await Deliver(node, Data(stalled, "blocked"u8)).WaitAsync(TimeSpan.FromSeconds(5));
            await Completed(entered);
            await Deliver(node, Data(healthy, "healthy"u8)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("healthy"u8.ToArray(), await Completed(delivered));
            Assert.Equal(2, node.Interface.Sent.Count(packet => packet.PacketType == PacketType.Proof));
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task ApplicationProofPolicyRunsWithoutPacketObserversAndFailuresDoNotPoisonNextDecision()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        using var local = Single(identity, proof: ProofStrategy.App);
        var decisions = 0;
        var failureObserved = Completion<bool>();
        var logs = new ConcurrentQueue<string>();
        node.Router.Log = line =>
        {
            logs.Enqueue(line);
            if (line.Contains("parity5-proof-policy", StringComparison.Ordinal)) failureObserved.TrySetResult(true);
        };
        local.ProofRequested = _ =>
        {
            if (Interlocked.Increment(ref decisions) == 1) throw new IOException("parity5-proof-policy");
            return true;
        };
        node.Router.RegisterLocalDestination(local);
        await Deliver(node, Data(local, "policy fails"u8));
        await Completed(failureObserved);
        Assert.Empty(node.Interface.Sent);
        var accepted = Data(local, "policy accepts"u8);
        await Deliver(node, accepted);
        await LinkTests.Until(() => node.Interface.Sent.Any(packet => packet.PacketType == PacketType.Proof));
        var proof = Assert.Single(node.Interface.Sent);
        Assert.True(identity.Verify(proof.Payload.Span, accepted.CalculateHash()));
        Assert.Equal(2, Volatile.Read(ref decisions));
    }

    [Fact]
    public async Task ApplicationProofPolicyCanRejectWithoutPacketSubscribers()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        using var local = Single(identity, proof: ProofStrategy.App);
        var rejected = Completion<bool>();
        local.ProofRequested = _ => { rejected.TrySetResult(true); return false; };
        node.Router.RegisterLocalDestination(local);
        await Deliver(node, Data(local, "reject"u8));
        await Completed(rejected);
        Assert.Empty(node.Interface.Sent);
        local.ProofRequested = _ => true;
        var accepted = Data(local, "accept"u8);
        await Deliver(node, accepted);
        await LinkTests.Until(() => node.Interface.Sent.Any(packet => packet.PacketType == PacketType.Proof));
        var proof = Assert.Single(node.Interface.Sent);
        Assert.True(identity.Verify(proof.Payload.Span, accepted.CalculateHash()));
    }

    [Fact]
    public async Task RouterAnnouncesUseDynamicDefaultsExplicitOverridesAndTaggedPayloadCache()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        using var ring = new DestinationRatchets();
        var clock = new Clock();
        using var local = new LocalDestination(identity, Destination.Create(DestinationType.Single, "parity5", ["announce"], identity),
            timeProvider: clock) { Ratchets = ring };
        node.Router.RegisterLocalDestination(local);
        var calls = 0;
        local.SetDefaultAppDataProvider(() => [(byte)Interlocked.Increment(ref calls)]);
        await node.Router.AnnounceAsync(local, tag: [1]);
        await node.Router.AnnounceAsync(local, pathResponse: true, appData: [99], tag: [1]);
        await node.Router.AnnounceAsync(local, appData: [], tag: [2]);
        await node.Router.AnnounceAsync(local, tag: [3]);
        var packets = node.Interface.Sent.Where(packet => packet.PacketType == PacketType.Announce).ToArray();
        Assert.Equal(4, packets.Length);
        Assert.Equal(packets[0].Payload.ToArray(), packets[1].Payload.ToArray());
        Assert.Equal(Announce.PathResponseContext, packets[1].Context);
        Assert.True(packets[1].ContextFlag);
        Assert.True(Announce.Validate(packets[1]).IsValid);
        Assert.True(Announce.TryParse(packets[2], out var empty));
        Assert.Empty(empty!.AppData!);
        Assert.True(Announce.TryParse(packets[3], out var latest));
        Assert.Equal(new byte[] { 2 }, latest!.AppData);
        Assert.Equal(2, calls);
        clock.Advance(30.0001);
        await node.Router.AnnounceAsync(local, pathResponse: true, tag: [1]);
        var expired = node.Interface.Sent.Last(packet => packet.PacketType == PacketType.Announce);
        Assert.NotEqual(packets[0].Payload.ToArray(), expired.Payload.ToArray());
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task PathRequestUsesTheSameEndpointTagCacheAsExplicitAnnouncements()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        using var local = Single(identity, "path-tag");
        node.Router.RegisterLocalDestination(local);
        var calls = 0;
        local.SetDefaultAppDataProvider(() => [(byte)Interlocked.Increment(ref calls)]);
        var tag = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        await node.Router.AnnounceAsync(local, tag: tag);
        var original = Assert.Single(node.Interface.Sent);
        var request = Packet.Create(PacketType.Data, DestinationType.Plain, TransportRouter.PathRequestHash,
            (byte[])[.. local.Destination.Hash, .. tag]);
        await Deliver(node, request);
        var response = Assert.Single(node.Interface.Sent, packet => packet.Context == Announce.PathResponseContext);
        Assert.Equal(original.Payload.ToArray(), response.Payload.ToArray());
        Assert.Equal(1, calls);
    }
}
