using System.Security.Cryptography;
using DotReticulum.Core;
using static DotReticulum.Transport.Tests.LinkTests;

namespace DotReticulum.Transport.Tests;

// In-process Resource transfers over direct and transport-hop topologies. Wire formats against upstream: ResourceVectorTests; live: LiveLinkTests.
[Trait("Category", "Unit")]
public class ResourceTests
{
    private static async Task<(Link Initiator, Link Responder)> ConnectAsync(Topology topo)
    {
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        topo.Destination.LinkEstablished = l =>
        {
            l.ResourceStrategy = ResourceStrategy.AcceptAll;
            accepted.TrySetResult(l);
        };
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => established.TrySetResult(l));
        await Within(established.Task);
        return (link, await Within(accepted.Task));
    }

    private static byte[] Payload(int size, bool compressible)
    {
        var data = new byte[size];
        if (compressible)
        {
            for (var i = 0; i < size; i++) data[i] = (byte)("reticulum "u8[i % 10]);
        }
        else
        {
            RandomNumberGenerator.Fill(data);
        }

        return data;
    }

    public static IEnumerable<object[]> Cases() =>
    [
        [false, 300 * 1024, false],
        [true, 300 * 1024, false],
        [false, 1000 * 1000, true],
        [true, 3 * 1024 * 1024, false],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Resource_TransfersAndProves(bool viaTransport, int size, bool compressible)
    {
        await using var topo = await BuildAsync(viaTransport);
        var (initiator, responder) = await ConnectAsync(topo);
        var received = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        responder.ResourceReceived = r => received.TrySetResult(r);
        var sent = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = Payload(size, compressible);

        initiator.SendResource(payload, concluded: r => sent.TrySetResult(r));

        var rx = await Within(received.Task, 60);
        var tx = await Within(sent.Task, 60);
        Assert.Equal(ResourceStatus.Complete, rx.Status);
        Assert.Equal(ResourceStatus.Complete, tx.Status);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(rx.Data!));
        Assert.Equal(compressible, tx.Compressed);
        Assert.Equal(1.0, rx.Progress);
    }

    [Fact]
    public async Task Resource_ReverseDirection_AndMetadata()
    {
        await using var topo = await BuildAsync(false);
        var (initiator, responder) = await ConnectAsync(topo);
        initiator.ResourceStrategy = ResourceStrategy.AcceptAll;
        var received = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        initiator.ResourceReceived = r => received.TrySetResult(r);
        var payload = Payload(50_000, false);

        responder.SendResource(payload, new Dictionary<object, object?> { ["name"] = "file.bin" });

        var rx = await Within(received.Task, 30);
        Assert.Equal(payload, rx.Data);
        Assert.True(rx.HasMetadata);
        Assert.Equal("file.bin", ((Dictionary<object, object?>)rx.Metadata!)["name"]);
    }

    [Fact]
    public async Task Resource_Segmented_ReassemblesLargePayload()
    {
        await using var topo = await BuildAsync(false);
        var (initiator, responder) = await ConnectAsync(topo);
        var received = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        responder.ResourceReceived = r => received.TrySetResult(r);
        var sent = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = Payload((2 * Resource.MaxEfficientSize) + 12345, false);

        initiator.SendResource(payload, new object?[] { "meta" }, autoCompress: false, concluded: r => sent.TrySetResult(r));

        var rx = await Within(received.Task, 120);
        await Within(sent.Task, 120);
        Assert.Equal(3, rx.TotalSegments);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(rx.Data!));
    }

    [Fact]
    public async Task Resource_RejectedByStrategy_NoneFailsSender()
    {
        await using var topo = await BuildAsync(false);
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        topo.Destination.LinkEstablished = l => accepted.TrySetResult(l);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => established.TrySetResult(l));
        await Within(established.Task);
        var remote = await Within(accepted.Task);
        remote.ResourceStrategy = ResourceStrategy.AcceptApp;
        remote.ResourceAdvertised = _ => false;
        var sent = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);

        link.SendResource(Payload(4000, false), concluded: r => sent.TrySetResult(r));

        var tx = await Within(sent.Task);
        Assert.Equal(ResourceStatus.Rejected, tx.Status);
    }

    [Fact]
    public async Task Resource_CancelBySender_CancelsReceiver()
    {
        await using var topo = await BuildAsync(false);
        var (initiator, responder) = await ConnectAsync(topo);
        var started = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        responder.ResourceStarted = r => started.TrySetResult(r);
        responder.ResourceReceived = r => received.TrySetResult(r);

        var tx = initiator.SendResource(Payload(5 * 1024 * 1024, false));
        await Within(started.Task);
        tx.Cancel();

        var rx = await Within(received.Task, 20);
        Assert.Equal(ResourceStatus.Failed, rx.Status);
        Assert.Equal(ResourceStatus.Failed, tx.Status);
    }

    [Theory]
    [MemberData(nameof(LinkTests.Topologies), MemberType = typeof(LinkTests))]
    public async Task Request_LargeRequestAndLargeResponse_UseResources(bool viaTransport)
    {
        await using var topo = await BuildAsync(viaTransport);
        topo.Destination.RegisterRequestHandler("/big", ctx => ((byte[])ctx.Data!).Reverse().ToArray(), RequestPolicy.All);
        topo.Destination.RegisterRequestHandler("/file", ctx => new FileResponse(Payload(80_000, false), "info"), RequestPolicy.All);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => established.TrySetResult(l));
        await Within(established.Task);

        var data = Payload(20_000, false);
        var receipt = link.Request("/big", data, timeout: 60);
        Assert.NotNull(receipt);
        var response = (byte[])(await Within(receipt.ResponseTask, 60))!;
        Assert.Equal(data.Reverse().ToArray(), response);

        var fileReceipt = link.Request("/file", null, timeout: 60);
        Assert.NotNull(fileReceipt);
        var file = (byte[])(await Within(fileReceipt.ResponseTask, 60))!;
        Assert.Equal(80_000, file.Length);
        Assert.Equal("info", fileReceipt.Metadata);
    }
}


