using System.Text.Json;

namespace DotReticulum.Interfaces.Tests;

/// <summary>Selector oracle: RNS a29bfb51 (1.5.7), GenerateRNodeBleVectors.py. No physical BLE acceptance.</summary>
[Trait("Category", "Unit")]
public sealed class RNodeBleTests
{
    [Fact]
    public void SelectorsAndBondedUartFilteringMatchPinnedPython()
    {
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rnode-ble.json")));
        foreach (var item in oracle.RootElement.GetProperty("selectors").EnumerateArray())
        {
            var selector = RNodeBleSelector.Parse(item.GetProperty("port").GetString()!);
            Assert.Equal(item.GetProperty("name").GetString(), selector.Name);
            Assert.Equal(item.GetProperty("address").GetString(), selector.Address);
        }

        foreach (var item in oracle.RootElement.GetProperty("selections").EnumerateArray())
        {
            var selector = RNodeBleSelector.Parse(item.GetProperty("port").GetString()!);
            Assert.Equal(item.GetProperty("matches").GetBoolean(), selector.Matches(
                item.GetProperty("name").GetString(), item.GetProperty("address").GetString(),
                item.GetProperty("bonded").GetBoolean(), item.GetProperty("uart").GetBoolean()));
        }
        foreach (var item in oracle.RootElement.GetProperty("windows_bonds").EnumerateArray())
            Assert.Equal(item.GetProperty("matches").GetBoolean(), RNodeBleSelector.Parse("ble://RNode Example")
                .Matches("RNode Example", "AA:BB:CC:DD:EE:FF", item.GetProperty("paired").GetBoolean(), uart: true));
    }

    [Fact]
    public async Task WritesAreChunkedToProviderBoundAndFramesCannotInterleave()
    {
        var gatt = new RecordingGatt { MaxWritePayload = 3 };
        await using var stream = await RNodeBleStream.OpenAsync(gatt, 512, default);
        var one = stream.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7 }).AsTask();
        var two = stream.WriteAsync(new byte[] { 8, 9, 10, 11 }).AsTask();
        await Task.WhenAll(one, two).WaitAsync(TestSupport.Patience);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, gatt.Writes.SelectMany(x => x));
        Assert.Equal(new[] { 3, 3, 1, 3, 1 }, gatt.Writes.Select(x => x.Length));
    }

    [Fact]
    public async Task NotificationsPreserveByteOrderAcrossRingWrapAndIgnoreEmptyEvents()
    {
        var gatt = new RecordingGatt();
        await using var stream = await RNodeBleStream.OpenAsync(gatt, 512, default);
        var first = Enumerable.Range(0, 400).Select(i => (byte)i).ToArray();
        gatt.Notify(first);
        var bytes = new byte[300];
        Assert.Equal(300, await stream.ReadAsync(bytes));
        Assert.Equal(first[..300], bytes);
        gatt.Notify([]);
        var second = Enumerable.Range(0, 400).Select(i => (byte)(i + 17)).ToArray();
        gatt.Notify(second);
        var remaining = new byte[500];
        Assert.Equal(500, await stream.ReadAsync(remaining));
        Assert.Equal(first[300..].Concat(second), remaining);
    }

    [Fact]
    public async Task OverflowFaultsInsteadOfDroppingBytesOrGrowingBuffer()
    {
        var gatt = new RecordingGatt();
        await using var stream = await RNodeBleStream.OpenAsync(gatt, 512, default);
        gatt.Notify(new byte[512]);
        gatt.Notify([1]);
        var exception = await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[1]).AsTask());
        Assert.Contains("overflow", exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(new byte[] { 2 }).AsTask());
        Assert.Empty(gatt.Writes);
    }

    [Fact]
    public async Task DisconnectWakesPendingReaderAndOwnedDisposalUnsubscribes()
    {
        var gatt = new RecordingGatt();
        var stream = await RNodeBleStream.OpenAsync(gatt, 512, default);
        var read = stream.ReadAsync(new byte[10]).AsTask();
        gatt.Disconnect(new IOException("radio disconnected"));
        var exception = await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(TestSupport.Patience));
        Assert.Equal("radio disconnected", exception.InnerException!.Message);
        await stream.DisposeAsync();
        await stream.DisposeAsync();
        Assert.Equal(1, gatt.Disposals);
        Assert.Equal(0, gatt.Subscribers);
    }

    [Fact]
    public async Task CancelledReadDoesNotPoisonConnectionAndDisposalWakesReader()
    {
        var gatt = new RecordingGatt();
        var stream = await RNodeBleStream.OpenAsync(gatt, 512, default);
        using var cancellation = new CancellationTokenSource();
        var read = stream.ReadAsync(new byte[1], cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TestSupport.Patience));
        gatt.Notify([7]);
        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal(7, buffer[0]);
        read = stream.ReadAsync(buffer).AsTask();
        await stream.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(TestSupport.Patience));
    }

    [Fact]
    public async Task AmbiguousWriteFailureDoesNotRetryOrSendRemainingChunks()
    {
        var gatt = new RecordingGatt { MaxWritePayload = 2, WriteFailure = new IOException("link lost after accepting bytes") };
        await using var stream = await RNodeBleStream.OpenAsync(gatt, 512, default);
        await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(new byte[] { 1, 2, 3, 4 }).AsTask());
        Assert.Equal(new byte[] { 1, 2 }, Assert.Single(gatt.Writes));
        await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[1]).AsTask());
    }

    [Fact]
    public async Task CancellationDuringWriteEndsSessionAndDisposalJoinsOwnedOperation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatt = new RecordingGatt
        {
            MaxWritePayload = 2,
            DuringWrite = async token =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };
        var stream = await RNodeBleStream.OpenAsync(gatt, 512, default);
        var write = stream.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();
        await started.Task.WaitAsync(TestSupport.Patience);
        await stream.DisposeAsync().AsTask().WaitAsync(TestSupport.Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(new byte[] { 1, 2 }, Assert.Single(gatt.Writes));
        Assert.Equal(1, gatt.Disposals);
    }

    [Fact]
    public async Task FailedAndCancelledNotificationStartupReleaseOwnedGatt()
    {
        var failed = new RecordingGatt { StartFailure = new IOException("notifications denied") };
        await Assert.ThrowsAsync<IOException>(() => RNodeBleStream.OpenAsync(failed, 512, default).AsTask());
        Assert.Equal(1, failed.Disposals);
        Assert.Equal(0, failed.Subscribers);
        var cancelled = new RecordingGatt();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RNodeBleStream.OpenAsync(cancelled, 512, new CancellationToken(true)).AsTask());
        Assert.Equal(1, cancelled.Disposals);
        Assert.Equal(0, cancelled.Subscribers);
    }

    [Fact]
    public void UnsupportedPlatformsRejectBleWithoutAttemptingSerial()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(RNodeTcpConnection.Factory("ble://RNode Example", new()));
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(() => RNodeTcpConnection.Factory("ble://RNode Example", new()));
        }
    }

    private sealed class RecordingGatt : IRNodeBleGatt
    {
        public int MaxWritePayload { get; init; } = 20;
        public List<byte[]> Writes { get; } = [];
        public Exception? StartFailure { get; init; }
        public Exception? WriteFailure { get; init; }
        public Func<CancellationToken, Task>? DuringWrite { get; init; }
        public int Disposals { get; private set; }
        public event Action<ReadOnlyMemory<byte>>? Received;
        public event Action<Exception>? Disconnected;
        public int Subscribers => (Received?.GetInvocationList().Length ?? 0) + (Disconnected?.GetInvocationList().Length ?? 0);
        public ValueTask StartNotificationsAsync(CancellationToken cancellationToken) =>
            StartFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(StartFailure);
        public async ValueTask WriteWithoutResponseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Writes.Add(data.ToArray());
            if (DuringWrite is not null) await DuringWrite(cancellationToken);
            await Task.Yield();
            if (WriteFailure is not null) throw WriteFailure;
        }
        public void Notify(byte[] data) => Received?.Invoke(data);
        public void Disconnect(Exception exception) => Disconnected?.Invoke(exception);
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
