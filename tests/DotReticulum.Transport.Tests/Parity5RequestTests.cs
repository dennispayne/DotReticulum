using System.Collections.Concurrent;
using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// Pinned Link.py:484-529,858-887,1319-1470; GenerateParity5RequestVectors.py
// executes request settlement and the bundled MessagePack small-response size domain.
[Trait("Category", "Unit")]
public sealed class Parity5RequestTests
{
    [Fact]
    public async Task ImmediateManagerStopJoinsFreshWorkersBeforeDisposingTheirCancellationSource()
    {
        using var identity = Identity.Generate();
        for (var iteration = 0; iteration < 32; iteration++)
        {
            await using var node = await Parity4LinkMtuTests.Harness.Create(clock: new Parity4LinkMtuTests.Clock());
            node.Router.Links.Connect(identity, new byte[16]);
            await node.Router.Links.StopAsync();
            Assert.Empty(node.Router.Links.Links);
            Assert.Equal(0, node.Router.Links.QueuedOutboundPackets);
        }
    }

    [Fact]
    public async Task NewLinksCaptureValidatedManagerResourceDefaultsWithoutChangingExistingOverrides()
    {
        await using var topology = await LinkTests.BuildAsync(false);
        var outgoing = new ResourcePolicy { MemoryThreshold = 123 };
        var incoming = new ResourcePolicy { MemoryThreshold = 456 };
        topology.Initiator.Router.Links.DefaultResourcePolicy = outgoing;
        topology.Responder.Router.Links.DefaultResourcePolicy = incoming;
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        topology.Destination.LinkEstablished = link => accepted.TrySetResult(link);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = topology.Initiator.Router.Links.Connect(topology.Destination.Identity,
            topology.Destination.Destination.Hash, link => established.TrySetResult(link));
        await LinkTests.Within(established.Task);
        var receiver = await LinkTests.Within(accepted.Task);
        Assert.Same(outgoing, sender.ResourcePolicy);
        Assert.Same(incoming, receiver.ResourcePolicy);
        var replacement = outgoing with { MemoryThreshold = 789 };
        topology.Initiator.Router.Links.DefaultResourcePolicy = replacement;
        Assert.Same(outgoing, sender.ResourcePolicy);
        receiver.ResourcePolicy = incoming with { MemoryThreshold = 42 };
        topology.Responder.Router.Links.DefaultResourcePolicy = outgoing;
        Assert.Equal(42, receiver.ResourcePolicy.MemoryThreshold);
        Assert.Throws<ArgumentNullException>(() => topology.Initiator.Router.Links.DefaultResourcePolicy = null!);
        Assert.Throws<ArgumentOutOfRangeException>(() => topology.Initiator.Router.Links.DefaultResourcePolicy = outgoing with { MaxSegments = 0 });
        Assert.Same(replacement, topology.Initiator.Router.Links.DefaultResourcePolicy);
        accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = topology.Initiator.Router.Links.Connect(topology.Destination.Identity,
            topology.Destination.Destination.Hash, link => established.TrySetResult(link));
        await LinkTests.Within(established.Task);
        Assert.Same(replacement, second.ResourcePolicy);
        Assert.Same(outgoing, (await LinkTests.Within(accepted.Task)).ResourcePolicy);
    }

    [Fact]
    public async Task ChannelResendReencryptsSameEnvelopeRejectsOtherOwnerAndDoesNotResendDeliveredReceipt()
    {
        await using var node = await Parity4LinkMtuTests.Harness.Create();
        var link = Parity4LinkMtuTests.FixedInitiator(node);
        var other = Parity4LinkMtuTests.FixedInitiator(node);
        var sent = link.SendWithContext("envelope"u8, LinkContext.Channel, createReceipt: true, encrypt: true)!;
        Assert.True(sent.CoreCallbacks);
        Assert.Throws<ArgumentException>(() => other.ResendChannelPacket(sent));
        sent.Conclude(PacketReceiptStatus.Failed, node.Router.Links.UnixNow);
        var replacement = link.ResendChannelPacket(sent)!;
        Assert.NotSame(sent, replacement);
        Assert.NotSame(sent.SentPacket, replacement.SentPacket);
        Assert.NotEqual(sent.Hash, replacement.Hash);
        Assert.Equal("envelope"u8.ToArray(), link.Decrypt(replacement.SentPacket!.Payload.Span));
        Assert.Equal(sent.Timeout, replacement.Timeout);
        Assert.True(replacement.CoreCallbacks);
        replacement.Conclude(PacketReceiptStatus.Delivered, node.Router.Links.UnixNow);
        Assert.Same(replacement, link.ResendChannelPacket(replacement));
        var cost = link.EstablishmentCost;
        link.ProvePacket(replacement.SentPacket!);
        Assert.Equal(cost, link.EstablishmentCost);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30000)]
    public async Task LosslessApplicationValuesRoundtripThroughPacketAndResourceRequestsWithoutChangingLegacyView(int padding)
    {
        await using var topology = await LinkTests.BuildAsync(false);
        topology.Destination.RegisterRequestHandler("/values", context => context.Data, RequestPolicy.All,
            useApplicationValues: true);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topology.Initiator.Router.Links.Connect(topology.Destination.Identity,
            topology.Destination.Destination.Hash, active => established.TrySetResult(active));
        await LinkTests.Within(established.Task);
        var value = new Dictionary<object, object?>
        {
            [new byte[] { 0xA1 }] = "binary",
            ["A1"] = "text",
            ["extension"] = new MsgPackExtension(12, [1, 2, 3]),
            ["timestamp"] = new MsgPackTimestamp(1_000_000, 123_456_789),
            ["padding"] = new byte[padding],
        };
        var request = link.Request("/values", value, timeout: 30, useApplicationValues: true)!;
        var response = Assert.IsType<Dictionary<object, object?>>(await LinkTests.Within(request.ResponseTask, 30));
        Assert.True(request.UseApplicationValues);
        Assert.Equal("binary", response[new byte[] { 0xA1 }]);
        Assert.Equal("text", response["A1"]);
        Assert.Equal(value["extension"], response["extension"]);
        Assert.Equal(value["timestamp"], response["timestamp"]);
        Assert.Equal(new byte[padding], Assert.IsType<byte[]>(response["padding"]));
        var legacyCollision = link.Request("/values", value, timeout: 30)!;
        await Assert.ThrowsAsync<TimeoutException>(() => legacyCollision.ResponseTask.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(RequestStatus.Failed, legacyCollision.Status);
        var binaryOnly = new Dictionary<object, object?> { [new byte[] { 0xA1 }] = "binary" };
        var wireSize = MsgPack.Pack(binaryOnly).LongLength - 2;
        var legacy = link.Request("/values", binaryOnly, timeout: 30, maxResponseSize: wireSize)!;
        var projected = Assert.IsType<Dictionary<object, object?>>(await LinkTests.Within(legacy.ResponseTask));
        Assert.Equal("binary", projected["A1"]);
        Assert.Equal(wireSize, legacy.ResponseSize);
    }

    [Fact]
    public async Task MonotonicAgeInactivityAndHandshakeRateMatchReferenceIncludingKeepalive()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity5-request-vectors.json")));
        var expected = vectors.RootElement.GetProperty("timing");
        var clock = new Parity4LinkMtuTests.Clock();
        await using var node = await Parity4LinkMtuTests.Harness.Create(clock: clock);
        var link = Parity4LinkMtuTests.FixedInitiator(node);
        Assert.Equal(0, link.Age);
        clock.Advance(3);
        link.Send("data"u8, createReceipt: false);
        clock.Advance(2);
        link.SendWithContext([0xFF], LinkContext.Keepalive, createReceipt: false, encrypt: false);
        clock.Advance(2);
        Assert.Equal(expected.GetProperty("age").GetDouble(), link.Age);
        Assert.Equal(expected.GetProperty("noInboundFor").GetDouble(), link.NoInboundFor);
        Assert.Equal(expected.GetProperty("noOutboundFor").GetDouble(), link.NoOutboundFor);
        Assert.Equal(expected.GetProperty("noDataFor").GetDouble(), link.NoDataFor);
        Assert.Equal(expected.GetProperty("inactiveFor").GetDouble(), link.InactiveFor);
        Assert.Equal(expected.GetProperty("establishmentRate").GetDouble(), link.EstablishmentRate);
    }

    [Fact]
    public async Task CompletionMatchesReferenceProgressAndPacketReceiptCouplingEvenWhenProgressObserverThrows()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity5-request-vectors.json")));
        await using var node = await Parity4LinkMtuTests.Harness.Create();
        var link = Parity4LinkMtuTests.FixedInitiator(node);
        var events = new ConcurrentQueue<string>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = link.Request("/test", timeout: 30, maxResponseSize: 100,
            progressCallback: r =>
            {
                events.Enqueue($"progress:{r.Progress:g}");
                throw new InvalidOperationException("deliberate observer failure");
            },
            responseCallback: _ => { events.Enqueue("response"); observed.TrySetResult(); })!;
        Assert.Equal(100, request.MaxResponseSize);
        Assert.Equal(30, request.PacketReceipt!.Timeout);
        request.PacketReceipt.Delivered = _ => events.Enqueue("delivered");
        link.HandleResponse(request.RequestId, "answer"u8.ToArray(), responseSize: 6, transferSize: 6, checkSize: true);
        Assert.Equal("answer"u8.ToArray(), await request.ResponseTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(vectors.RootElement.GetProperty("success").GetProperty("events").EnumerateArray().Select(v => v.GetString()), events);
        Assert.Equal(RequestStatus.Ready, request.Status);
        Assert.Equal(1, request.Progress);
        Assert.True(await request.PacketReceipt.Completion);
        Assert.True(request.PacketReceipt.Proved);
        link.HandleResponse(request.RequestId, "duplicate");
        request.Cancel();
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task SmallResponseLimitUsesPinnedSerializedSizeAndFailsExactlyOnce()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity5-request-vectors.json")));
        await using var node = await Parity4LinkMtuTests.Harness.Create();
        var link = Parity4LinkMtuTests.FixedInitiator(node);
        var failures = 0;
        foreach (var vector in vectors.RootElement.GetProperty("smallResponseSizes").EnumerateArray())
        {
            var response = MsgPack.Unpack(Convert.FromHexString(vector.GetProperty("responseHex").GetString()!));
            var size = MsgPack.Pack(response).LongLength - 2;
            Assert.Equal(vector.GetProperty("size").GetInt64(), size);
            var request = link.Request("/size", timeout: 30, maxResponseSize: 0, failedCallback: _ => Interlocked.Increment(ref failures))!;
            link.HandleResponse(request.RequestId, response, responseSize: size, transferSize: size, checkSize: true);
            if (size <= 0)
            {
                await request.ResponseTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(RequestStatus.Ready, request.Status);
            }
            else
            {
                await Assert.ThrowsAsync<TimeoutException>(() => request.ResponseTask);
                request.Cancel();
                Assert.Equal(RequestStatus.Failed, request.Status);
            }
        }
        await LinkTests.Until(() => Volatile.Read(ref failures) == 3);
    }

    [Fact]
    public async Task CancellationSettlesTaskAndPacketWithoutClosingLink()
    {
        await using var node = await Parity4LinkMtuTests.Harness.Create();
        var link = Parity4LinkMtuTests.FixedInitiator(node);
        using var cancellation = new CancellationTokenSource();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = link.Request("/cancel", timeout: 30, cancellationToken: cancellation.Token, failedCallback: _ => failed.TrySetResult())!;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.ResponseTask);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await request.PacketReceipt!.Completion);
        Assert.Equal(RequestStatus.Failed, request.Status);
        Assert.Equal(LinkStatus.Active, link.Status);
        link.HandleResponse(request.RequestId, "late response");
        Assert.Equal(RequestStatus.Failed, request.Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => link.Request("/bad", timeout: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => link.Request("/bad", maxResponseSize: -1));
        Assert.Throws<OperationCanceledException>(() => link.Request("/bad", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task RequestWorkIsBoundedAndStalledObserversDoNotBlockProtocolSettlementOrShutdown()
    {
        await using var node = await Parity4LinkMtuTests.Harness.Create();
        var link = Parity4LinkMtuTests.FixedInitiator(node);
        using var release = new ManualResetEventSlim();
        try
        {
            for (var i = 0; i < LinkManager.RequestConcurrency; i++)
                Assert.True(node.Router.Links.DispatchRequest(() => release.Wait()));
            await LinkTests.Until(() => node.Router.Links.ExecutingApplicationWork == LinkManager.RequestConcurrency);
            for (var i = 0; i < LinkManager.WorkQueueCapacity; i++)
                Assert.True(node.Router.Links.DispatchRequest(() => { }));
            Assert.False(node.Router.Links.DispatchRequest(() => throw new InvalidOperationException("must not run")));
            Assert.Equal(LinkManager.WorkQueueCapacity, node.Router.Links.QueuedRequests);
            var response = link.Request("/independent", timeout: 30)!;
            link.HandleResponse(response.RequestId, "settled");
            Assert.Equal("settled", await response.ResponseTask.WaitAsync(TimeSpan.FromSeconds(5)));
            await node.Router.Links.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(LinkStatus.Closed, link.Status);
            Assert.Equal(0, node.Router.Links.QueuedRequests);
            Assert.InRange(node.Router.Links.ExecutingApplicationWork, 0, LinkManager.RequestConcurrency);
        }
        finally { release.Set(); }
        await LinkTests.Until(() => node.Router.Links.ExecutingApplicationWork == 0);
    }

    [Fact]
    public async Task PendingRequestsAreBoundedAndReleaseCapacityOnCancellation()
    {
        await using var node = await Parity4LinkMtuTests.Harness.Create();
        var link = Parity4LinkMtuTests.FixedInitiator(node);
        var requests = Enumerable.Range(0, Link.MaximumPendingRequests).Select(_ => link.Request("/pending", timeout: 30)!).ToArray();
        Assert.Throws<InvalidOperationException>(() => link.Request("/overflow", timeout: 30));
        requests[0].Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requests[0].ResponseTask);
        Assert.NotNull(link.Request("/recovered", timeout: 30));
        link.Teardown();
        foreach (var request in requests.Skip(1))
            await Assert.ThrowsAsync<TimeoutException>(() => request.ResponseTask);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncHandlerIsCanceledOnLinkCloseAndDisposesDiscardedOwnedStream(bool leaveOpen)
    {
        await using var topology = await LinkTests.BuildAsync(false);
        using var source = new MemoryStream(new byte[42]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        topology.Destination.RegisterAsyncRequestHandler("/owned", async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.TrySetResult(); }
            return new StreamFileResponse(source, LeaveOpen: leaveOpen);
        }, RequestPolicy.All);
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        topology.Destination.LinkEstablished = link => accepted.TrySetResult(link);
        var sender = topology.Initiator.Router.Links.Connect(topology.Destination.Identity,
            topology.Destination.Destination.Hash, link => established.TrySetResult(link));
        await LinkTests.Within(established.Task);
        var responder = await LinkTests.Within(accepted.Task);
        var request = sender.Request("/owned", timeout: 30)!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        responder.Teardown();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await LinkTests.Until(() => topology.Responder.Router.Links.ExecutingApplicationWork == 0);
        Assert.Equal(leaveOpen, source.CanRead);
        await Assert.ThrowsAsync<TimeoutException>(() => request.ResponseTask);
        using var rejected = new MemoryStream(new byte[1]);
        Assert.False(responder.Respond(new byte[16], new StreamFileResponse(rejected)));
        Assert.False(rejected.CanRead);
    }
}
