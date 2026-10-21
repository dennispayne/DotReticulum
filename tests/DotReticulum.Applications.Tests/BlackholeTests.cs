using System.Text.Json;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

// Differential fixture executes unmodified methods at a29bfb51c468945c96a1ee22cae0114e993e2391:
// Transport.py:3984-4115, Discovery.py:883-966, Reticulum.py:1998-2041.
// SHA-256 anchors for all five reviewed source files are in Fixtures/Blackhole.json.
// Generator's injected clock/link adapter is explicitly not live-node evidence.
public sealed class BlackholeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), "Blackhole-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(1000);
    private static readonly byte[] Local = Hash(0xaa);
    private static readonly byte[] Remote = Hash(0xbb);
    private static readonly byte[] Relay = Hash(0xcc);
    private static byte[] Hash(byte value) => Enumerable.Repeat(value, 16).ToArray();
    private BlackholeStore Store(IEnumerable<byte[]>? sources = null) => new(_directory, Local, sources ?? [Remote], _clock);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    private static byte[] Fixture(string property)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Blackhole.json")));
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", json.RootElement.GetProperty("upstream").GetString());
        return Convert.FromHexString(json.RootElement.GetProperty(property).GetString()!);
    }

    private static byte[] List(byte[] hash, byte[]? source = null, object? until = null, object? reason = null) =>
        MsgPack.Pack(new Dictionary<object, object?>
        {
            [hash] = new Dictionary<object, object?> { ["source"] = source ?? Remote, ["until"] = until, ["reason"] = reason },
        });

    [Fact]
    public void LocalManagementPublicationAndPersistenceMatchReference()
    {
        var store = Store();
        Assert.True(store.BlackholeIdentity(Hash(1), reason: "local Δ"));
        Assert.Null(store.BlackholeIdentity(Hash(1), reason: "ignored"));
        Assert.False(store.BlackholeIdentity([1]));
        Assert.True(store.BlackholeIdentity(Hash(2), 2000, "temporary"));
        Assert.Equal(Fixture("local_publication"), store.PackPublication());
        Assert.Equal(Fixture("local_file"), File.ReadAllBytes(Path.Combine(_directory, "local")));
        Assert.Equal(store.PackPublication(), MsgPack.Pack(store.PublicationValue()));
        Assert.Equal(store.PackPublication(), Store().PackPublication());
        Assert.True(store.IsBlackholed(Hash(1)));
        Assert.Throws<ArgumentException>(() => store.IsBlackholed([1]));
        Assert.False(store.UnblackholeIdentity([1]));
        Assert.Null(store.UnblackholeIdentity(Hash(255)));
        Assert.True(store.UnblackholeIdentity(Hash(1)));
        Assert.False(Store().IsBlackholed(Hash(1)));
    }

    [Fact]
    public void RemoteAdditiveMergeConflictAndRestartProvenanceMatchReference()
    {
        var store = Store();
        store.BlackholeIdentity(Hash(1), reason: "local Δ");
        store.BlackholeIdentity(Hash(2), 2000, "temporary");
        Assert.Equal(4, store.ApplySourceUpdate(Remote, Fixture("remote_publication")));
        Assert.Equal(Fixture("merged_publication"), store.PackPublication());
        Assert.Equal(Fixture("source_file"), File.ReadAllBytes(Path.Combine(_directory, Convert.ToHexStringLower(Remote))));
        var relayed = Assert.Single(store.Snapshot(), e => e.IdentityHash.SequenceEqual(Hash(3)));
        Assert.Equal(Relay, relayed.Source);
        Assert.Equal(Remote, relayed.ObtainedFrom);
        Assert.Equal(0, store.ApplySourceUpdate(Remote, MsgPack.Pack(new Dictionary<object, object?>())));
        Assert.Equal(Fixture("after_failed_and_empty"), store.PackPublication());
        Assert.Equal(0, store.ApplySourceUpdate(Remote, List(Hash(3), reason: "changed ignored")));
        Assert.Equal(Fixture("after_conflict_file"), File.ReadAllBytes(Path.Combine(_directory, Convert.ToHexStringLower(Remote))));
        store.Reload();
        AssertEquivalentMaps(Fixture("reloaded_publication"), store.PackPublication());
        var reloaded = Assert.Single(store.Snapshot(), e => e.IdentityHash.SequenceEqual(Hash(3)));
        Assert.Equal(Remote, reloaded.Source);
        Assert.Equal("local Δ", Assert.Single(store.Snapshot(), e => e.IdentityHash.SequenceEqual(Hash(1))).Reason);
        Assert.True(store.UnblackholeIdentity(Hash(3)));
        store.Reload();
        Assert.True(store.IsBlackholed(Hash(3)));
        Assert.False(Store([]).IsBlackholed(Hash(3)));
    }

    [Fact]
    public void ExpiryMatchesUpstreamStrictMaintenanceAndRestartRules()
    {
        var store = Store();
        store.ApplySourceUpdate(Remote, Fixture("remote_publication"));
        // Updater adds expired records. Periodic maintenance expires truthy timestamps
        // strictly after expiry, while restart only restores timestamps strictly in the future.
        Assert.Equal(5, store.Snapshot().Count);
        Assert.Equal(1, store.RemoveExpired());
        Assert.True(store.IsBlackholed(Hash(5))); // until=0 is falsey during maintenance
        Assert.True(store.IsBlackholed(Hash(6))); // equal time is not yet expired
        _clock.Now = 1000.01;
        Assert.Equal(1, store.RemoveExpired());
        Assert.False(store.IsBlackholed(Hash(6)));
        store.Reload();
        Assert.False(store.IsBlackholed(Hash(5)));
        Assert.True(store.IsBlackholed(Hash(3)));
    }

    [Fact]
    public void LocalAndObtainedProvenanceCannotBeConfusedByPublishedSource()
    {
        var store = Store();
        store.ApplySourceUpdate(Remote, List(Hash(3), Local, reason: "supplier claims local"));
        store.BlackholeIdentity(Hash(1));
        var localFile = Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(File.ReadAllBytes(Path.Combine(_directory, "local"))));
        Assert.Single(localFile);
        Assert.True(localFile.ContainsKey(Convert.ToHexString(Hash(1))));
        store.Reload();
        Assert.Equal(Remote, Assert.Single(store.Snapshot(), e => e.IdentityHash.SequenceEqual(Hash(3))).Source);
    }

    [Fact]
    public void MalformedUnauthorizedAndOversizedUpdatesAreAtomic()
    {
        var store = Store();
        store.BlackholeIdentity(Hash(1));
        var before = store.PackPublication();
        Assert.Throws<UnauthorizedAccessException>(() => store.ApplySourceUpdate(Relay, List(Hash(2))));
        var textual = MsgPack.Pack(new Dictionary<object, object?> { [Convert.ToHexString(Hash(2))] = new Dictionary<object, object?>() });
        foreach (var invalid in new[]
        {
            new byte[] { 0xc0 }, new byte[] { 0x81 }, new byte[] { 0xc1 }, textual,
            List([2]), List(Hash(2), [3]), List(Hash(2), until: double.NaN),
            List(Hash(2), until: double.PositiveInfinity), List(Hash(2), until: "tomorrow"),
            List(Hash(2), reason: 7), List(Hash(2), reason: new string('x', 4097)),
            Fixture("remote_publication")[..^1], Fixture("remote_publication").Concat(new byte[] { 0 }).ToArray(),
        })
        {
            var exception = Record.Exception(() => store.ApplySourceUpdate(Remote, invalid));
            Assert.True(exception is FormatException or InvalidDataException, exception?.ToString() ?? "Malformed input was accepted.");
            Assert.Equal(before, store.PackPublication());
        }

        Assert.Throws<InvalidDataException>(() => store.ApplySourceUpdate(Remote, new byte[4 * 1024 * 1024 + 1]));
        Assert.Throws<InvalidDataException>(() => store.ApplySourceUpdate(Remote, new byte[] { 0xde, 0x27, 0x11 }));
        // Two identical binary keys remain a schema error, not silent last-write-wins.
        var one = List(Hash(2));
        var repeated = new byte[] { 0x82 }.Concat(one[1..]).Concat(one[1..]).ToArray();
        Assert.Throws<FormatException>(() => store.ApplySourceUpdate(Remote, repeated));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.BlackholeIdentity(Hash(2), double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.BlackholeIdentity(Hash(2), reason: new string('x', 4097)));
        Assert.Equal(before, store.PackPublication());
    }

    [Fact]
    public void CorruptionAndPersistenceFailuresAreExplicitAndNeverErasePolicy()
    {
        var store = Store();
        store.BlackholeIdentity(Hash(1));
        var before = store.PackPublication();
        File.WriteAllBytes(Path.Combine(_directory, Convert.ToHexStringLower(Remote)), [0x81]);
        Assert.Throws<InvalidDataException>(store.Reload);
        Assert.Equal(before, store.PackPublication());
        Assert.Throws<InvalidDataException>(() => Store());
        File.Delete(Path.Combine(_directory, Convert.ToHexStringLower(Remote)));
        Directory.CreateDirectory(Path.Combine(_directory, "local.tmp"));
        Assert.True(Record.Exception(() => store.BlackholeIdentity(Hash(2))) is IOException or UnauthorizedAccessException);
        Assert.Equal(before, store.PackPublication());
        Assert.False(store.IsBlackholed(Hash(2)));
    }

    [Fact]
    public async Task ConcurrentMutationsAndDetachedSnapshotsSurviveRestart()
    {
        var store = Store();
        var changed = 0;
        var diagnostics = 0;
        store.Diagnostic = (_, _) => Interlocked.Increment(ref diagnostics);
        store.Changed += () => throw new InvalidOperationException("observer failed");
        store.Changed += () => Interlocked.Increment(ref changed);
        await Task.WhenAll(Enumerable.Range(1, 40).Select(i => Task.Run(() => store.BlackholeIdentity(Hash((byte)i), reason: "concurrent"))));
        Assert.Equal(40, changed);
        Assert.Equal(40, diagnostics);
        var snapshot = store.Snapshot();
        snapshot[0].IdentityHash[0] ^= 255;
        snapshot[0].Source[0] ^= 255;
        Assert.Equal(40, Store().Snapshot().Count);
        Assert.All(store.Snapshot(), entry => Assert.Equal(Local, entry.Source));
        Assert.True(store.IsBlackholed(Hash(1)));
        Assert.False(Directory.EnumerateFiles(_directory, "*.tmp").Any());
    }

    [Fact]
    public async Task FetchRequiresAuthenticatedConfiguredSupplierAndPreservesFailures()
    {
        var store = Store();
        store.BlackholeIdentity(Hash(1));
        var before = store.PackPublication();
        var calls = 0;
        var service = new BlackholeService(store, new BlackholeOptions { Sources = [Remote] }, (_, _) =>
        {
            calls++;
            return Task.FromResult(new BlackholeFetchResponse(Relay, List(Hash(2))));
        }, _clock);
        Assert.False((await service.UpdateSourceAsync(Remote)).Succeeded);
        Assert.Equal(before, store.PackPublication());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateSourceAsync(Relay));
        Assert.Equal(1, calls);
        foreach (var failure in new Exception[] { new IOException("no path"), new TimeoutException("failed request"), new FormatException("bad response") })
        {
            service = new BlackholeService(store, new BlackholeOptions(), (_, _) => Task.FromException<BlackholeFetchResponse>(failure), _clock);
            Assert.False((await service.UpdateSourceAsync(Remote)).Succeeded);
            Assert.Equal(before, store.PackPublication());
        }

        service = new BlackholeService(store, new BlackholeOptions(), (_, _) =>
            Task.FromResult(new BlackholeFetchResponse(Remote, List(Hash(2)))), _clock);
        Assert.Equal(new BlackholeUpdateResult(true, 1, null), await service.UpdateSourceAsync(Remote));
        Assert.True(Store().IsBlackholed(Hash(2)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UpdateSourceAsync(Remote, canceled.Token));
    }

    [Fact]
    public async Task SchedulerUsesInitialWaitJobIntervalAndUpdateInterval()
    {
        var store = Store();
        var calls = 0;
        var service = new BlackholeService(store, new BlackholeOptions { Sources = [Remote], UpdateInterval = TimeSpan.FromMinutes(2) },
            (_, _) => { calls++; return Task.FromResult(new BlackholeFetchResponse(Remote, List(Hash(2)))); }, _clock);
        await service.TickAsync();
        Assert.Equal(0, calls);
        _clock.Now += 20;
        await service.TickAsync();
        Assert.Equal(1, calls);
        _clock.Now += 60;
        await service.TickAsync();
        Assert.Equal(1, calls);
        _clock.Now += 1;
        await service.TickAsync();
        Assert.Equal(1, calls);
        _clock.Now += 61;
        await service.TickAsync();
        Assert.Equal(2, calls);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackholeService(store,
            new BlackholeOptions { UpdateInterval = TimeSpan.FromSeconds(119) }, (_, _) => throw new NotSupportedException()));
    }

    [Fact]
    public async Task SourceTimeoutIsBoundedAndCancellationPreservesExistingPolicy()
    {
        var store = Store();
        store.BlackholeIdentity(Hash(1));
        var observedCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new BlackholeService(store, new BlackholeOptions(), async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { observedCancellation.TrySetResult(token.IsCancellationRequested); }
            throw new InvalidOperationException("Infinite delay unexpectedly completed.");
        }, _clock);
        var update = service.UpdateSourceAsync(Remote);
        _clock.Now += 25;
        var result = await update.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Succeeded);
        Assert.Contains("25 seconds", result.Error);
        Assert.True(await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(store.IsBlackholed(Hash(1)));
    }

    [Fact]
    public async Task FailedPathRetriesNextJobButFailedCreatedLinkWaitsUpdateInterval()
    {
        var store = Store();
        var calls = 0;
        var service = new BlackholeService(store, new BlackholeOptions { Sources = [Remote], UpdateInterval = TimeSpan.FromMinutes(2) },
            (_, _) => { calls++; throw new IOException("No path"); }, _clock);
        _clock.Now += 20;
        await service.TickAsync();
        _clock.Now += 61;
        await service.TickAsync();
        Assert.Equal(2, calls);
        service = new BlackholeService(store, new BlackholeOptions { Sources = [Remote], UpdateInterval = TimeSpan.FromMinutes(2) },
            (_, _) => { calls++; throw new BlackholeFetchException("Request failed after link creation", linkEstablished: true); }, _clock);
        _clock.Now += 20;
        await service.TickAsync();
        Assert.Equal(3, calls);
        _clock.Now += 61;
        await service.TickAsync();
        Assert.Equal(3, calls);
        _clock.Now += 61;
        await service.TickAsync();
        Assert.Equal(4, calls);
    }

    [Fact]
    public void PublicationDestinationAndRequestPayloadMatchReference()
    {
        Assert.Equal(Fixture("destination_hash"), BlackholeService.SourceDestinationHash(Remote));
        Assert.Equal(Fixture("request_payload"), MsgPack.Pack(new object?[] { 1000.0, Identity.TruncatedHash(System.Text.Encoding.UTF8.GetBytes("/list")), null }));
        using var identity = Identity.Generate();
        var store = new BlackholeStore(_directory, identity.Hash);
        var disabled = new BlackholeService(store, new BlackholeOptions(), (_, _) => throw new NotSupportedException());
        Assert.Null(disabled.RegisterPublisher(identity, _ => throw new InvalidOperationException()));
        var enabled = new BlackholeService(store, new BlackholeOptions { Publish = true }, (_, _) => throw new NotSupportedException());
        LocalDestination? registered = null;
        var destination = enabled.RegisterPublisher(identity, value => registered = value);
        Assert.Same(destination, registered);
        Assert.NotNull(destination);
        Assert.Equal(BlackholeService.SourceDestinationHash(identity.Hash), destination.Destination.Hash);
        Assert.True(destination.AcceptLinks);
    }

    [Fact]
    public async Task NativePublisherAndFetcherExchangeUnwrappedMapOverAuthenticatedLink()
    {
        var port = ReticulumNodeTests.FreePort();
        var server = ReticulumNode.Create(DotReticulum.Applications.Rnsd.RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
            [interfaces]
              [[Server]]
                enabled = yes
                type = TCPServerInterface
                listen_ip = 127.0.0.1
                listen_port = {port}
            """), Path.Combine(_directory, "server-config"));
        var client = ReticulumNode.Create(DotReticulum.Applications.Rnsd.RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
            [interfaces]
              [[Client]]
                enabled = yes
                type = TCPClientInterface
                target_host = 127.0.0.1
                target_port = {port}
            """), Path.Combine(_directory, "client-config"));
        try
        {
            await server.StartAsync();
            await client.StartAsync();
            using var identity = Identity.Generate();
            var serverStore = new BlackholeStore(Path.Combine(_directory, "server"), identity.Hash);
            serverStore.BlackholeIdentity(Hash(1), reason: "published");
            var publisher = new BlackholeService(serverStore, new BlackholeOptions { Publish = true }, (_, _) => throw new NotSupportedException());
            var destination = publisher.RegisterPublisher(identity, server.Router.RegisterLocalDestination)!;
            await Task.Delay(250);
            await server.Router.AnnounceAsync(destination);
            var store = new BlackholeStore(Path.Combine(_directory, "client"), Local, [identity.Hash]);
            var subscriber = new BlackholeService(store, new BlackholeOptions { Sources = [identity.Hash] }, BlackholeService.NativeFetcher(client));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await subscriber.UpdateSourceAsync(identity.Hash, timeout.Token);
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(1, result.Added);
            Assert.Equal(serverStore.PackPublication(), store.PackPublication());
            Assert.Equal(identity.Hash, Assert.Single(store.Snapshot()).ObtainedFrom);
            // Cross the response Resource boundary, not just the single-packet path.
            serverStore.BlackholeIdentity(Hash(2), reason: new string('r', 600));
            result = await subscriber.UpdateSourceAsync(identity.Hash, timeout.Token);
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(1, result.Added);
            Assert.Equal(serverStore.PackPublication(), store.PackPublication());
            var beforeMalformed = store.PackPublication();
            foreach (var length in new[] { 0, 600 })
            {
                // A text map key must never be reinterpreted as binary by Core's
                // general decoded-map normalisation, on either response path.
                destination.RegisterRequestHandler("/list", _ => new Dictionary<object, object?>
                {
                    [Convert.ToHexString(Hash(3))] = new Dictionary<object, object?>
                    {
                        ["source"] = identity.Hash, ["until"] = null, ["reason"] = new string('m', length),
                    },
                }, RequestPolicy.All);
                result = await subscriber.UpdateSourceAsync(identity.Hash, timeout.Token);
                Assert.False(result.Succeeded);
                Assert.Equal(beforeMalformed, store.PackPublication());
                Assert.False(store.IsBlackholed(Hash(3)));
            }
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static void AssertEquivalentMaps(byte[] expected, byte[] actual)
    {
        var left = Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(expected));
        var right = Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(actual));
        Assert.Equal(left.Count, right.Count);
        foreach (var (key, value) in left)
        {
            Assert.True(right.TryGetValue(key, out var found));
            Assert.Equal(MsgPack.Pack(value), MsgPack.Pack(found));
        }
    }

    private sealed class Clock(double now) : TimeProvider
    {
        private readonly List<ClockTimer> _timers = [];
        private double _now = now;
        public double Now
        {
            get => _now;
            set
            {
                _now = value;
                ClockTimer[] timers;
                lock (_timers) timers = _timers.ToArray();
                foreach (var timer in timers) timer.FireIfDue();
            }
        }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Now);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (_timers) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        private sealed class ClockTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private double _due = double.PositiveInfinity;
            private TimeSpan _period;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? double.PositiveInfinity : clock.Now + dueTime.TotalSeconds;
                _period = period;
                return true;
            }

            public void FireIfDue()
            {
                if (_disposed || clock.Now < _due) return;
                _due = _period == Timeout.InfiniteTimeSpan ? double.PositiveInfinity : clock.Now + _period.TotalSeconds;
                callback(state);
            }

            public void Dispose()
            {
                _disposed = true;
                lock (clock._timers) clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
