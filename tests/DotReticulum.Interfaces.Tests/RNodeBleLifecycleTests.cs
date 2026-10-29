namespace DotReticulum.Interfaces.Tests;

[Trait("Category", "Integration")]
public sealed class RNodeBleLifecycleTests
{
    [Fact]
    public async Task GattDisconnectReopensAndRedetectsWithoutReplayingOldFrame()
    {
        await using var first = new Parity6RNodeSimulator();
        await using var second = new Parity6RNodeSimulator();
        var firstGatt = new SimulatorGatt(first.Host);
        var secondGatt = new SimulatorGatt(second.Host);
        var attempts = 0;
        var options = new RNodeOptions
        {
            Radio = new() { Frequency = 868000000, Bandwidth = 125000, TxPower = 7, SpreadingFactor = 7, CodingRate = 5 },
            DeviceSettleDelay = TimeSpan.Zero,
            ReconnectDelay = TimeSpan.FromMilliseconds(10),
            ResponseTimeout = TimeSpan.FromSeconds(2),
            PartialFrameTimeout = TimeSpan.FromMilliseconds(20),
        };
        await using var node = new RNodePacketInterface(async token =>
            await RNodeBleStream.OpenAsync(Interlocked.Increment(ref attempts) == 1 ? firstGatt : secondGatt, 2048, token), options);
        await node.StartAsync(default);
        await node.SendAsync(new byte[] { 1, 0xc0, 0xdb, 2 }, default);
        Assert.Equal(new byte[] { 1, 0xc0, 0xdb, 2 }, (await first.NextTransmissionAsync()).Payload);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Connected += () => reconnected.TrySetResult();
        firstGatt.Disconnect();
        await reconnected.Task.WaitAsync(TestSupport.Patience);
        Assert.True(firstGatt.Disposed);
        Assert.Equal(2, attempts);
        Assert.Equal(1L, node.GetDiagnostics()["reconnections"]);
        await node.SendAsync(new byte[] { 3, 0xdb, 0xc0, 4 }, default);
        Assert.Equal(new byte[] { 3, 0xdb, 0xc0, 4 }, (await second.NextTransmissionAsync()).Payload);
        await using var received = node.ReceiveAsync(default).GetAsyncEnumerator();
        await second.RawAsync([0xc0, 0, 5, 0xdb]);
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        await second.RawAsync([0xdc, 0xdb, 0xdd, 6, 0xc0]);
        Assert.True(await received.MoveNextAsync().AsTask().WaitAsync(TestSupport.Patience));
        Assert.Equal(new byte[] { 5, 0xc0, 0xdb, 6 }, received.Current.ToArray());
    }

    private sealed class SimulatorGatt(Stream stream) : IRNodeBleGatt
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _pump = Task.CompletedTask;
        public int MaxWritePayload => 7;
        public bool Disposed { get; private set; }
        public event Action<ReadOnlyMemory<byte>>? Received;
        public event Action<Exception>? Disconnected;
        public ValueTask StartNotificationsAsync(CancellationToken cancellationToken)
        {
            _pump = PumpAsync();
            return ValueTask.CompletedTask;
        }
        public ValueTask WriteWithoutResponseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            stream.WriteAsync(data, cancellationToken);
        private async Task PumpAsync()
        {
            var bytes = new byte[11];
            try
            {
                while (true)
                {
                    var length = await stream.ReadAsync(bytes, _stop.Token);
                    if (length == 0) { Disconnect(); return; }
                    Received?.Invoke(bytes.AsMemory(0, length));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException exception) when (!_stop.IsCancellationRequested) { Disconnected?.Invoke(exception); }
        }
        public void Disconnect() => Disconnected?.Invoke(new IOException("Simulated GATT disconnect"));
        public async ValueTask DisposeAsync()
        {
            if (Disposed) return;
            Disposed = true;
            _stop.Cancel();
            await _pump;
            await stream.DisposeAsync();
            _stop.Dispose();
        }
    }
}
