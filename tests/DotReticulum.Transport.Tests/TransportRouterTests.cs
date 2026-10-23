using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// Behaviour follows upstream RNS/Transport.py (e40191b3d193b46b7f2d8a44424a594cd758839b): packet_filter, _inbound transport forwarding,
// reverse-table proofs, path_request / path_request_handler, announce table path responses. These are in-process tests, not live interop.
public class TransportRouterTests
{
    private sealed class RecordingInterface : IPacketInterface
    {
        public List<byte[]> Sent { get; } = [];
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            lock (Sent) Sent.Add(packet.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public List<Packet> Packets()
        {
            lock (Sent) return [.. Sent.Select(b => Packet.Parse(b))];
        }

        public void Clear()
        {
            lock (Sent) Sent.Clear();
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public FakeTime Time { get; } = new(1_800_000_000);
        public RecordingInterface[] Ifaces { get; }
        public PacketInterfaceManager Manager { get; }
        public TransportRouter Router { get; private set; } = null!;

        private Rig(int count)
        {
            Ifaces = [.. Enumerable.Range(0, count).Select(_ => new RecordingInterface())];
            Manager = new PacketInterfaceManager(Ifaces);
        }

        public static async Task<Rig> CreateAsync(int count, bool transport = true)
        {
            var rig = new Rig(count);
            await rig.Manager.StartAsync();
            rig.Router = new TransportRouter(rig.Manager, new TransportRouterOptions { TransportEnabled = transport, TimeProvider = rig.Time, PathRequestGrace = TimeSpan.Zero });
            return rig;
        }

        public ValueTask Receive(RecordingInterface from, Packet packet) => Router.HandleAsync(new ReceivedPacket(packet.ToArray(), Packet.Parse(packet.ToArray()), from));

        public async ValueTask Flush()
        {
            Time.Advance(10);
            await Router.FlushAnnouncesAsync();
        }

        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }

    private static List<Packet> PathResponses(RecordingInterface iface) => [.. iface.Packets().Where(p => p.Context == Announce.PathResponseContext)];

    private static (Identity Identity, Destination Destination) NewDestination(string aspect = "router")
    {
        var identity = Identity.Generate();
        return (identity, Destination.Create(DestinationType.Single, "dotreticulum", ["tests", aspect], identity));
    }

    private static Packet WithPath(Packet announce, byte hops, byte[]? viaTransport) => viaTransport is null
        ? Packet.Create(PacketType.Announce, DestinationType.Single, announce.DestinationHash, announce.Payload, announce.Context, hops: hops, contextFlag: announce.ContextFlag)
        : Packet.Create(PacketType.Announce, DestinationType.Single, announce.DestinationHash, announce.Payload, announce.Context,
            HeaderType.Header2, viaTransport, TransportType.Transport, announce.ContextFlag, hops);

    private static Packet PathRequest(byte[] destination, byte[]? requestorTransport = null, byte[]? tag = null) =>
        Packet.Create(PacketType.Data, DestinationType.Plain, TransportRouter.PathRequestHash,
            (byte[])[.. destination, .. requestorTransport ?? [], .. tag ?? new byte[16].Select((_, i) => (byte)(i + 1)).ToArray()]);

    // Vector from upstream (pinned e40191b3): RNS.Destination.hash_from_name_and_identity("rnstransport.path.request", None).
    [Fact]
    public void PathRequestDestinationHash_MatchesUpstream() =>
        Assert.Equal("6b9f66014d9853faab220fba47d02761", Convert.ToHexStringLower(TransportRouter.PathRequestHash));

    [Fact]
    public async Task Announce_IsRebroadcastWithTransportHeader_NotBackToItsSource()
    {
        await using var rig = await Rig.CreateAsync(3);
        var (id, dest) = NewDestination();
        await rig.Receive(rig.Ifaces[0], Announce.Create(id, dest, "app"u8.ToArray()));
        await rig.Flush();

        Assert.Equal(1, rig.Router.HopsTo(dest.Hash));
        Assert.Empty(rig.Ifaces[0].Sent);
        foreach (var other in rig.Ifaces.Skip(1))
        {
            var p = Assert.Single(other.Packets());
            Assert.Equal(HeaderType.Header2, p.HeaderType);
            Assert.Equal(TransportType.Transport, p.TransportType);
            Assert.Equal(rig.Router.TransportId, p.TransportId.ToArray());
            Assert.Equal(1, p.Hops);
            Assert.Equal(PacketType.Announce, p.PacketType);
        }
    }

    [Fact]
    public async Task PathRequest_ForKnownPath_ReplaysCachedAnnounceAsPathResponse_OnlyToRequester()
    {
        await using var rig = await Rig.CreateAsync(3);
        var (id, dest) = NewDestination();
        await rig.Receive(rig.Ifaces[0], WithPath(Announce.Create(id, dest), 2, null));
        await rig.Flush();
        foreach (var i in rig.Ifaces) i.Clear();

        await rig.Receive(rig.Ifaces[1], PathRequest(dest.Hash));
        await rig.Flush();

        Assert.Empty(PathResponses(rig.Ifaces[0]));
        Assert.Empty(PathResponses(rig.Ifaces[2]));
        var response = Assert.Single(PathResponses(rig.Ifaces[1]));
        Assert.Equal(Announce.PathResponseContext, response.Context);
        Assert.Equal(PacketType.Announce, response.PacketType);
        Assert.Equal(dest.Hash, response.DestinationHash.ToArray());
        Assert.Equal(3, response.Hops);
        Assert.True(Announce.Validate(response).IsValid);

        // The same tag is not answered twice.
        rig.Ifaces[1].Clear();
        await rig.Receive(rig.Ifaces[1], PathRequest(dest.Hash));
        await rig.Flush();
        Assert.Empty(PathResponses(rig.Ifaces[1]));
    }

    [Fact]
    public async Task PathRequest_WhoseRequestorIsTheNextHop_IsNotAnswered()
    {
        await using var rig = await Rig.CreateAsync(2);
        var (id, dest) = NewDestination();
        var nextHop = new byte[16];
        nextHop.AsSpan().Fill(0x42);
        await rig.Receive(rig.Ifaces[0], WithPath(Announce.Create(id, dest), 2, nextHop));
        await rig.Flush();
        rig.Ifaces[1].Clear();

        await rig.Receive(rig.Ifaces[1], PathRequest(dest.Hash, requestorTransport: nextHop));
        await rig.Flush();
        Assert.Empty(PathResponses(rig.Ifaces[1]));
    }

    [Fact]
    public async Task PathRequest_ForLocalDestination_IsAnsweredWithPathResponseAnnounce()
    {
        await using var rig = await Rig.CreateAsync(2);
        var (id, dest) = NewDestination("local");
        rig.Router.RegisterLocalDestination(new LocalDestination(id, dest) { AppData = "hello"u8.ToArray() });

        await rig.Receive(rig.Ifaces[0], PathRequest(dest.Hash));

        Assert.Empty(rig.Ifaces[1].Sent);
        var response = Assert.Single(rig.Ifaces[0].Packets());
        Assert.Equal(Announce.PathResponseContext, response.Context);
        Assert.Equal(0, response.Hops);
        Assert.True(Announce.Validate(response).IsValid);
    }

    [Fact]
    public async Task Packet_ForUnknownTransportId_IsIgnored_AndTaglessRequestsAreDropped()
    {
        await using var rig = await Rig.CreateAsync(2);
        var (id, dest) = NewDestination();
        await rig.Receive(rig.Ifaces[0], Announce.Create(id, dest));
        rig.Ifaces[1].Clear();

        var other = new byte[16];
        await rig.Receive(rig.Ifaces[1], Packet.Create(PacketType.Data, DestinationType.Single, dest.Hash, new byte[] { 1, 2, 3 }, 0,
            HeaderType.Header2, other, TransportType.Transport, hops: 1));
        Assert.Empty(rig.Ifaces[0].Sent);

        await rig.Receive(rig.Ifaces[1], Packet.Create(PacketType.Data, DestinationType.Plain, TransportRouter.PathRequestHash, dest.Hash));
        await rig.Flush();
        Assert.Empty(PathResponses(rig.Ifaces[1]));
    }

    [Fact]
    public async Task Forward_ToMultiHopPath_ReplacesTransportIdWithNextHop_AndIncrementsHops()
    {
        await using var rig = await Rig.CreateAsync(2);
        var (id, dest) = NewDestination();
        var nextHop = new byte[16];
        nextHop.AsSpan().Fill(0x77);
        await rig.Receive(rig.Ifaces[0], WithPath(Announce.Create(id, dest), 2, nextHop));
        rig.Ifaces[0].Clear();

        var data = Packet.Create(PacketType.Data, DestinationType.Single, dest.Hash, new byte[] { 9, 8, 7, 6 }, 0,
            HeaderType.Header2, rig.Router.TransportId, TransportType.Transport, hops: 1);
        await rig.Receive(rig.Ifaces[1], data);

        var forwarded = Assert.Single(rig.Ifaces[0].Packets());
        Assert.Equal(HeaderType.Header2, forwarded.HeaderType);
        Assert.Equal(nextHop, forwarded.TransportId.ToArray());
        Assert.Equal(2, forwarded.Hops);
        Assert.Equal(data.Payload.ToArray(), forwarded.Payload.ToArray());
        Assert.Equal(data.CalculateHash(), forwarded.CalculateHash());
    }

    [Fact]
    public async Task Forward_ToDirectNeighbour_StripsToHeader1_AndProofReturnsViaReverseTable_Once()
    {
        await using var rig = await Rig.CreateAsync(3);
        var (id, dest) = NewDestination();
        await rig.Receive(rig.Ifaces[0], Announce.Create(id, dest));
        rig.Ifaces[0].Clear();

        var data = Packet.Create(PacketType.Data, DestinationType.Single, dest.Hash, new byte[] { 1, 1, 2, 3 }, 0,
            HeaderType.Header2, rig.Router.TransportId, TransportType.Transport, hops: 1);
        await rig.Receive(rig.Ifaces[1], data);

        var forwarded = Assert.Single(rig.Ifaces[0].Packets());
        Assert.Equal(HeaderType.Header1, forwarded.HeaderType);
        Assert.Equal(TransportType.Broadcast, forwarded.TransportType);
        Assert.Equal(2, forwarded.Hops);
        Assert.Equal(1, rig.Router.ReverseTableCount);

        var proof = Packet.Create(PacketType.Proof, DestinationType.Single, data.CalculateTruncatedHash(), new byte[64]);

        // A proof arriving on the wrong interface is consumed but not forwarded.
        var wrong = Packet.Create(PacketType.Proof, DestinationType.Single, data.CalculateTruncatedHash(), new byte[64], hops: 0);
        await rig.Receive(rig.Ifaces[2], wrong);
        Assert.Empty(rig.Ifaces[1].Sent);

        await rig.Receive(rig.Ifaces[0], proof);
        Assert.Empty(rig.Ifaces[1].Sent); // reverse entry was already consumed above
        Assert.Equal(0, rig.Router.ReverseTableCount);
    }

    [Fact]
    public async Task Proof_OnOutboundInterface_IsSentToTheReceivingInterface_WithIncrementedHops()
    {
        await using var rig = await Rig.CreateAsync(2);
        var (id, dest) = NewDestination();
        await rig.Receive(rig.Ifaces[0], Announce.Create(id, dest));
        var data = Packet.Create(PacketType.Data, DestinationType.Single, dest.Hash, new byte[] { 5, 5, 5 }, 0,
            HeaderType.Header2, rig.Router.TransportId, TransportType.Transport, hops: 1);
        await rig.Receive(rig.Ifaces[1], data);

        var proof = Packet.Create(PacketType.Proof, DestinationType.Single, data.CalculateTruncatedHash(), new byte[64]);
        await rig.Receive(rig.Ifaces[0], proof);

        var returned = Assert.Single(rig.Ifaces[1].Packets());
        Assert.Equal(PacketType.Proof, returned.PacketType);
        Assert.Equal(1, returned.Hops);
        Assert.Equal(proof.DestinationHash.ToArray(), returned.DestinationHash.ToArray());
    }

    [Fact]
    public async Task DuplicateDataPacket_IsForwardedOnce()
    {
        await using var rig = await Rig.CreateAsync(2);
        var (id, dest) = NewDestination();
        await rig.Receive(rig.Ifaces[0], Announce.Create(id, dest));
        rig.Ifaces[0].Clear();
        var data = Packet.Create(PacketType.Data, DestinationType.Single, dest.Hash, new byte[] { 4, 4 }, 0,
            HeaderType.Header2, rig.Router.TransportId, TransportType.Transport, hops: 1);

        await rig.Receive(rig.Ifaces[1], data);
        await rig.Receive(rig.Ifaces[1], data);
        Assert.Single(rig.Ifaces[0].Sent);
    }

    [Fact]
    public async Task PlainPacketsWithMoreThanOneHop_AreDropped()
    {
        await using var rig = await Rig.CreateAsync(1);
        var (id, dest) = NewDestination("local");
        rig.Router.RegisterLocalDestination(new LocalDestination(id, dest));
        var request = Packet.Create(PacketType.Data, DestinationType.Plain, TransportRouter.PathRequestHash, (byte[])[.. dest.Hash, .. new byte[16]], hops: 2);
        await rig.Receive(rig.Ifaces[0], request);
        Assert.Empty(rig.Ifaces[0].Sent);
    }

    [Fact]
    public async Task Send_UsesHeader2ForMultiHopPaths_DirectForNeighboursAndBroadcastWithoutPath()
    {
        await using var rig = await Rig.CreateAsync(2);
        var (id, dest) = NewDestination();
        var next = new byte[16];
        next.AsSpan().Fill(9);
        var data = Packet.Create(PacketType.Data, DestinationType.Single, dest.Hash, new byte[] { 1, 2 });

        await rig.Router.SendAsync(data);
        Assert.Single(rig.Ifaces[0].Sent);
        Assert.Single(rig.Ifaces[1].Sent);
        foreach (var i in rig.Ifaces) i.Clear();

        await rig.Receive(rig.Ifaces[0], WithPath(Announce.Create(id, dest), 2, next));
        await rig.Router.SendAsync(data);
        var multi = Assert.Single(rig.Ifaces[0].Packets());
        Assert.Equal(HeaderType.Header2, multi.HeaderType);
        Assert.Equal(next, multi.TransportId.ToArray());
        Assert.Empty(rig.Ifaces[1].Sent);
        rig.Ifaces[0].Clear();

        var (id2, dest2) = NewDestination("neighbour");
        await rig.Receive(rig.Ifaces[1], Announce.Create(id2, dest2));
        await rig.Router.SendAsync(Packet.Create(PacketType.Data, DestinationType.Single, dest2.Hash, new byte[] { 3 }));
        var direct = Assert.Single(rig.Ifaces[1].Packets(), p => p.PacketType == PacketType.Data);
        Assert.Equal(HeaderType.Header1, direct.HeaderType);
    }

    [Fact]
    public async Task PathRequest_ForUnknownDestination_IsForwardedByGatewayInterfaces_AndAnsweredWhenTheAnnounceArrives()
    {
        await using var rig = await Rig.CreateAsync(3);
        var (id, dest) = NewDestination();
        rig.Router.SetInterfaceMode(rig.Ifaces[0], InterfaceMode.Gateway);

        await rig.Receive(rig.Ifaces[0], PathRequest(dest.Hash));
        Assert.Empty(rig.Ifaces[0].Sent);
        foreach (var other in rig.Ifaces.Skip(1))
        {
            var forwarded = Assert.Single(other.Packets());
            Assert.Equal(TransportRouter.PathRequestHash, forwarded.DestinationHash.ToArray());
            Assert.Equal(dest.Hash, forwarded.Payload.ToArray()[..16]);
            Assert.Equal(48, forwarded.Payload.Length); // destination + our transport id + tag
        }

        await rig.Receive(rig.Ifaces[2], Announce.Create(id, dest));
        await rig.Flush();
        var response = Assert.Single(PathResponses(rig.Ifaces[0]));
        Assert.Equal(Announce.PathResponseContext, response.Context);
    }

    [Fact]
    public async Task PathRequest_ForUnknownDestination_IsIgnoredOnFullModeInterfaces()
    {
        await using var rig = await Rig.CreateAsync(2);
        var (_, dest) = NewDestination();
        await rig.Receive(rig.Ifaces[0], PathRequest(dest.Hash));
        Assert.Empty(rig.Ifaces[1].Sent);
    }

    [Fact]
    public async Task Cull_RemovesExpiredPaths()
    {
        await using var rig = await Rig.CreateAsync(1);
        var (id, dest) = NewDestination();
        await rig.Receive(rig.Ifaces[0], Announce.Create(id, dest));
        Assert.True(rig.Router.HasPath(dest.Hash));

        rig.Time.Advance(6 * 24 * 3600);
        rig.Router.Cull();
        Assert.True(rig.Router.HasPath(dest.Hash));

        rig.Time.Advance(2 * 24 * 3600);
        rig.Router.Cull();
        Assert.False(rig.Router.HasPath(dest.Hash));
    }

    [Fact]
    public async Task NonTransportNode_NeitherRebroadcastsNorForwardsNorAnswersFromItsPathTable()
    {
        await using var rig = await Rig.CreateAsync(2, transport: false);
        var (id, dest) = NewDestination();
        await rig.Receive(rig.Ifaces[0], Announce.Create(id, dest));
        await rig.Flush();
        Assert.Empty(rig.Ifaces[1].Sent);

        await rig.Receive(rig.Ifaces[1], PathRequest(dest.Hash));
        await rig.Flush();
        Assert.Empty(rig.Ifaces[1].Sent);
        Assert.Equal(1, rig.Router.HopsTo(dest.Hash));
    }

    [Fact]
    public async Task LocalDestination_ReceivesPacketsAddressedToIt()
    {
        await using var rig = await Rig.CreateAsync(1);
        var (id, dest) = NewDestination("local");
        byte[]? got = null;
        rig.Router.RegisterLocalDestination(new LocalDestination(id, dest) { PacketReceived = r => { got = r.Packet.Payload.ToArray(); return ValueTask.CompletedTask; } });

        await rig.Receive(rig.Ifaces[0], Packet.Create(PacketType.Data, DestinationType.Single, dest.Hash, new byte[] { 0xAB, 0xCD }));
        Assert.Equal(new byte[] { 0xAB, 0xCD }, got);
    }

    private static async Task<(PacketReceipt Receipt, Packet Proof, Rig Sender, Rig Receiver)> ProofRoundTrip(bool explicitProof, Func<Packet, Packet>? tamper = null)
    {
        var sender = await Rig.CreateAsync(1, transport: false);
        var receiver = await Rig.CreateAsync(1, transport: false);
        var (id, dest) = NewDestination("proof");
        var local = new LocalDestination(id, dest) { PacketReceived = _ => ValueTask.CompletedTask, ProofStrategy = ProofStrategy.All };
        receiver.Router.RegisterLocalDestination(local);

        var publicIdentity = Identity.FromPublicKey(id.ExportPublicKey());
        var packet = Packet.Create(PacketType.Data, DestinationType.Single, dest.Hash, publicIdentity.Encrypt("hello"u8));
        var receipt = await sender.Router.SendWithReceiptAsync(packet, publicIdentity);
        Assert.NotNull(receipt);
        Assert.Equal(PacketReceiptStatus.Sent, receipt.Status);
        Assert.Equal(1, sender.Router.PendingReceiptCount);
        await receiver.Receive(receiver.Ifaces[0], sender.Ifaces[0].Packets().Single());
        var proofPacket = receiver.Ifaces[0].Packets().Single();
        if (explicitProof)
        {
            var hash = packet.CalculateHash();
            proofPacket = Packet.Create(PacketType.Proof, DestinationType.Single, proofPacket.DestinationHash, (byte[])[.. hash, .. proofPacket.Payload.ToArray()]);
        }

        proofPacket = tamper?.Invoke(proofPacket) ?? proofPacket;
        await sender.Receive(sender.Ifaces[0], proofPacket);
        return (receipt, proofPacket, sender, receiver);
    }

    [Fact]
    public async Task SinglePacket_ProvedByLocalDestination_CompletesReceipt_ImplicitAndExplicit()
    {
        foreach (var explicitProof in new[] { false, true })
        {
            var (receipt, proof, sender, receiver) = await ProofRoundTrip(explicitProof);
            await using var _ = sender;
            await using var __ = receiver;
            Assert.Equal(PacketType.Proof, proof.PacketType);
            Assert.Equal(explicitProof ? 96 : 64, proof.Payload.Length);
            Assert.True(receipt.Completion.IsCompletedSuccessfully);
            Assert.Equal(PacketReceiptStatus.Delivered, receipt.Status);
            Assert.Equal(0, sender.Router.PendingReceiptCount);
        }
    }

    [Fact]
    public async Task SinglePacket_ProofWithBadSignature_IsIgnored_AndReceiptTimesOut()
    {
        var (receipt, _, sender, receiver) = await ProofRoundTrip(false, p =>
        {
            var data = p.Payload.ToArray();
            data[10] ^= 0xFF;
            return Packet.Create(PacketType.Proof, DestinationType.Single, p.DestinationHash, data);
        });
        await using var _ = sender;
        await using var __ = receiver;
        Assert.False(receipt.Completion.IsCompleted);
        Assert.Equal(6 + (6 * Packet.HopLimit), receipt.Timeout);
        Assert.False(receipt.Proved);
        Assert.Null(receipt.ProofPacket);
        Assert.Null(receipt.ConcludedAt);
        sender.Time.Advance(checked((long)Math.Ceiling(receipt.Timeout)) + 1);
        sender.Router.Cull();
        Assert.True(receipt.Completion.IsCompletedSuccessfully);
        Assert.False(await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(PacketReceiptStatus.Failed, receipt.Status);
        Assert.False(receipt.Proved);
        Assert.Null(receipt.ProofPacket);
        Assert.Null(receipt.Rtt);
        Assert.Equal(sender.Router.Links.UnixNow, receipt.ConcludedAt);
        Assert.Equal(0, sender.Router.PendingReceiptCount);
        var concludedAt = receipt.ConcludedAt;
        sender.Time.Advance(1);
        sender.Router.Cull();
        Assert.Equal(concludedAt, receipt.ConcludedAt);
    }
}
