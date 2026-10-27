using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Transport.Tests;

// Vectors produced by executing upstream RNS.Link (see GenerateLinkVectors.py). Both ephemeral keys are fixed, so the .NET responder must produce
// the byte-identical LRPROOF and derive the identical link key; the .NET initiator must emit the identical LINKREQUEST and accept the upstream proof.
[Trait("Category", "Unit")]
public class LinkVectorTests
{
    private sealed class Capture : IPacketInterface
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

        public async Task<byte[]> NextAsync(Func<Packet, bool> match)
        {
            for (var i = 0; i < 500; i++)
            {
                lock (Sent)
                {
                    foreach (var raw in Sent) if (match(Packet.Parse(raw))) return raw;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException();
        }
    }

    private static JsonElement Root { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "link-vectors.json"))).RootElement;

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    private static JsonElement Request(string label) => Root.GetProperty("requests").EnumerateArray().Single(r => r.GetProperty("label").GetString() == label);

    private static ReceivedPacket Received(byte[] raw, IPacketInterface source) => new(raw, Packet.Parse(raw), source);

    private static async Task<(TransportRouter Router, Capture Iface, PacketInterfaceManager Manager)> NewRouterAsync()
    {
        var iface = new Capture();
        var manager = new PacketInterfaceManager([iface]);
        await manager.StartAsync();
        return (new TransportRouter(manager), iface, manager);
    }

    [Fact]
    public void LinkId_MatchesUpstream_WithAndWithoutSignalling()
    {
        foreach (var label in new[] { "mtu500", "mtu262144", "nosignalling" })
        {
            var r = Request(label);
            Assert.Equal(Hex(r, "linkId"), Link.LinkIdFromRequest(Packet.Parse(Hex(r, "requestRaw"))));
        }
    }

    [Fact]
    public async Task Initiator_EmitsUpstreamRequest_AndAcceptsUpstreamProof()
    {
        var (router, iface, manager) = await NewRouterAsync();
        await using var _ = manager;
        var r = Request("mtu500");
        var identity = Identity.FromPrivateKey(Hex(Root, "destinationPrivateKey"));
        var link = Link.CreateInitiator(router.Links, Identity.FromPublicKey(identity.ExportPublicKey()), Hex(Root, "destinationHash"), 1, 30,
            Hex(Root, "initiatorX25519Private"), Hex(Root, "initiatorSigSeed"));

        Assert.Equal(Hex(r, "requestRaw"), link.RequestPacket!.ToArray());
        Assert.Equal(Hex(r, "linkId"), link.LinkId);

        link.RequestTime = router.Links.Now - 0.05;
        Assert.True(link.ValidateProof(Received(Hex(r, "proofRaw"), iface)));
        Assert.Equal(LinkStatus.Active, link.Status);
        Assert.Equal(Hex(r, "derivedKey"), link.DerivedKeyForTests);
        Assert.Equal(500, link.Mtu);
        Assert.Equal(431, link.Mdu);
        Assert.NotNull(link.Rtt);

        // LRRTT sent by us must be accepted by upstream's reader: encrypted msgpack float with context 0xFE
        var rttRaw = await iface.NextAsync(p => p.Context == LinkContext.LrRtt);
        var rtt = Packet.Parse(rttRaw);
        Assert.Equal(Hex(r, "linkId"), rtt.DestinationHash.ToArray());
        Assert.Equal(link.Rtt!.Value, MsgPack.ToDouble(MsgPack.Unpack(link.Decrypt(rtt.Payload.Span)!)), 6);
    }

    [Fact]
    public async Task Initiator_ProcessesUpstreamEncryptedPackets()
    {
        var (router, iface, manager) = await NewRouterAsync();
        await using var _ = manager;
        var r = Request("mtu500");
        var s = r.GetProperty("samples");
        var link = Link.CreateInitiator(router.Links, Identity.FromPublicKey(Identity.FromPrivateKey(Hex(Root, "destinationPrivateKey")).ExportPublicKey()),
            Hex(Root, "destinationHash"), 1, 30, Hex(Root, "initiatorX25519Private"), Hex(Root, "initiatorSigSeed"));
        link.RequestTime = router.Links.Now;
        Assert.True(link.ValidateProof(Received(Hex(r, "proofRaw"), iface)));

        var data = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.PacketReceived = (d, _) => data.TrySetResult(d);
        link.Closed = l => closed.TrySetResult(l);

        link.Receive(Received(Hex(s.GetProperty("data"), "raw"), iface));
        Assert.Equal(Hex(s.GetProperty("data"), "plaintext"), await data.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        link.Receive(Received(Hex(s.GetProperty("keepalive_pong"), "raw"), iface));
        Assert.Equal(1, link.KeepalivesReceived);

        link.Receive(Received(Hex(s.GetProperty("linkclose"), "raw"), iface));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LinkStatus.Closed, link.Status);
        Assert.Equal(LinkTeardownReason.DestinationClosed, link.TeardownReason);
    }

    [Theory]
    [InlineData("mtu500", 500)]
    [InlineData("mtu262144", 500)]
    [InlineData("nosignalling", 500)]
    public async Task Responder_ProducesByteIdenticalProofAndKey(string label, int expectedMtu)
    {
        var (router, iface, manager) = await NewRouterAsync();
        await using var _ = manager;
        var r = Request(label);
        var identity = Identity.FromPrivateKey(Hex(Root, "destinationPrivateKey"));
        var local = new LocalDestination(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["vectors", "link"], identity)) { AcceptLinks = true };
        Assert.Equal(Hex(Root, "destinationHash"), Identity.TruncatedHash(
            [.. Destination.Create(DestinationType.Single, "dotreticulum", ["vectors", "link"], identity).NameHash.ToArray(), .. identity.Hash]));

        var link = Link.ValidateRequest(router.Links, local, Received(Hex(r, "requestRaw"), iface), Hex(Root, "responderX25519Private"));
        Assert.NotNull(link);
        Assert.Equal(expectedMtu, link.Mtu);
        Assert.Equal(Hex(r, "linkId"), link.LinkId);
        if (label == "mtu500")
        {
            Assert.Equal(Hex(r, "derivedKey"), link.DerivedKeyForTests);
            Assert.Equal(Hex(r, "proofRaw"), await iface.NextAsync(p => p.Context == LinkContext.LrProof));
        }
        else if (label == "mtu262144")
        {
            // Upstream echoes the requested MTU; we clamp to our fixed MTU and sign what we actually echo, so only the layout is compared.
            var proof = Packet.Parse(await iface.NextAsync(p => p.Context == LinkContext.LrProof));
            Assert.Equal(99, proof.Payload.Length);
            Assert.Equal(Hex(r, "linkId"), proof.DestinationHash.ToArray());
        }
        else
        {
            Assert.Equal(99, Packet.Parse(Hex(r, "proofRaw")).Payload.Length);
            var proof = Packet.Parse(await iface.NextAsync(p => p.Context == LinkContext.LrProof));
            Assert.Equal(99, proof.Payload.Length);
        }
    }

    [Fact]
    public async Task Responder_ProcessesUpstreamLinkPackets_AndProvesLikeUpstream()
    {
        var (router, iface, manager) = await NewRouterAsync();
        await using var _ = manager;
        var r = Request("mtu500");
        var s = r.GetProperty("samples");
        var identity = Identity.FromPrivateKey(Hex(Root, "destinationPrivateKey"));
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = new LocalDestination(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["vectors", "link"], identity))
        {
            AcceptLinks = true,
            ProofStrategy = ProofStrategy.All,
            LinkEstablished = l => established.TrySetResult(l),
        };
        local.RegisterRequestHandler("/echo", ctx => $"echo:{ctx.Data}", RequestPolicy.All);
        var link = Link.ValidateRequest(router.Links, local, Received(Hex(r, "requestRaw"), iface), Hex(Root, "responderX25519Private"))!;
        Assert.Equal(LinkStatus.Handshake, link.Status);

        var data = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identified = new TaskCompletionSource<Identity>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.PacketReceived = (d, _) => data.TrySetResult(d);
        link.RemoteIdentified = (_, id) => identified.TrySetResult(id);

        link.Receive(Received(Hex(s.GetProperty("lrrtt"), "raw"), iface));
        await established.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LinkStatus.Active, link.Status);
        Assert.Equal(0.25, link.Rtt!.Value, 3);

        link.Receive(Received(Hex(s.GetProperty("data"), "raw"), iface));
        Assert.Equal(Hex(s.GetProperty("data"), "plaintext"), await data.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Hex(s.GetProperty("packet_proof"), "raw"),
            await iface.NextAsync(p => p.PacketType == PacketType.Proof && p.Context == LinkContext.None));

        link.Receive(Received(Hex(s.GetProperty("linkidentify"), "raw"), iface));
        Assert.Equal(Hex(s.GetProperty("linkidentify"), "identityHash"), (await identified.Task.WaitAsync(TimeSpan.FromSeconds(5))).Hash);

        link.Receive(Received(Hex(s.GetProperty("request"), "raw"), iface));
        var responseRaw = await iface.NextAsync(p => p.Context == LinkContext.Response);
        var response = (object?[])MsgPack.Unpack(link.Decrypt(Packet.Parse(responseRaw).Payload.Span)!)!;
        Assert.Equal(Hex(s.GetProperty("request"), "requestId"), (byte[])response[0]!);
        Assert.Equal("echo:hi", response[1]);

        link.Receive(Received(Hex(s.GetProperty("linkclose"), "raw"), iface));
        await Task.Delay(50);
        Assert.Equal(LinkStatus.Closed, link.Status);
        Assert.Equal(LinkTeardownReason.InitiatorClosed, link.TeardownReason);
    }
}
