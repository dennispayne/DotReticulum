using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Core;
using static DotReticulum.Transport.Tests.LinkTests;

namespace DotReticulum.Transport.Tests;

// Independent vectors: GenerateParity5ResourceVectors.py executes RNS at
// a29bfb51c468945c96a1ee22cae0114e993e2391; source hash is recorded in the fixture.
public class Parity5ResourceTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity5-resource-vectors.json"))).RootElement;

    private static string[] StorageFiles(string directory) => Directory.GetFiles(directory)
        .Where(path => Path.GetFileName(path) != ResourceStorageScavenger.OwnershipMarkerFileName).ToArray();

    private static async Task<(Link Sender, Link Receiver)> Connect(Topology topology)
    {
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        topology.Destination.LinkEstablished = link =>
        {
            link.ResourceStrategy = ResourceStrategy.AcceptAll;
            accepted.TrySetResult(link);
        };
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = topology.Initiator.Router.Links.Connect(topology.Destination.Identity,
            topology.Destination.Destination.Hash, link => established.TrySetResult(link));
        await Within(established.Task);
        return (sender, await Within(accepted.Task));
    }

    private static Dictionary<object, object?> Advertisement() => new()
    {
        ["t"] = 64L, ["d"] = 0L, ["n"] = 1L, ["h"] = new byte[32],
        ["r"] = new byte[4], ["o"] = new byte[32], ["i"] = 1L, ["l"] = 1L,
        ["q"] = null, ["f"] = 1L, ["m"] = new byte[4],
    };

    public static IEnumerable<object[]> InvalidAdvertisements() =>
    [
        ["t", -1L], ["t", 0L], ["d", -1L], ["n", 0L], ["n", long.MaxValue],
        ["i", -1L], ["i", 2L], ["l", 0L], ["f", 64L], ["f", 4L],
        ["q", new byte[16]], ["r", new byte[3]], ["m", new byte[3]], ["h", new byte[31]],
        ["t", 64.0], ["i", long.MaxValue],
    ];

    [Theory]
    [MemberData(nameof(InvalidAdvertisements))]
    public void Advertisement_RejectsInvalidFieldsBeforeAllocation(string key, object value)
    {
        var advertisement = Advertisement();
        advertisement[key] = value;
        var exception = Record.Exception(() => ResourceAdvertisement.Unpack(MsgPack.Pack(advertisement)));
        Assert.NotNull(exception);
        Assert.True(exception is InvalidDataException or OverflowException);
    }

    [Fact]
    public void BzipExpansion_IsBoundedAndCancelable()
    {
        var compressed = Convert.FromHexString(Vectors.GetProperty("compressedExpansion").GetString()!);
        foreach (var boundary in Vectors.GetProperty("compressionLimits").EnumerateArray())
        {
            var limit = boundary.GetProperty("limit").GetInt64();
            if (boundary.GetProperty("eof").GetBoolean())
                Assert.Equal(boundary.GetProperty("length").GetInt32(), Bz2.Decompress(compressed, limit).Length);
            else
                Assert.Throws<InvalidDataException>(() => Bz2.Decompress(compressed, limit));
        }
        Assert.False(Vectors.GetProperty("truncatedCompressionEof").GetBoolean());
        Assert.Throws<InvalidDataException>(() => Bz2.Decompress(compressed[..^1], 65537));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bz2.Decompress(compressed, -1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Bz2.Decompress(compressed, 65537, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => Bz2.Decompress(Bz2.Compress([]), 0, cancellation.Token));
    }

    [Fact]
    public async Task FileMultipart_UsesReferenceBoundaries_AndSpillsReceivedData()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var payload = Enumerable.Range(0, 2 * Resource.MaxEfficientSize + 37).Select(index => (byte)(index % 251)).ToArray();
            Assert.Equal(Vectors.GetProperty("payloadSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(payload)));
            var path = Path.Combine(directory, "source.bin");
            await File.WriteAllBytesAsync(path, payload);
            await using var topology = await BuildAsync(false);
            var (sender, receiver) = await Connect(topology);
            receiver.ResourcePolicy = new ResourcePolicy { MemoryThreshold = 1024, StorageDirectory = directory };
            var segments = new ConcurrentBag<Resource>();
            receiver.ResourceStarted = segments.Add;
            var received = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.ResourceReceived = resource => received.TrySetResult(resource);
            sender.SendResourceFile(path, new Dictionary<object, object?> { ["name"] = "source.bin" }, false,
                resource => sent.TrySetResult(resource));
            await using var result = await Within(received.Task, 90);
            var transmitted = await Within(sent.Task, 90);
            Assert.Equal(ResourceStatus.Complete, transmitted.Status);
            Assert.True(result.IsFileBacked);
            Assert.False(result.HasDataInMemory);
            using var input = result.OpenRead();
            Assert.Equal(SHA256.HashData(payload), await SHA256.HashDataAsync(input));
            Assert.Equal("source.bin", Assert.IsType<Dictionary<object, object?>>(result.Metadata)["name"]);
            Assert.Equal(3, segments.Count);
            var packedMetadata = MsgPack.Pack(new Dictionary<object, object?> { ["name"] = "source.bin" });
            byte[] metadata = [0, 0, (byte)packedMetadata.Length, .. packedMetadata];
            foreach (var reference in Vectors.GetProperty("segments").EnumerateArray())
            {
                var index = reference.GetProperty("index").GetInt32();
                var segment = Assert.Single(segments, segment => segment.SegmentIndex == index);
                var start = index == 1 ? 0 : Resource.MaxEfficientSize - metadata.Length + ((index - 2) * Resource.MaxEfficientSize);
                var count = Math.Min(index == 1 ? Resource.MaxEfficientSize - metadata.Length : Resource.MaxEfficientSize, payload.Length - start);
                byte[] body = index == 1 ? [.. metadata, .. payload.AsSpan(start, count)] : payload.AsSpan(start, count).ToArray();
                Assert.Equal(reference.GetProperty("bodySize").GetInt32(), body.Length);
                Assert.Equal(reference.GetProperty("bodySha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(body)));
                Assert.Equal(Resource.ComputeHash(body, segment.RandomHash), segment.Hash);
            }
            await result.DisposeAsync();
            Assert.Single(StorageFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task QueuedCancelAndRejectedActive_ReleaseFollowingResource_ExactlyOnce()
    {
        await using var topology = await BuildAsync(false);
        var (sender, receiver) = await Connect(topology);
        var advertised = 0;
        receiver.ResourceStrategy = ResourceStrategy.AcceptApp;
        receiver.ResourceAdvertised = _ => Interlocked.Increment(ref advertised) != 1;
        var firstCount = 0;
        var canceledCount = 0;
        var success = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        Resource first, canceled;
        lock (sender.Gate)
        {
            first = sender.SendResource(new byte[1000], concluded: _ => Interlocked.Increment(ref firstCount));
            canceled = sender.SendResource(new byte[1000], concluded: _ => Interlocked.Increment(ref canceledCount));
            sender.SendResource(new byte[1000], concluded: resource => success.TrySetResult(resource));
            Assert.Equal(ResourceStatus.Queued, canceled.Status);
            canceled.Cancel();
            canceled.Cancel();
        }
        Assert.Equal(ResourceStatus.Complete, (await Within(success.Task)).Status);
        await Until(() => Volatile.Read(ref firstCount) == 1 && Volatile.Read(ref canceledCount) == 1);
        first.Cancel();
        canceled.Cancel();
        Assert.Equal(ResourceStatus.Rejected, first.Status);
        Assert.Equal(1, firstCount);
        Assert.Equal(1, canceledCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("0000")]
    [InlineData("00006301")]
    [InlineData("000001c1")]
    [InlineData("000002c0c0")]
    public async Task MalformedMetadata_ConcludesCorruptWithoutProofOrUnobservedWork(string bodyHex)
    {
        await using var topology = await BuildAsync(false);
        var (sender, receiver) = await Connect(topology);
        var body = Convert.FromHexString(bodyHex);
        byte[] random = [1, 2, 3, 4];
        var encrypted = sender.Encrypt([0, 0, 0, 0, .. body]);
        var hash = Resource.ComputeHash(body, random);
        var advertisement = new ResourceAdvertisement
        {
            TransferSize = encrypted.Length, DataSize = body.Length, Parts = 1,
            Hash = hash, OriginalHash = hash, RandomHash = random, Hashmap = Resource.MapHash(encrypted, random),
            SegmentIndex = 1, TotalSegments = 1, Flags = 0x21,
        };
        var terminal = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        Resource resource;
        lock (receiver.Gate)
        {
            resource = Resource.Accept(receiver, advertisement, completed =>
            {
                Interlocked.Increment(ref count);
                terminal.TrySetResult(completed);
            }, null, null)!;
            Assert.NotNull(resource);
            resource.ReceivePart(Packet.Create(PacketType.Data, DestinationType.Link, receiver.LinkId,
                encrypted, LinkContext.Resource), encrypted.Length + Packet.Header1Size);
        }
        Assert.Equal(ResourceStatus.Corrupt, (await Within(terminal.Task)).Status);
        await receiver.DrainResourceWorkAsync();
        resource.Cancel();
        Assert.Equal(1, count);
        Assert.Equal(LinkStatus.Active, receiver.Status);
    }

    [Fact]
    public async Task InputOwnershipAndCancellation_AreExplicit()
    {
        await using var topology = await BuildAsync(false);
        var (sender, receiver) = await Connect(topology);
        receiver.ResourceStrategy = ResourceStrategy.AcceptNone;
        using var cancellation = new CancellationTokenSource();
        var source = new MemoryStream(new byte[Resource.MaxEfficientSize + 1]);
        var completed = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = sender.SendResource(source, autoCompress: false, concluded: result => completed.TrySetResult(result),
            cancellationToken: cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(ResourceStatus.Failed, (await Within(completed.Task)).Status);
        Assert.False(source.CanRead);
        await resource.DisposeAsync();
        using var retained = new MemoryStream(new byte[100]);
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => sender.SendResource(retained, leaveOpen: true,
            cancellationToken: alreadyCanceled.Token));
        Assert.True(retained.CanRead);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(16384)]
    [InlineData(262144)]
    public async Task UpgradedMtu_TransfersActualLargeResourceParts(int mtu)
    {
        var outgoing = new Parity4LinkMtuTests.Probe { HardwareMtu = mtu, SupportsMtuDiscovery = true };
        var incoming = new Parity4LinkMtuTests.Probe { HardwareMtu = mtu, SupportsMtuDiscovery = true, Peer = outgoing };
        outgoing.Peer = incoming;
        await using var senderNode = await Parity4LinkMtuTests.Harness.Create(outgoing, run: true);
        await using var receiverNode = await Parity4LinkMtuTests.Harness.Create(incoming, run: true);
        using var identity = Identity.Generate();
        var owner = new LocalDestination(identity, Destination.Create(DestinationType.Single, "dotreticulum", ["parity5", "resource"], identity))
        {
            AcceptLinks = true,
        };
        receiverNode.Router.RegisterLocalDestination(owner);
        await receiverNode.Router.AnnounceAsync(owner);
        await Until(() => senderNode.Router.HasPath(owner.Destination.Hash));
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.LinkEstablished = link =>
        {
            link.ResourceStrategy = ResourceStrategy.AcceptAll;
            accepted.TrySetResult(link);
        };
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = senderNode.Router.Links.Connect(identity, owner.Destination.Hash, link => established.TrySetResult(link));
        await Within(established.Task);
        var receiver = await Within(accepted.Task);
        Assert.Equal(mtu, sender.Mtu);
        Assert.Equal(mtu, receiver.Mtu);
        var received = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.ResourceReceived = resource => received.TrySetResult(resource);
        var payload = RandomNumberGenerator.GetBytes(100_000);
        sender.SendResource(payload, autoCompress: false, concluded: resource => sent.TrySetResult(resource));
        Assert.Equal(payload, (await Within(received.Task, 30)).Data);
        Assert.Equal(ResourceStatus.Complete, (await Within(sent.Task, 30)).Status);
        Assert.Contains(outgoing.Sent, packet => packet.Context == LinkContext.Resource && packet.ToArray().Length > Packet.Mtu);
        Assert.All(outgoing.Sent.Where(packet => packet.Context == LinkContext.Resource), packet => Assert.True(packet.ToArray().Length <= mtu));
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(1, 3)]
    [InlineData(0, 4)]
    [InlineData(2, 4)]
    public async Task InvalidHmu_IsRejectedBeforeIndexingOrReplacingKnownHashes(int segment, int bytes)
    {
        await using var topology = await BuildAsync(false);
        var (_, receiver) = await Connect(topology);
        var random = new byte[] { 1, 2, 3, 4 };
        var parts = Enumerable.Range(0, 75).Select(index =>
        {
            var part = new byte[464];
            BitConverter.GetBytes(index).CopyTo(part, 0);
            return part;
        }).ToArray();
        var hashes = parts.SelectMany(part => Resource.MapHash(part, random)).ToArray();
        var advertisement = new ResourceAdvertisement
        {
            TransferSize = 75 * 464, DataSize = 75 * 464 - 64, Parts = 75,
            Hash = new byte[32], OriginalHash = new byte[32], RandomHash = random,
            Hashmap = hashes[..(ResourceAdvertisement.HashmapMaxLength * 4)],
            SegmentIndex = 1, TotalSegments = 1, Flags = 1,
        };
        lock (receiver.Gate)
        {
            var resource = Resource.Accept(receiver, advertisement, null, null, null);
            Assert.NotNull(resource);
            for (var index = 0; index < ResourceAdvertisement.HashmapMaxLength; index++)
                resource.ReceivePart(Packet.Create(PacketType.Data, DestinationType.Link, receiver.LinkId,
                    parts[index], LinkContext.Resource), 483);
            byte[] update = [.. resource.Hash, .. MsgPack.Pack(new object?[] { (long)segment, new byte[bytes] })];
            Assert.Throws<InvalidDataException>(() => resource.HashmapUpdatePacket(update));
            resource.CancelLocked();
        }
    }

    private sealed class WaitingInput : Stream
    {
        public TaskCompletionSource<bool> Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [ResourceUnixFact]
    public async Task PlaintextInputProxy_IsCreatedOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Unix-only resource test was not skipped.");
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, _) = await Connect(topology);
            sender.ResourcePolicy = new ResourcePolicy { StorageDirectory = directory };
            using var source = new WaitingInput();
            var preparation = sender.SendResourceAsync(source);
            try
            {
                await Within(source.Reading.Task);
                var path = Assert.Single(Directory.GetFiles(directory, "input-*.bin"));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
            finally
            {
                sender.Teardown();
                await sender.DrainResourceWorkAsync();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
            Assert.True(source.Disposed);
            Assert.Empty(StorageFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task LinkTeardown_CancelsAndJoinsOwnedStreamPreparation_AndDeletesProxy()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, _) = await Connect(topology);
            sender.ResourcePolicy = new ResourcePolicy { StorageDirectory = directory };
            using var source = new WaitingInput();
            var preparation = sender.SendResourceAsync(source);
            await Within(source.Reading.Task);
            var proxy = Assert.Single(Directory.GetFiles(directory, "input-*.bin"));
            File.SetLastWriteTimeUtc(proxy, DateTime.UtcNow.AddDays(-2));
            var scavenged = ResourceStorageScavenger.Scavenge(directory);
            Assert.Equal(1, scavenged.Active);
            Assert.Equal(0, scavenged.Deleted);
            Assert.True(File.Exists(proxy));
            sender.Teardown();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
            await sender.DrainResourceWorkAsync();
            Assert.True(source.Disposed);
            Assert.Empty(StorageFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingNextSegment_ExpiresOrRequestCancellationDisposesMultipartStorage_Once(bool cancelRequest)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, receiver) = await Connect(topology);
            receiver.ResourcePolicy = new ResourcePolicy
            {
                StorageDirectory = directory, MemoryThreshold = 1024, MultipartTimeout = 0.01,
            };
            var body = new byte[Resource.MaxEfficientSize];
            byte[] random = [1, 2, 3, 4];
            var encrypted = sender.Encrypt([0, 0, 0, 0, .. Bz2.Compress(body)]);
            var hash = Resource.ComputeHash(body, random);
            var advertisement = new ResourceAdvertisement
            {
                TransferSize = encrypted.Length, DataSize = Resource.MaxEfficientSize + 1L, Parts = 1,
                Hash = hash, OriginalHash = hash, RandomHash = random, Hashmap = Resource.MapHash(encrypted, random),
                SegmentIndex = 1, TotalSegments = 2, Flags = cancelRequest ? 23 : 7,
                RequestId = cancelRequest ? new byte[16] : null,
            };
            var count = 0;
            var terminal = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            Resource resource;
            lock (receiver.Gate)
            {
                resource = Resource.Accept(receiver, advertisement, result =>
                {
                    Interlocked.Increment(ref count);
                    terminal.TrySetResult(result);
                }, null, null)!;
                Assert.NotNull(resource);
                resource.ReceivePart(Packet.Create(PacketType.Data, DestinationType.Link, receiver.LinkId,
                    encrypted, LinkContext.Resource), encrypted.Length + Packet.Header1Size);
            }
            await Until(() => resource.Status == ResourceStatus.Complete);
            Assert.Single(StorageFiles(directory));
            if (cancelRequest) receiver.CancelRequestResources(advertisement.RequestId!);
            else receiver.Tick(receiver.Now + 0.1);
            Assert.Equal(ResourceStatus.Failed, (await Within(terminal.Task)).Status);
            resource.Cancel();
            await receiver.DrainResourceWorkAsync();
            Assert.Equal(1, count);
            Assert.Empty(StorageFiles(directory));
            Assert.Equal(LinkStatus.Active, receiver.Status);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task StreamProxy_SucceedsAndEnforcesDeclaredBoundBeforeAdvertising()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, receiver) = await Connect(topology);
            sender.ResourcePolicy = new ResourcePolicy { StorageDirectory = directory };
            using var source = new MemoryStream(RandomNumberGenerator.GetBytes(20005));
            source.Position = 5;
            var expected = source.ToArray()[5..];
            var terminal = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.ResourceReceived = result => received.TrySetResult(result);
            await sender.SendResourceAsync(source, autoCompress: false, concluded: result => terminal.TrySetResult(result));
            Assert.Equal(expected, (await Within(received.Task)).Data);
            Assert.Equal(ResourceStatus.Complete, (await Within(terminal.Task)).Status);
            Assert.False(source.CanRead);
            await sender.DrainResourceWorkAsync();
            Assert.Empty(StorageFiles(directory));

            sender.ResourcePolicy = sender.ResourcePolicy with { MaxDataSize = 1000 };
            using var excessive = new MemoryStream(new byte[1001]);
            await Assert.ThrowsAsync<InvalidDataException>(() => sender.SendResourceAsync(excessive));
            Assert.False(excessive.CanRead);
            await sender.DrainResourceWorkAsync();
            Assert.Empty(StorageFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class FailingMultipartInput(byte[] bytes, bool unsupported = false) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= Resource.MaxEfficientSize)
            {
                if (unsupported) throw new NotSupportedException("Owned test source no longer supports segment reads.");
                throw new IOException("Owned test source failed preparing segment two.");
            }
            return base.Read(buffer, offset, count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextSegmentPreparationFailure_DisposesInputAndReleasesQueue_Once(bool unsupported)
    {
        await using var topology = await BuildAsync(false);
        var (sender, _) = await Connect(topology);
        using var source = new FailingMultipartInput(new byte[Resource.MaxEfficientSize + 10], unsupported);
        var failed = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var following = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        lock (sender.Gate)
        {
            sender.SendResource(source, autoCompress: false, concluded: resource =>
            {
                Interlocked.Increment(ref count);
                failed.TrySetResult(resource);
            });
            sender.SendResource(new byte[100], concluded: resource => following.TrySetResult(resource));
        }
        Assert.Equal(ResourceStatus.Failed, (await Within(failed.Task, 60)).Status);
        Assert.Equal(ResourceStatus.Complete, (await Within(following.Task, 60)).Status);
        Assert.False(source.CanRead);
        Assert.Equal(1, count);
        await sender.DrainResourceWorkAsync();
    }

    [Fact]
    public async Task CoreRequestResourceSettlement_DoesNotWaitForOptionalObservers()
    {
        await using var topology = await BuildAsync(false);
        topology.Destination.RegisterRequestHandler("/resource/core", context => context.Data, RequestPolicy.All);
        var (sender, _) = await Connect(topology);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = 0;
        var optionalCompleted = 0;
        foreach (var manager in new[] { topology.Initiator.Router.Links, topology.Responder.Router.Links })
        {
            for (var index = 0; index < LinkManager.ObserverConcurrency; index++)
                manager.Dispatch(() =>
                {
                    if (Interlocked.Increment(ref blocked) == 2 * LinkManager.ObserverConcurrency) started.TrySetResult(true);
                    if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Owned test observer was not released.");
                });
        }
        try
        {
            await Within(started.Task);
            var payload = RandomNumberGenerator.GetBytes(20000);
            var receipt = sender.Request("/resource/core", payload,
                responseCallback: _ => Interlocked.Increment(ref optionalCompleted), timeout: 20);
            Assert.NotNull(receipt);
            Assert.Equal(payload, Assert.IsType<byte[]>(await Within(receipt.ResponseTask, 15)));
            Assert.Equal(RequestStatus.Ready, receipt.Status);
            Assert.Equal(1, receipt.Progress);
            Assert.Equal(0, optionalCompleted);
        }
        finally { release.Set(); }
        await Until(() => Volatile.Read(ref optionalCompleted) == 1);
    }

    [Fact]
    public async Task AssemblyCancellation_DisposeJoinsWorker_AndSettlesOnceWithoutStorage()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, receiver) = await Connect(topology);
            receiver.ResourcePolicy = new ResourcePolicy { MemoryThreshold = 1, StorageDirectory = directory };
            var body = new byte[Resource.MaxEfficientSize];
            byte[] random = [3, 4, 5, 6];
            var encrypted = sender.Encrypt([0, 0, 0, 0, .. Bz2.Compress(body)]);
            var hash = Resource.ComputeHash(body, random);
            var advertisement = new ResourceAdvertisement
            {
                TransferSize = encrypted.Length, DataSize = body.Length, Parts = 1,
                Hash = hash, OriginalHash = hash, RandomHash = random, Hashmap = Resource.MapHash(encrypted, random),
                SegmentIndex = 1, TotalSegments = 1, Flags = 3,
            };
            var terminal = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            Resource resource;
            lock (receiver.Gate)
            {
                resource = Resource.Accept(receiver, advertisement, result =>
                {
                    Interlocked.Increment(ref count);
                    terminal.TrySetResult(result);
                }, null, null)!;
                Assert.NotNull(resource);
                resource.ReceivePart(Packet.Create(PacketType.Data, DestinationType.Link, receiver.LinkId,
                    encrypted, LinkContext.Resource), encrypted.Length + Packet.Header1Size);
                Assert.Equal(ResourceStatus.Assembling, resource.Status);
                resource.CancelLocked();
            }
            Assert.Equal(ResourceStatus.Failed, (await Within(terminal.Task)).Status);
            await resource.DisposeAsync();
            await receiver.DrainResourceWorkAsync();
            resource.Cancel();
            Assert.Equal(1, count);
            Assert.Null(resource.Data);
            Assert.False(resource.IsFileBacked);
            Assert.False(Directory.Exists(directory));
            Assert.Equal(LinkStatus.Active, receiver.Status);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Cancellation_ReleasesFollowingQueuedResourceSynchronously()
    {
        await using var topology = await BuildAsync(false);
        var (sender, receiver) = await Connect(topology);
        receiver.ResourceStrategy = ResourceStrategy.AcceptNone;
        var terminals = 0;
        lock (sender.Gate)
        {
            var first = sender.SendResource(new byte[100], concluded: _ => Interlocked.Increment(ref terminals));
            var second = sender.SendResource(new byte[100], concluded: _ => Interlocked.Increment(ref terminals));
            var third = sender.SendResource(new byte[100], concluded: _ => Interlocked.Increment(ref terminals));
            Assert.Equal(ResourceStatus.Queued, second.Status);
            Assert.Equal(ResourceStatus.Queued, third.Status);
            first.Cancel();
            Assert.Equal(ResourceStatus.Failed, first.Status);
            Assert.Equal(ResourceStatus.Advertised, second.Status);
            second.Cancel();
            Assert.Equal(ResourceStatus.Failed, second.Status);
            Assert.Equal(ResourceStatus.Advertised, third.Status);
            third.Cancel();
            first.Cancel();
            second.Cancel();
            third.Cancel();
        }
        await Until(() => Volatile.Read(ref terminals) == 3);
    }

    private sealed class PatternInput(long length) : Stream
    {
        private long _position;
        public bool Disposed { get; private set; }
        public int LargestRead { get; private set; }
        public long BytesRead { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => !Disposed;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            var read = (int)Math.Min(count, length - _position);
            for (var index = 0; index < read; index++) buffer[offset + index] = (byte)((_position + index) % 251);
            LargestRead = Math.Max(LargestRead, count);
            BytesRead += read;
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => length + offset, _ => -1 };
            if (next < 0 || next > length) throw new IOException("Invalid owned test stream position.");
            return _position = next;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task StreamedMultipart_MinimumSplitBoundary_ReadsAtMostOneSegment_AndSpillsAtOneByte()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, receiver) = await Connect(topology);
            receiver.ResourcePolicy = new ResourcePolicy { MemoryThreshold = 1, StorageDirectory = directory };
            var received = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.ResourceReceived = resource => received.TrySetResult(resource);
            using var source = new PatternInput(Resource.MaxEfficientSize + 1L);
            sender.SendResource(source, autoCompress: false, concluded: resource => sent.TrySetResult(resource));
            await using var result = await Within(received.Task, 60);
            Assert.Equal(ResourceStatus.Complete, (await Within(sent.Task, 60)).Status);
            Assert.Equal(2, result.TotalSegments);
            Assert.True(source.Disposed);
            Assert.Equal(Resource.MaxEfficientSize, source.LargestRead);
            Assert.Equal(Resource.MaxEfficientSize + 1L, source.BytesRead);
            Assert.True(result.IsFileBacked);
            Assert.False(result.HasDataInMemory);
            using var expected = new PatternInput(Resource.MaxEfficientSize + 1L);
            using var actual = result.OpenRead();
            Assert.Equal(SHA256.HashData(expected), SHA256.HashData(actual));
            await result.DisposeAsync();
            Assert.Empty(StorageFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DiskBackedData_LazilyPreservesByteArrayApi_AndDisposalReleasesRetentionQuota()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, receiver) = await Connect(topology);
            receiver.ResourcePolicy = new ResourcePolicy { MemoryThreshold = 1, StorageDirectory = directory, MaxCompletedFileResources = 1 };
            var received = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.ResourceReceived = result => received.TrySetResult(result);
            var payload = RandomNumberGenerator.GetBytes(100);
            sender.SendResource(payload);
            await using var first = await Within(received.Task);
            Assert.True(first.IsFileBacked);
            Assert.False(first.HasDataInMemory);
            Assert.Equal(payload, first.Data);
            Assert.True(first.HasDataInMemory);
            var rejected = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.SendResource(payload, concluded: result => rejected.TrySetResult(result));
            Assert.Equal(ResourceStatus.Rejected, (await Within(rejected.Task)).Status);
            await first.DisposeAsync();
            var following = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.ResourceReceived = result => following.TrySetResult(result);
            sender.SendResource(payload);
            await using var next = await Within(following.Task);
            using var input = next.OpenRead();
            Assert.Equal(SHA256.HashData(payload), SHA256.HashData(input));
            await next.DisposeAsync();
            Assert.Empty(StorageFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task AssemblyStorageFailure_PropagatesTerminalAndDiagnostic_WithoutClosingLink()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var occupied = Path.Combine(directory, "occupied.bin");
        await File.WriteAllBytesAsync(occupied, [1]);
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, receiver) = await Connect(topology);
            receiver.ResourcePolicy = new ResourcePolicy { MemoryThreshold = 1, StorageDirectory = occupied };
            var logs = new ConcurrentQueue<string>();
            topology.Responder.Router.Log = logs.Enqueue;
            byte[] body = [1, 2, 3, 4];
            byte[] random = [3, 4, 5, 6];
            var encrypted = sender.Encrypt([0, 0, 0, 0, .. body]);
            var hash = Resource.ComputeHash(body, random);
            var advertisement = new ResourceAdvertisement
            {
                TransferSize = encrypted.Length, DataSize = body.Length, Parts = 1,
                Hash = hash, OriginalHash = hash, RandomHash = random, Hashmap = Resource.MapHash(encrypted, random),
                SegmentIndex = 1, TotalSegments = 1, Flags = 1,
            };
            var terminal = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            Resource resource;
            lock (receiver.Gate)
            {
                resource = Resource.Accept(receiver, advertisement, result =>
                {
                    Interlocked.Increment(ref count);
                    terminal.TrySetResult(result);
                }, null, null)!;
                Assert.NotNull(resource);
                resource.ReceivePart(Packet.Create(PacketType.Data, DestinationType.Link, receiver.LinkId,
                    encrypted, LinkContext.Resource), encrypted.Length + Packet.Header1Size);
            }
            Assert.Equal(ResourceStatus.Corrupt, (await Within(terminal.Task)).Status);
            await resource.DisposeAsync();
            await receiver.DrainResourceWorkAsync();
            Assert.Equal(1, count);
            Assert.Null(resource.Data);
            Assert.Contains(logs, message => message.Contains("Completing received resource failed:", StringComparison.Ordinal));
            Assert.Equal(LinkStatus.Active, receiver.Status);
            Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(occupied));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(0, ResourceStatus.Corrupt)]
    [InlineData(1, ResourceStatus.Complete)]
    public async Task PackedMetadataLimit_IsInclusive(int metadataLimit, ResourceStatus expected)
    {
        await using var topology = await BuildAsync(false);
        var (sender, receiver) = await Connect(topology);
        receiver.ResourcePolicy = new ResourcePolicy { MaxMetadataSize = metadataLimit };
        byte[] body = [0, 0, 1, 0xc0];
        byte[] random = [3, 4, 5, 6];
        var encrypted = sender.Encrypt([0, 0, 0, 0, .. body]);
        var hash = Resource.ComputeHash(body, random);
        var advertisement = new ResourceAdvertisement
        {
            TransferSize = encrypted.Length, DataSize = body.Length, Parts = 1,
            Hash = hash, OriginalHash = hash, RandomHash = random, Hashmap = Resource.MapHash(encrypted, random),
            SegmentIndex = 1, TotalSegments = 1, Flags = 0x21,
        };
        var terminal = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        Resource resource;
        lock (receiver.Gate)
        {
            resource = Resource.Accept(receiver, advertisement, result => terminal.TrySetResult(result), null, null)!;
            Assert.NotNull(resource);
            resource.ReceivePart(Packet.Create(PacketType.Data, DestinationType.Link, receiver.LinkId,
                encrypted, LinkContext.Resource), encrypted.Length + Packet.Header1Size);
        }
        Assert.Equal(expected, (await Within(terminal.Task)).Status);
        await resource.DisposeAsync();
        if (expected == ResourceStatus.Complete)
        {
            Assert.Empty(resource.Data!);
            Assert.Null(resource.Metadata);
        }
    }

    private sealed class ForwardOnlyInput(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamFileResponse_TransfersMultipartMetadata_AndDisposesOwnedInputAndReceiveStorage(bool forwardOnly)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var topology = await BuildAsync(false);
            using var source = new PatternInput(Resource.MaxEfficientSize + 1L);
            using var offered = forwardOnly ? new ForwardOnlyInput(source) : (Stream)source;
            topology.Destination.RegisterRequestHandler("/stream/file", _ =>
                new StreamFileResponse(offered, new Dictionary<object, object?> { ["name"] = "pattern.bin" }), RequestPolicy.All);
            var (sender, responder) = await Connect(topology);
            sender.ResourcePolicy = new ResourcePolicy { StorageDirectory = directory, MemoryThreshold = 1 };
            responder.ResourcePolicy = new ResourcePolicy { StorageDirectory = directory };
            var receipt = sender.Request("/stream/file", timeout: 60);
            Assert.NotNull(receipt);
            var response = Assert.IsType<byte[]>(await Within(receipt.ResponseTask, 60));
            using var expected = new PatternInput(Resource.MaxEfficientSize + 1L);
            Assert.Equal(SHA256.HashData(expected), SHA256.HashData(response));
            Assert.Equal("pattern.bin", Assert.IsType<Dictionary<object, object?>>(receipt.Metadata)["name"]);
            await Until(() => source.Disposed);
            Assert.True(source.Disposed);
            Assert.True(source.LargestRead <= Resource.MaxEfficientSize);
            await sender.DrainResourceWorkAsync();
            await responder.DrainResourceWorkAsync();
            await Until(() => { lock (responder.Gate) return !responder.HasOutgoingResource; });
            Assert.Empty(StorageFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class CleanupGatedInput : Stream
    {
        public TaskCompletionSource<bool> Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CleaningUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException)
            {
                CleaningUp.TrySetResult(true);
                await ReleaseCleanup.Task;
                throw;
            }
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingCancellationInput : Stream
    {
        public TaskCompletionSource<bool> Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> InCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseCallback { get; } = new();
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var delay = Task.Delay(Timeout.Infinite, cancellationToken);
            using var callback = cancellationToken.Register(() =>
            {
                InCallback.TrySetResult(true);
                if (!ReleaseCallback.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Owned cancellation callback was not released.");
                throw new InvalidOperationException("Owned stream cancellation callback failure.");
            });
            Reading.TrySetResult(true);
            await delay;
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task StreamCancellationCallback_CannotBlockOrThrowThroughTeardown_AndIsJoinedBeforeDisposal()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, _) = await Connect(topology);
            sender.ResourcePolicy = new ResourcePolicy { StorageDirectory = directory };
            var logs = new ConcurrentQueue<string>();
            topology.Initiator.Router.Log = logs.Enqueue;
            using var source = new ThrowingCancellationInput();
            var preparation = sender.SendResourceAsync(source);
            Task? cleanup = null;
            try
            {
                await Within(source.Reading.Task);
                await Task.Run(sender.Teardown).WaitAsync(TimeSpan.FromSeconds(2));
                await Within(source.InCallback.Task);
                Assert.Equal(LinkStatus.Closed, sender.Status);
                cleanup = sender.DrainResourceWorkAsync();
                Assert.False(cleanup.IsCompleted);
                Assert.False(source.Disposed);
            }
            finally { source.ReleaseCallback.Set(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
            await (cleanup ?? sender.DrainResourceWorkAsync()).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(source.Disposed);
            Assert.Contains(logs, message => message.Contains("Resource input cancellation callback failed:", StringComparison.Ordinal));
            Assert.Empty(StorageFiles(directory));
            source.ReleaseCallback.Dispose();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ManagerStop_WaitsBeyondObserverTimeoutForMandatoryResourceCancellationCleanup()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, _) = await Connect(topology);
            sender.ResourcePolicy = new ResourcePolicy { StorageDirectory = directory };
            using var source = new CleanupGatedInput();
            var preparation = sender.SendResourceAsync(source);
            Task? stopping = null;
            try
            {
                await Within(source.Reading.Task);
                stopping = topology.Initiator.Router.Links.StopAsync();
                await Within(source.CleaningUp.Task);
                var elapsed = Task.Delay(TimeSpan.FromMilliseconds(1250));
                Assert.Same(elapsed, await Task.WhenAny(stopping, elapsed));
                Assert.False(stopping.IsCompleted);
                Assert.False(preparation.IsCompleted);
                Assert.False(source.Disposed);
            }
            finally
            {
                source.ReleaseCleanup.TrySetResult(true);
                if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(10));
                else sender.Teardown();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
            Assert.True(source.Disposed);
            Assert.Empty(StorageFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class OwnershipInput(string failure, bool forwardOnly = false) : MemoryStream(new byte[100])
    {
        public int DisposeCount { get; private set; }
        public override bool CanSeek => !forwardOnly && base.CanSeek;
        public override long Length => failure == "length" ? throw new IOException("Owned test length failure.") : base.Length;
        public override long Position
        {
            get => failure == "position" ? throw new IOException("Owned test position failure.") : base.Position;
            set => base.Position = value;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData("length", false, false)]
    [InlineData("position", false, false)]
    [InlineData("length", true, false)]
    [InlineData("position", true, false)]
    [InlineData("length", false, true)]
    [InlineData("position", false, true)]
    [InlineData("length", true, true)]
    [InlineData("position", true, true)]
    public async Task StreamInitializationFailure_DisposesExactlyOnceUnlessRetained(string failure, bool leaveOpen, bool response)
    {
        await using var topology = await BuildAsync(false);
        var (sender, _) = await Connect(topology);
        var source = new OwnershipInput(failure);
        try
        {
            if (response)
                Assert.Throws<IOException>(() => sender.Respond(new byte[16], new StreamFileResponse(source, LeaveOpen: leaveOpen)));
            else
                Assert.Throws<IOException>(() => sender.SendResource(source, leaveOpen: leaveOpen));
            Assert.Equal(leaveOpen ? 0 : 1, source.DisposeCount);
        }
        finally { if (leaveOpen) source.Dispose(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AsyncStreamPolicyRejection_DisposesExactlyOnceUnlessRetained(bool leaveOpen, bool response)
    {
        await using var topology = await BuildAsync(false);
        var (sender, _) = await Connect(topology);
        sender.ResourcePolicy = new ResourcePolicy { MaxParts = 0 };
        var source = new OwnershipInput("", forwardOnly: true);
        try
        {
            if (response)
                Assert.Throws<ArgumentOutOfRangeException>(() => sender.Respond(new byte[16],
                    new StreamFileResponse(source, LeaveOpen: leaveOpen)));
            else
                Assert.Throws<ArgumentOutOfRangeException>(() => { _ = sender.SendResourceAsync(source, leaveOpen: leaveOpen); });
            Assert.Equal(leaveOpen ? 0 : 1, source.DisposeCount);
        }
        finally
        {
            sender.ResourcePolicy = new ResourcePolicy();
            if (leaveOpen) source.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForwardOnlyResponseQueueRejection_DisposesExactlyOnceUnlessRetained(bool leaveOpen)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, _) = await Connect(topology);
            sender.ResourcePolicy = new ResourcePolicy { MaxQueuedResources = 0, MaxIncomingResources = 1, StorageDirectory = directory };
            using var blocker = new WaitingInput();
            var preparation = sender.SendResourceAsync(blocker);
            var source = new OwnershipInput("", forwardOnly: true);
            try
            {
                await Within(blocker.Reading.Task);
                Assert.Throws<InvalidOperationException>(() => sender.Respond(new byte[16],
                    new StreamFileResponse(source, LeaveOpen: leaveOpen)));
                Assert.Equal(leaveOpen ? 0 : 1, source.DisposeCount);
            }
            finally
            {
                sender.Teardown();
                if (leaveOpen) source.Dispose();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
                await sender.DrainResourceWorkAsync();
            }
            Assert.Empty(StorageFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task InactiveStreamRejection_DisposesExactlyOnceUnlessRetained(int operation, bool leaveOpen)
    {
        await using var topology = await BuildAsync(false);
        var (sender, _) = await Connect(topology);
        sender.Teardown();
        var source = new OwnershipInput("");
        try
        {
            if (operation == 2)
                Assert.False(sender.Respond(new byte[16], new StreamFileResponse(source, LeaveOpen: leaveOpen)));
            else if (operation == 1)
                Assert.Throws<InvalidOperationException>(() => { _ = sender.SendResourceAsync(source, leaveOpen: leaveOpen); });
            else
                Assert.Throws<InvalidOperationException>(() => sender.SendResource(source, leaveOpen: leaveOpen));
            Assert.Equal(leaveOpen ? 0 : 1, source.DisposeCount);
        }
        finally { if (leaveOpen) source.Dispose(); }
    }

    [Fact]
    public async Task MalformedAdvertisement_ClosesLinkWithoutWaitingForOptionalObservers()
    {
        await using var topology = await BuildAsync(false);
        var (sender, receiver) = await Connect(topology);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = 0;
        for (var index = 0; index < LinkManager.ObserverConcurrency; index++)
            topology.Responder.Router.Links.Dispatch(() =>
            {
                if (Interlocked.Increment(ref blocked) == LinkManager.ObserverConcurrency) entered.TrySetResult(true);
                if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Owned test observer was not released.");
            });
        try
        {
            await Within(entered.Task);
            sender.SendWithContext([0xc1], LinkContext.ResourceAdv, createReceipt: false, encrypt: true);
            await Until(() => receiver.Status == LinkStatus.Closed);
        }
        finally { release.Set(); }
    }
}
