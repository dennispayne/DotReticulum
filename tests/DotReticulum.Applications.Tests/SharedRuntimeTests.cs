using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

/// <summary>Vectors independently executed against hosted upstream a29bfb51 and the recorded Python connection.py SHA-256.</summary>
[Trait("Category", "Integration")]
public sealed class SharedRuntimeTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "shared-runtime.json")));
    private static byte[] PacketBytes()
    {
        using var vectors = Vectors();
        return Convert.FromHexString(vectors.RootElement.GetProperty("hdlc_packet").GetString()!);
    }
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static IPEndPoint Loopback => new(IPAddress.Loopback, 0);

    [Fact]
    public async Task ReferenceValuesAndByteFramesRoundTripWithoutNormalizingBinaryKeys()
    {
        using var vectors = Vectors();
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", vectors.RootElement.GetProperty("upstream").GetString());
        Assert.Equal(4, vectors.RootElement.GetProperty("source_sha256").EnumerateObject().Count());
        foreach (var vector in vectors.RootElement.GetProperty("values").EnumerateArray())
        {
            var packed = Convert.FromHexString(vector.GetProperty("packed").GetString()!);
            Assert.Equal(packed, ManagementMessagePack.Pack(ManagementMessagePack.Unpack(packed)));
            using var stream = new MemoryStream();
            await ManagementConnection.WriteAsync(stream, packed, CancellationToken.None);
            Assert.Equal(Convert.FromHexString(vector.GetProperty("framed").GetString()!), stream.ToArray());
            stream.Position = 0;
            Assert.Equal(packed, await ManagementConnection.ReadAsync(stream, CancellationToken.None));
        }
        var map = Assert.IsType<Dictionary<object, object?>>(ManagementMessagePack.Unpack([0x81, 0xc4, 1, 0xff, 0xc3]));
        Assert.True(Assert.IsType<bool>(map[new byte[] { 0xff }]));
    }

    [Theory]
    [InlineData("ffffffffffffffffffffffff")]
    [InlineData("fffffffe")]
    [InlineData("00400001")]
    [InlineData("ffffffff0000000000400001")]
    [InlineData("00000004aabb")]
    public async Task RejectsMalformedOversizedAndTruncatedByteFrames(string hex)
    {
        using var stream = new MemoryStream(Convert.FromHexString(hex));
        if (hex == "00000004aabb")
            await Assert.ThrowsAsync<EndOfStreamException>(async () => await ManagementConnection.ReadAsync(stream, CancellationToken.None));
        else
            await Assert.ThrowsAsync<InvalidDataException>(async () => await ManagementConnection.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task AcceptsBoundedExtendedPythonLengthPrefix()
    {
        using var stream = new MemoryStream(Convert.FromHexString("ffffffff0000000000000001c0"));
        Assert.Equal(new byte[] { 0xc0 }, await ManagementConnection.ReadAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("ddffffffff")]
    [InlineData("dfffffffff")]
    [InlineData("c70000")]
    [InlineData("80c0")]
    [InlineData("81c0c0")]
    [InlineData("82a16101a16102")]
    [InlineData("d901ff")]
    [InlineData("918091")]
    public void RejectsUnsupportedAndHostileMessagePack(string hex) =>
        Assert.ThrowsAny<Exception>(() => ManagementMessagePack.Unpack(Convert.FromHexString(hex)));

    [Fact]
    public void RejectsUnsupportedProviderValuesAndDeepValues()
    {
        Assert.Throws<ArgumentException>(() => ManagementMessagePack.Pack(new object()));
        object? value = null;
        for (var i = 0; i < 40; i++) value = new object?[] { value };
        Assert.Throws<ArgumentException>(() => ManagementMessagePack.Pack(value));
        Assert.Throws<InvalidDataException>(() => ManagementMessagePack.Unpack(Enumerable.Repeat((byte)0x91, 40).Append((byte)0xc0).ToArray()));
    }

    [Theory]
    [InlineData("get", "interface_stats")]
    [InlineData("get", "rate_table")]
    [InlineData("get", "lowest_interface_bitrate")]
    [InlineData("get", "medium_path_timeout")]
    [InlineData("get", "link_count")]
    [InlineData("get", "active_link_count")]
    [InlineData("get", "profiling_results")]
    [InlineData("get", "blackholed_identities")]
    [InlineData("drop", "announce_queues")]
    public void RecognizesAllowlistedOperations(string operation, string path)
    {
        var request = ManagementRequest.Parse(new Dictionary<object, object?> { [operation] = path });
        Assert.Equal(operation, request.Operation);
        Assert.Equal(path, request.Path);
    }

    [Fact]
    public void ValidatesRequiredOperationArgumentsAndRejectsAmbiguousCalls()
    {
        Assert.Throws<InvalidDataException>(() => ManagementRequest.Parse(new Dictionary<object, object?> { ["get"] = "unknown" }));
        Assert.Throws<InvalidDataException>(() => ManagementRequest.Parse(new Dictionary<object, object?> { ["get"] = "link_count", ["drop"] = "announce_queues" }));
        Assert.Throws<InvalidDataException>(() => ManagementRequest.Parse(new Dictionary<object, object?> { ["get"] = "path_table" }));
        Assert.Throws<InvalidDataException>(() => ManagementRequest.Parse(new Dictionary<object, object?> { ["get"] = "next_hop", ["destination_hash"] = new byte[15] }));
        Assert.Throws<InvalidDataException>(() => ManagementRequest.Parse(new Dictionary<object, object?> { ["manage"] = "attach_interface" }));
        Assert.Throws<InvalidDataException>(() => ManagementRequest.Parse(new Dictionary<object, object?> { ["blackhole_identity"] = new byte[16], ["until"] = double.NaN, ["reason"] = null }));
        Assert.Equal("path_table", ManagementRequest.Parse(new Dictionary<object, object?> { ["get"] = "path_table", ["max_hops"] = null }).Path);
        foreach (var operation in new[] { "used", "retain", "unretain" })
            Assert.Equal(operation, ManagementRequest.Parse(new Dictionary<object, object?> { ["destination_data"] = operation, ["destination_hash"] = new byte[16] }).Path);
        Assert.Equal("retain", ManagementRequest.Parse(new Dictionary<object, object?> { ["identity_data"] = "retain", ["identity_hash"] = new byte[16] }).Path);
    }

    [Fact]
    public async Task RpcMutuallyAuthenticatesConcurrentClientsAndDispatchesOnlyValidCalls()
    {
        var calls = new ConcurrentQueue<ManagementRequest>();
        await using var server = new ManagementRpcServer(Loopback, Key, (call, _) =>
        {
            calls.Enqueue(call);
            return ValueTask.FromResult<object?>(new Dictionary<object, object?> { ["answer"] = 42L, [new byte[] { 1, 2 }] = true });
        });
        await server.StartAsync();
        using var client = new ManagementRpcClient(server.LocalEndpoint!, Key);
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
            await client.CallAsync(new Dictionary<object, object?> { ["get"] = "link_count" })));
        Assert.Equal(12, calls.Count);
        foreach (var response in responses)
        {
            var map = Assert.IsType<Dictionary<object, object?>>(response);
            Assert.Equal(42L, map["answer"]);
            Assert.Equal(true, map[new byte[] { 1, 2 }]);
        }
    }

    [Fact]
    public async Task BadKeysStalledPeersAndFailingObserversDoNotPoisonListener()
    {
        var errors = new ConcurrentQueue<Exception>();
        await using var server = new ManagementRpcServer(Loopback, Key, (_, _) => ValueTask.FromResult<object?>(7L),
            TimeSpan.FromMilliseconds(300), diagnostic: ex => { errors.Enqueue(ex); throw new InvalidOperationException("observer"); });
        await server.StartAsync();
        using (var wrong = new ManagementRpcClient(server.LocalEndpoint!, new byte[32], TimeSpan.FromSeconds(2)))
            await Assert.ThrowsAsync<AuthenticationException>(async () => await wrong.CallAsync(new Dictionary<object, object?> { ["get"] = "link_count" }));
        using var stalled = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await stalled.ConnectAsync(server.LocalEndpoint!);
        await Task.Delay(500);
        using var valid = new ManagementRpcClient(server.LocalEndpoint!, Key);
        Assert.Equal(7L, await valid.CallAsync(new Dictionary<object, object?> { ["get"] = "link_count" }));
        Assert.True(errors.Count >= 2);
    }

    [Fact]
    public async Task StalledRpcProviderCannotPreventDeterministicShutdown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ManagementRpcServer(Loopback, Key, (_, _) => { entered.TrySetResult(); return new ValueTask<object?>(never.Task); });
        await server.StartAsync();
        using var client = new ManagementRpcClient(server.LocalEndpoint!, Key);
        var request = client.CallAsync(new Dictionary<object, object?> { ["get"] = "link_count" }).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<IOException>(() => request);
        await server.DisposeAsync();
        never.TrySetResult(null);
    }

    [Fact]
    public async Task ClientTimeoutCancellationAndDisposalAreExplicit()
    {
        using var listener = new TcpListener(Loopback);
        listener.Start();
        using var client = new ManagementRpcClient(listener.LocalEndpoint, Key, TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.CallAsync(new Dictionary<object, object?> { ["get"] = "link_count" }));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.CallAsync(new Dictionary<object, object?> { ["get"] = "link_count" }, canceled.Token));
        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await client.CallAsync(new Dictionary<object, object?> { ["get"] = "link_count" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnswersActualPythonLegacyAndModernChallengeVectors(bool modern)
    {
        using var vectors = Vectors();
        var challenge = Convert.FromHexString(vectors.RootElement.GetProperty(modern ? "modern_challenge" : "challenge").GetString()!);
        var response = Convert.FromHexString(vectors.RootElement.GetProperty(modern ? "modern_response" : "legacy_response").GetString()!);
        using var listener = new TcpListener(Loopback);
        listener.Start();
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(listener.LocalEndpoint);
        using var accepted = await listener.AcceptSocketAsync();
        using var clientStream = new NetworkStream(socket);
        using var serverStream = new NetworkStream(accepted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var authentication = ManagementConnection.AuthenticateAsync(clientStream, Key, false, timeout.Token).AsTask();
        byte[] bytes = [.. "#CHALLENGE#"u8.ToArray(), .. challenge];
        await ManagementConnection.WriteAsync(serverStream, bytes, timeout.Token);
        Assert.Equal(response, await ManagementConnection.ReadAsync(serverStream, timeout.Token));
        await ManagementConnection.WriteAsync(serverStream, "#WELCOME#"u8.ToArray(), timeout.Token);
        // Complete the reciprocal challenge with the actual implementation, exercising both halves.
        var reciprocal = await ManagementConnection.ReadAsync(serverStream, timeout.Token);
        var message = reciprocal.AsSpan(11).ToArray();
        byte[] mac = [.. "{sha256}"u8.ToArray(), .. System.Security.Cryptography.HMACSHA256.HashData(Key, message)];
        await ManagementConnection.WriteAsync(serverStream, mac, timeout.Token);
        Assert.Equal("#WELCOME#"u8.ToArray(), await ManagementConnection.ReadAsync(serverStream, timeout.Token));
        await authentication;
    }

    [Fact]
    public async Task SharedBearerUsesReferenceHdlcAndRejectsDuplicateStart()
    {
        await using var server = new LocalSharedServerInterface(Loopback);
        var connected = new TaskCompletionSource<IPacketInterface>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ChildConnected += peer => connected.TrySetResult(peer);
        server.Reserve();
        await server.StartAsync(CancellationToken.None);
        await using var client = new LocalSharedPacketInterface(server.LocalEndpoint!);
        await client.StartAsync(CancellationToken.None);
        await using var peer = await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var fromClient = peer.ReceiveAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        await using var fromServer = client.ReceiveAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var receive = fromClient.MoveNextAsync().AsTask();
        await client.SendAsync(PacketBytes(), timeout.Token);
        Assert.True(await receive);
        Assert.Equal(PacketBytes(), fromClient.Current.ToArray());
        var reply = fromServer.MoveNextAsync().AsTask();
        await peer.SendAsync(PacketBytes(), timeout.Token);
        Assert.True(await reply);
        Assert.Equal(PacketBytes(), fromServer.Current.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.StartAsync(timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await server.StartAsync(timeout.Token));
    }

    [Fact]
    public async Task ClientReconnectsAfterDaemonRestartAndKeepsOneReceiveEnumeration()
    {
        var first = new LocalSharedServerInterface(Loopback);
        first.Reserve();
        var endpoint = first.LocalEndpoint!;
        var firstPeer = new TaskCompletionSource<IPacketInterface>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ChildConnected += peer => firstPeer.TrySetResult(peer);
        await first.StartAsync(CancellationToken.None);
        await using var client = new LocalSharedPacketInterface(endpoint, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(200));
        await client.StartAsync(CancellationToken.None);
        await firstPeer.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var stream = client.ReceiveAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var read = stream.MoveNextAsync().AsTask();
        await first.DisposeAsync();
        await using var restarted = new LocalSharedServerInterface(endpoint);
        var nextPeer = new TaskCompletionSource<IPacketInterface>(TaskCreationOptions.RunContinuationsAsynchronously);
        restarted.ChildConnected += peer => nextPeer.TrySetResult(peer);
        await restarted.StartAsync(timeout.Token);
        var next = await nextPeer.Task.WaitAsync(timeout.Token);
        await next.SendAsync(PacketBytes(), timeout.Token);
        Assert.True(await read);
        Assert.Equal(PacketBytes(), stream.Current.ToArray());
    }

    [Fact]
    public async Task ElectsOneHostAndAClientWithoutStartingTheirInterfaces()
    {
        using var probe = new TcpListener(Loopback);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var options = new SharedInstanceOptions { InstancePort = port };
        await using var host = await SharedInstanceSession.OpenAsync(options);
        await using var client = await SharedInstanceSession.OpenAsync(options);
        Assert.Equal(SharedInstanceRole.Host, host.Role);
        Assert.IsAssignableFrom<ISpawningPacketInterface>(host.Server);
        Assert.Equal(SharedInstanceRole.Client, client.Role);
        Assert.IsType<LocalSharedPacketInterface>(client.Bearer);
        await host.Bearer!.StartAsync(CancellationToken.None);
        await client.Bearer!.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RequiredSharedFailsRatherThanBecomingHostAndDisabledIsStandalone()
    {
        using var probe = new TcpListener(Loopback);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await Assert.ThrowsAsync<IOException>(async () => await SharedInstanceSession.OpenAsync(new SharedInstanceOptions { Required = true, InstancePort = port }));
        await using var standalone = await SharedInstanceSession.OpenAsync(new SharedInstanceOptions { Enabled = false });
        Assert.Equal(SharedInstanceRole.Standalone, standalone.Role);
        Assert.Null(standalone.Bearer);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await SharedInstanceSession.OpenAsync(new SharedInstanceOptions { Enabled = false, Required = true }));
    }

    [Fact]
    public async Task UnreachableOccupiedEndpointFallsBackWithoutLeakingReservation()
    {
        var unix = LocalSharedPlatformPolicy.SupportsAbstractUnixSockets;
        var options = new SharedInstanceOptions
        {
            SocketPath = unix ? "unreachable-" + Guid.NewGuid().ToString("N") : null,
            OperationTimeout = TimeSpan.FromMilliseconds(100),
        };
        using var occupied = new Socket(unix ? AddressFamily.Unix : AddressFamily.InterNetwork,
            SocketType.Stream, unix ? ProtocolType.Unspecified : ProtocolType.Tcp);
        if (!unix) occupied.ExclusiveAddressUse = true;
        // Abstract Unix names reserve the endpoint even when not listening; some Linux TCP stacks do not.
        occupied.Bind(unix ? options.BearerEndpoint : Loopback);
        if (!unix) options = options with { InstancePort = ((IPEndPoint)occupied.LocalEndPoint!).Port };
        await using var session = await SharedInstanceSession.OpenAsync(options);
        Assert.Equal(SharedInstanceRole.Standalone, session.Role);
        occupied.Dispose();
        await using var recovered = await SharedInstanceSession.OpenAsync(options);
        Assert.Equal(SharedInstanceRole.Host, recovered.Role);
    }

    [Fact]
    public void RemoteManagementAclAndRequestShapesMatchNativeTransportContract()
    {
        using var identity = Identity.Generate();
        using var stranger = Identity.Generate();
        var provider = new Func<ManagementRequest, object?>(request => request.Path switch
        {
            "interface_stats" => new Dictionary<object, object?> { ["interfaces"] = Array.Empty<object?>() },
            "link_count" => 3L,
            "profiling_results" => null,
            "path_table" or "rate_table" => new object?[]
            {
                new Dictionary<object, object?> { ["hash"] = new byte[16], ["hops"] = 1L },
                new Dictionary<object, object?> { ["hash"] = Enumerable.Repeat((byte)1, 16).ToArray(), ["hops"] = 2L },
            },
            _ => null,
        });
        var service = new ManagementRemoteService(provider, [identity.Hash]);
        var destination = service.Register(identity, _ => { });
        Assert.Equal(ManagementRemoteService.DestinationHash(identity.Hash), destination.Destination.Hash);
        RequestContext Context(string path, object? data, Identity? caller) => new(path, data, new byte[16], new byte[16], caller, 0);
        Assert.Null(service.Handle(Context("/status", new object?[] { true }, stranger)));
        Assert.Null(service.Handle(Context("/status", new object?[] { true }, null)));
        var status = Assert.IsType<object?[]>(service.Handle(Context("/status", new object?[] { true, true }, identity)));
        Assert.Equal(3, status.Length);
        Assert.Equal(3L, status[1]);
        var table = Assert.IsType<object?[]>(service.Handle(Context("/path", new object?[] { "table", new byte[16], null }, identity)));
        Assert.Single(table);
        Assert.Null(service.Handle(Context("/path", new object?[] { "drop", null }, identity)));
    }
}
