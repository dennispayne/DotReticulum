using System.Collections.Concurrent;
using System.Security.Cryptography;
using static DotReticulum.Transport.Tests.LinkTests;

namespace DotReticulum.Transport.Tests;

[Trait("Category", "Integration")]
public class ResourceCallbackLifetimeTests
{
    private static async Task<(Link Sender, Link Receiver)> Connect(Topology topology)
    {
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        topology.Destination.LinkEstablished = link =>
        {
            link.ResourceStrategy = ResourceStrategy.AcceptAll;
            accepted.SetResult(link);
        };
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = topology.Initiator.Router.Links.Connect(topology.Destination.Identity,
            topology.Destination.Destination.Hash, link => established.SetResult(link));
        await Within(established.Task);
        return (sender, await Within(accepted.Task));
    }

    private static async Task BlockObservers(LinkManager manager, Task release)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        for (var index = 0; index < LinkManager.ObserverConcurrency; index++)
            manager.DispatchAsync(async () =>
            {
                if (Interlocked.Increment(ref count) == LinkManager.ObserverConcurrency) entered.SetResult(true);
                await release;
            });
        await Within(entered.Task);
    }

    [Theory]
    [InlineData(false, 1234)]
    [InlineData(true, 1234)]
    [InlineData(false, 4 * 1024 * 1024)]
    [InlineData(true, 4 * 1024 * 1024)]
    public async Task QueuedCompletion_SurvivesPeerCloseAndExplicitDisposal_UntilEveryObserverReturns(bool fileBacked, int length)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "callback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, receiver) = await Connect(topology);
            receiver.ResourcePolicy = new ResourcePolicy { MemoryThreshold = fileBacked ? 1 : length + 1024, StorageDirectory = directory };
            await BlockObservers(topology.Responder.Router.Links, release.Task);
            var payload = new byte[length];
            RandomNumberGenerator.Fill(payload);
            var observed = new TaskCompletionSource<(Resource Resource, Stream Reader)>(TaskCreationOptions.RunContinuationsAsynchronously);
            Resource? completed = null;
            Stream? ownedReader = null;
            var calls = 0;
            var diagnostics = new ConcurrentQueue<string>();
            topology.Responder.Router.Log = diagnostics.Enqueue;
            receiver.ResourceReceived = resource =>
            {
                try
                {
                    ownedReader = resource.OpenRead();
                    Assert.False(Monitor.IsEntered(receiver.Gate));
                    completed = resource;
                    Assert.Equal(length, ownedReader.Length);
                    Assert.Equal(length + 3 + DotReticulum.Core.MsgPack.Pack("lifetime").Length, resource.TotalSize);
                    Assert.Equal("lifetime", resource.Metadata);
                    Assert.Equal(fileBacked, resource.IsFileBacked);
                    resource.Cancel();
                    resource.Dispose();
                    resource.DisposeAsync().GetAwaiter().GetResult();
                    using var duringDisposal = resource.OpenRead();
                    Assert.Equal(length, duringDisposal.Length);
                    Interlocked.Increment(ref calls);
                }
                catch (Exception ex)
                {
                    observed.TrySetException(ex);
                    throw;
                }
                throw new InvalidOperationException("intentional lifetime observer failure");
            };
            receiver.ResourceReceived += resource =>
            {
                using var second = resource.OpenRead();
                Assert.Equal(SHA256.HashData(payload), SHA256.HashData(second));
                Interlocked.Increment(ref calls);
                observed.SetResult((resource, ownedReader!));
            };
            var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.SendResource(payload, "lifetime", false, resource =>
            {
                Assert.Equal(ResourceStatus.Complete, resource.Status);
                sender.Teardown();
                sent.SetResult(true);
            });
            await Within(sent.Task, 90);
            await Until(() => receiver.Status == LinkStatus.Closed);
            Assert.Null(completed);
            var filesBeforeCallback = Directory.GetFiles(directory, "resource-*.bin");
            release.SetResult();
            var result = await Within(observed.Task);
            if (fileBacked) Assert.Single(filesBeforeCallback);
            await Until(() => topology.Responder.Router.Links.ExecutingApplicationWork == 0);
            Assert.Equal(2, calls);
            Assert.Contains(diagnostics, message => message.Contains("Resource ") && message.Contains("intentional lifetime observer failure"));
            Assert.Throws<ObjectDisposedException>(() => result.Resource.OpenRead());
            using (result.Reader) Assert.Equal(SHA256.HashData(payload), await SHA256.HashDataAsync(result.Reader));
            Assert.Empty(Directory.GetFiles(directory, "resource-*.bin"));
            await receiver.DrainResourceWorkAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("full")]
    [InlineData("shutdown")]
    public async Task UnobservedOrRejectedCompletion_ReleasesStorageWithoutLeakingQueuedWork(string mode)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "callback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, receiver) = await Connect(topology);
            var manager = topology.Responder.Router.Links;
            receiver.ResourcePolicy = new ResourcePolicy { MemoryThreshold = 1, StorageDirectory = directory };
            var called = 0;
            var diagnostics = new ConcurrentQueue<string>();
            topology.Responder.Router.Log = diagnostics.Enqueue;
            if (mode != "none")
            {
                await BlockObservers(manager, release.Task);
                receiver.ResourceReceived = _ => Interlocked.Increment(ref called);
            }
            if (mode == "full")
                for (var index = 0; index < LinkManager.WorkQueueCapacity; index++) manager.Dispatch(() => { });
            var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.SendResource(new byte[1234], autoCompress: false, concluded: _ => sent.SetResult(true));
            await Within(sent.Task);
            if (mode == "full") Assert.Contains(diagnostics, message => message.Contains("notification rejected"));
            if (mode == "shutdown") await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            else
            {
                sender.Teardown();
                await Until(() => receiver.Status == LinkStatus.Closed);
            }
            await receiver.DrainResourceWorkAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(Directory.GetFiles(directory, "resource-*.bin"));
            release.TrySetResult();
            await Until(() => manager.ExecutingApplicationWork == 0 && manager.QueuedCallbacks == 0);
            Assert.Equal(0, called);
        }
        finally
        {
            release.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunningCompletion_DoesNotBlockTeardownOrShutdown_AndKeepsItsIndependentReader()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "callback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var release = new ManualResetEventSlim();
        try
        {
            await using var topology = await BuildAsync(false);
            var (sender, receiver) = await Connect(topology);
            receiver.ResourcePolicy = new ResourcePolicy { MemoryThreshold = 1, StorageDirectory = directory };
            var entered = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.ResourceReceived = resource =>
            {
                try
                {
                    entered.SetResult(resource);
                    if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Callback was not released.");
                    finished.SetResult(resource.OpenRead());
                }
                catch (Exception ex) { finished.TrySetException(ex); }
            };
            sender.SendResource(new byte[1234], autoCompress: false);
            var resource = await Within(entered.Task);
            await Task.Run(sender.Teardown).WaitAsync(TimeSpan.FromSeconds(2));
            await Until(() => receiver.Status == LinkStatus.Closed);
            await resource.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await topology.Responder.Router.Links.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(Directory.GetFiles(directory, "resource-*.bin"));
            release.Set();
            using var reader = await Within(finished.Task);
            await Until(() => topology.Responder.Router.Links.ExecutingApplicationWork == 0);
            Assert.Equal(1234, reader.Length);
            Assert.Equal(SHA256.HashData(new byte[1234]), await SHA256.HashDataAsync(reader));
            Assert.Throws<ObjectDisposedException>(() => resource.OpenRead());
            Assert.Empty(Directory.GetFiles(directory, "resource-*.bin"));
        }
        finally
        {
            release.Set();
            Directory.Delete(directory, recursive: true);
        }
    }
}
