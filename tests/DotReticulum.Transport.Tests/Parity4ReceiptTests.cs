using DotReticulum.Core;
using static DotReticulum.Transport.Tests.Parity4LinkMtuTests;

namespace DotReticulum.Transport.Tests;

// Packet.py:399-610, Transport.py:745-759,1358-1369,3194-3197 at
// a29bfb51c468945c96a1ee22cae0114e993e2391. Expected predicates, proofs and
// timeout boundaries execute upstream in GenerateParity4ReceiptLinkMtuVectors.py.
[Trait("Category", "Unit")]
public class Parity4ReceiptTests
{
    [Fact]
    public void All4096PacketTypeDestinationAndContextCombinationsMatchReference()
    {
        var expected = Vectors.GetProperty("eligibility");
        var index = 0;
        foreach (var packetType in Enum.GetValues<PacketType>())
        {
            foreach (var destinationType in Enum.GetValues<DestinationType>())
            {
                var row = expected[index++];
                for (var context = 0; context < 256; context++)
                {
                    var packet = Packet.Create(packetType, destinationType, new byte[16], new byte[] { 1 }, (byte)context);
                    Assert.Equal(row[context].GetBoolean(), PacketReceipt.IsEligible(packet));
                }
            }
        }
    }

    [Fact]
    public async Task IneligiblePacketsAreSentWithoutTrackingAReceipt()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        foreach (var context in new byte[] { LinkContext.Resource, LinkContext.ResourceRcl, LinkContext.Keepalive, LinkContext.LrProof })
        {
            var packet = Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], new byte[] { 1 }, context);
            Assert.Null(await node.Router.SendWithReceiptAsync(packet, identity));
        }
        Assert.Null(await node.Router.SendWithReceiptAsync(Packet.Create(PacketType.Announce, DestinationType.Single,
            new byte[16], new byte[] { 1 }), identity));
        Assert.Equal(0, node.Router.PendingReceiptCount);
        Assert.Equal(5, node.Interface.Sent.Count);
    }

    [Fact]
    public async Task ExplicitAndImplicitProofSizesAndTerminalMetadataMatchReference()
    {
        foreach (var vector in Vectors.GetProperty("proofs").EnumerateArray().Where(entry => !entry.GetProperty("link").GetBoolean()))
        {
            await using var node = await Harness.Create();
            using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
            var packet = Packet.Parse(Hex(Vectors, "receiptPacketRaw"));
            var receipt = await node.Router.SendWithReceiptAsync(packet, identity, TimeSpan.FromSeconds(30));
            Assert.NotNull(receipt);
            var callbackCount = 0;
            receipt.Delivered = _ => { callbackCount++; throw new InvalidOperationException("delivery observer"); };
            var proof = Packet.Create(PacketType.Proof, DestinationType.Single, packet.CalculateTruncatedHash(), Hex(vector, "proof"));
            await node.Router.HandleAsync(Received(proof, node.Interface));
            var accepted = vector.GetProperty("accepted").GetBoolean();
            Assert.Equal(accepted, receipt.Proved);
            Assert.Equal((PacketReceiptStatus)vector.GetProperty("status").GetByte(), receipt.Status);
            Assert.Equal(accepted ? 1 : 0, callbackCount);
            if (accepted)
            {
                Assert.True(await receipt.Completion);
                Assert.Same(proof, receipt.ProofPacket);
                Assert.NotNull(receipt.ConcludedAt);
                Assert.True(receipt.Rtt >= 0);
                Assert.Equal(receipt.Hash[..16], receipt.TruncatedHash);
                await node.Router.HandleAsync(Received(proof, node.Interface));
                Assert.Equal(1, callbackCount);
            }
            else
            {
                Assert.Null(receipt.ConcludedAt);
                Assert.Null(receipt.ProofPacket);
                Assert.False(receipt.Completion.IsCompleted);
            }
        }
    }

    [Fact]
    public async Task LinkProofPrefixSizesIncludingReferenceTrailingBytesMatchReference()
    {
        foreach (var vector in Vectors.GetProperty("proofs").EnumerateArray().Where(entry => entry.GetProperty("link").GetBoolean()))
        {
            await using var node = await Harness.Create(clock: new Clock());
            var link = FixedInitiator(node);
            var source = Packet.Parse(Hex(Vectors, "linkReceiptPacketRaw"));
            var receipt = link.SendWithContext(source.Payload.Span, LinkContext.None, createReceipt: true, encrypt: false);
            Assert.NotNull(receipt);
            var proof = Packet.Create(PacketType.Proof, DestinationType.Link, link.LinkId, Hex(vector, "proof"));
            link.Receive(Received(proof, node.Interface));
            Assert.Equal(vector.GetProperty("accepted").GetBoolean(), receipt.Proved);
            Assert.Equal((PacketReceiptStatus)vector.GetProperty("status").GetByte(), receipt.Status);
            Assert.Equal(1.5, receipt.Timeout, 6);
            if (receipt.Proved)
            {
                Assert.True(await receipt.Completion);
                Assert.Same(proof, receipt.ProofPacket);
                Assert.NotNull(receipt.ConcludedAt);
            }
        }
    }

    [Fact]
    public async Task DefaultTimeoutUsesBitrateAndUnknownHopLimit()
    {
        foreach (var entry in Vectors.GetProperty("firstHopTimeouts").EnumerateArray())
        {
            await using var node = await Harness.Create(new Probe { Bitrate = entry.GetProperty("bitrate").GetInt64() });
            using var identity = Identity.Generate();
            var destination = Destination.Create(DestinationType.Single, "parity4", ["receipt"], identity);
            await node.Router.HandleAsync(Received(Announce.Create(identity, destination), node.Interface));
            Assert.Equal(entry.GetProperty("timeout").GetDouble(), node.Router.FirstHopTimeout(destination.Hash), 6);
            var receipt = await node.Router.SendWithReceiptAsync(Packet.Create(PacketType.Data, DestinationType.Single,
                destination.Hash, new byte[] { 1 }), identity);
            Assert.NotNull(receipt);
            Assert.Equal(entry.GetProperty("timeout").GetDouble() + 6, receipt.Timeout, 6);
            var unknown = await node.Router.SendWithReceiptAsync(Packet.Create(PacketType.Data, DestinationType.Single,
                new byte[16], new byte[] { 2 }), identity);
            Assert.NotNull(unknown);
            Assert.Equal(6 + (128 * 6), unknown.Timeout);
        }
    }

    [Fact]
    public async Task StrictTimeoutBoundaryHasOneTerminalTimestampAndOneCallback()
    {
        var clock = new Clock();
        await using var node = await Harness.Create(clock: clock);
        using var identity = Identity.Generate();
        var receipt = await node.Router.SendWithReceiptAsync(Packet.Create(PacketType.Data, DestinationType.Single,
            new byte[16], new byte[] { 1 }), identity, TimeSpan.FromSeconds(2));
        Assert.NotNull(receipt);
        var timedOut = 0;
        receipt.TimedOut = _ => { timedOut++; throw new InvalidOperationException("timeout observer"); };
        clock.Advance(2);
        node.Router.Cull();
        Assert.Equal((PacketReceiptStatus)Vectors.GetProperty("timeoutBoundaries")[0].GetProperty("status").GetByte(), receipt.Status);
        clock.Advance(0.001);
        node.Router.Cull();
        Assert.Equal((PacketReceiptStatus)Vectors.GetProperty("timeoutBoundaries")[1].GetProperty("status").GetByte(), receipt.Status);
        Assert.False(await receipt.Completion);
        Assert.Equal(receipt.SentAt + 2.001, receipt.ConcludedAt!.Value, 3);
        Assert.False(receipt.Proved);
        Assert.Null(receipt.ProofPacket);
        node.Router.Cull();
        Assert.Equal(1, timedOut);
    }

    [Fact]
    public async Task Global1024LimitCullsOldestAcrossSingleAndLinkReceipts()
    {
        var clock = new Clock();
        await using var node = await Harness.Create(clock: clock);
        using var identity = Identity.Generate();
        var oldest = await node.Router.SendWithReceiptAsync(Packet.Create(PacketType.Data, DestinationType.Single,
            new byte[16], BitConverter.GetBytes(-1)), identity);
        Assert.NotNull(oldest);
        var culled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oldest.TimedOut = _ => { culled.TrySetResult(); throw new InvalidOperationException("culling observer"); };
        var link = FixedInitiator(node);
        for (var i = 0; i < TransportRouter.MaximumReceipts; i++)
        {
            await LinkTests.Until(() => node.Router.Links.QueuedOutboundPackets < LinkManager.WorkQueueCapacity);
            Assert.NotNull(link.Send(BitConverter.GetBytes(i)));
        }
        await culled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PacketReceiptStatus.Culled, oldest.Status);
        Assert.Equal(-1, oldest.Timeout);
        Assert.False(await oldest.Completion);
        Assert.NotNull(oldest.ConcludedAt);
        Assert.False(oldest.Proved);
        Assert.Equal(0, node.Router.PendingReceiptCount);
    }

    [Fact]
    public async Task CancellationAndSendFailureDoNotLeavePendingReceipts()
    {
        await using var node = await Harness.Create();
        using var identity = Identity.Generate();
        var packet = Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], new byte[] { 1 });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await node.Router.SendWithReceiptAsync(packet, identity, cancellationToken: cancellation.Token));
        Assert.Equal(0, node.Router.PendingReceiptCount);
        Assert.Empty(node.Interface.Sent);
        node.Interface.ThrowOnSend = true;
        await Assert.ThrowsAsync<IOException>(async () => await node.Router.SendWithReceiptAsync(packet, identity));
        Assert.Equal(0, node.Router.PendingReceiptCount);
    }

    [Fact]
    public async Task UpdatedLinkReceiptTimeoutIsObservedByWatchdogAndLoggingCannotBreakSettlement()
    {
        var clock = new Clock();
        await using var node = await Harness.Create(clock: clock);
        var link = FixedInitiator(node);
        node.Router.Log = _ => throw new InvalidOperationException("diagnostic observer");
        var receipt = link.Send(new byte[] { 1 });
        Assert.NotNull(receipt);
        await LinkTests.Until(() => node.Interface.Sent.Any(packet => packet.CalculateHash().SequenceEqual(receipt.Hash)));
        receipt.Timeout = 3;
        receipt.TimedOut = _ => throw new InvalidOperationException("timeout observer");
        clock.Advance(2);
        link.Tick(node.Router.Links.Now);
        Assert.Equal(PacketReceiptStatus.Sent, receipt.Status);
        clock.Advance(1.001);
        link.Tick(node.Router.Links.Now);
        Assert.False(await receipt.Completion);
        Assert.Equal(PacketReceiptStatus.Failed, receipt.Status);
        Assert.NotNull(receipt.ConcludedAt);
        Assert.Equal(LinkStatus.Active, link.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneProofSettlesEveryIdenticalPacketReceiptAsReferenceDoes(bool linkCase)
    {
        await using var node = await Harness.Create(clock: new Clock());
        using var identity = Identity.FromPrivateKey(Hex(Vectors, "destinationPrivateKey"));
        var expected = Vectors.GetProperty("duplicates").EnumerateArray().Single(entry => entry.GetProperty("link").GetBoolean() == linkCase);
        var proofData = Vectors.GetProperty("proofs").EnumerateArray()
            .Single(entry => entry.GetProperty("link").GetBoolean() == linkCase && entry.GetProperty("label").GetString() == "explicit");
        PacketReceipt? first;
        PacketReceipt? second;
        Packet proof;
        Link? link = null;
        if (linkCase)
        {
            link = FixedInitiator(node);
            var packet = Packet.Parse(Hex(Vectors, "linkReceiptPacketRaw"));
            first = link.SendWithContext(packet.Payload.Span, LinkContext.None, createReceipt: true, encrypt: false);
            second = link.SendWithContext(packet.Payload.Span, LinkContext.None, createReceipt: true, encrypt: false);
            proof = Packet.Create(PacketType.Proof, DestinationType.Link, link.LinkId, Hex(proofData, "proof"));
        }
        else
        {
            var packet = Packet.Parse(Hex(Vectors, "receiptPacketRaw"));
            first = await node.Router.SendWithReceiptAsync(packet, identity);
            second = await node.Router.SendWithReceiptAsync(packet, identity);
            proof = Packet.Create(PacketType.Proof, DestinationType.Single, packet.CalculateTruncatedHash(), Hex(proofData, "proof"));
            Assert.Equal(2, node.Router.PendingReceiptCount);
        }
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(PacketReceiptStatus.Sent, first.Status);
        Assert.Equal(PacketReceiptStatus.Sent, second.Status);
        if (link is not null) link.Receive(Received(proof, node.Interface));
        else await node.Router.HandleAsync(Received(proof, node.Interface));
        Assert.True(await first.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal((PacketReceiptStatus)expected.GetProperty("statuses")[0].GetByte(), first.Status);
        Assert.Equal((PacketReceiptStatus)expected.GetProperty("statuses")[1].GetByte(), second.Status);
        Assert.Equal(expected.GetProperty("remaining").GetInt32(), node.Router.PendingReceiptCount);
    }
}
