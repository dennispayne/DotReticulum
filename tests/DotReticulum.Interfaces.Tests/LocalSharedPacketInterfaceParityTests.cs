using System.Net;
using DotReticulum.Interfaces;

namespace DotReticulum.Interfaces.Tests;

[Trait("Category", "Unit")]
public sealed class LocalSharedPacketInterfaceParityTests
{
    [Fact]
    public void SleepPauseUsesMonotonicElapsedTimeAndRefreshesOnIncomingActivity()
    {
        var clock = new TestTimeProvider();
        var state = new LocalSharedClientSleepPauseState(clock, TimeSpan.FromSeconds(12));

        clock.Advance(TimeSpan.FromSeconds(12));
        Assert.False(state.IsPaused);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(state.IsPaused);

        state.RecordActivity();
        Assert.False(state.IsPaused);
        clock.Advance(TimeSpan.FromSeconds(12));
        Assert.False(state.IsPaused);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(state.IsPaused);
    }

    [Fact]
    public async Task AcceptedClientPauseAndStatisticsUseDecodedFramesAndExcludeEmptyKeepalives()
    {
        var clock = new TestTimeProvider();
        var policy = new LocalSharedPlatformPolicy
        {
            PauseAcceptedClientAfterInactivity = true,
            InitiatorKeepaliveInterval = TimeSpan.FromHours(1),
        };
        var server = new LocalSharedServerInterface(
            new IPEndPoint(IPAddress.Loopback, 0), 64, null, policy, clock);
        var connected = new TaskCompletionSource<LocalSharedPacketInterface>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ChildConnected += child => connected.TrySetResult(Assert.IsType<LocalSharedPacketInterface>(child));
        await using (server)
        {
            server.Reserve();
            await server.StartAsync(CancellationToken.None);
            await using var client = new LocalSharedPacketInterface(
                server.LocalEndpoint!, null, null, null, policy);
            await client.StartAsync(CancellationToken.None);
            await using var peer = await connected.Task.WaitAsync(TestSupport.Patience);
            using var timeout = new CancellationTokenSource(TestSupport.Patience);
            await using var fromPeer = peer.ReceiveAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
            await using var fromClient = client.ReceiveAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

            clock.Advance(TimeSpan.FromSeconds(13));
            Assert.True(peer.IsTransmitPaused);
            var firstRead = fromPeer.MoveNextAsync().AsTask();
            await client.SendKeepaliveAsync(timeout.Token);
            await TestSupport.WaitFor(() => !peer.IsTransmitPaused);
            Assert.Equal(0L, client.SentBytes);

            var packet = TestSupport.MakePacket(81);
            await client.SendAsync(packet, timeout.Token);
            Assert.True(await firstRead.WaitAsync(timeout.Token));
            Assert.Equal(packet, fromPeer.Current.ToArray());
            var encodedLength = HdlcPacketFraming.Encode(packet).Length;
            Assert.Equal(packet.Length, peer.ReceivedBytes);
            Assert.Equal((long)encodedLength, client.SentBytes);
            Assert.Equal(0L, client.GetDiagnostics()["tx_write_pending_bytes"]);

            clock.Advance(TimeSpan.FromSeconds(13));
            Assert.True(peer.IsTransmitPaused);
            var clientRead = fromClient.MoveNextAsync().AsTask();
            var sentBeforePause = peer.SentBytes;
            await peer.SendAsync(packet, timeout.Token);
            Assert.Equal(sentBeforePause, peer.SentBytes);

            var secondRead = fromPeer.MoveNextAsync().AsTask();
            await client.SendAsync(packet, timeout.Token);
            Assert.True(await secondRead.WaitAsync(timeout.Token));
            Assert.Equal(packet, fromPeer.Current.ToArray());
            clock.Advance(TimeSpan.FromSeconds(11));
            await peer.SendAsync(packet, timeout.Token);
            Assert.True(await clientRead.WaitAsync(timeout.Token));
            Assert.Equal(packet, fromClient.Current.ToArray());
            Assert.Equal(packet.Length * 2L, peer.ReceivedBytes);
            Assert.Equal(0L, sentBeforePause);
            Assert.Equal((long)encodedLength, peer.SentBytes);
            Assert.Equal(0L, peer.GetDiagnostics()["tx_write_pending_bytes"]);
            Assert.Equal(0L, server.GetDiagnostics()["tx_write_pending_bytes"]);
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan amount) => Interlocked.Add(ref timestamp, amount.Ticks);
    }
}
