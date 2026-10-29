using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace DotReticulum.Transport.Tests;

// Independent upstream execution: GenerateParity5ChannelVectors.py,
// a29bfb51c468945c96a1ee22cae0114e993e2391; source hashes are in the fixture.
[Trait("Category", "Unit")]
public sealed class Parity5ChannelTests
{
    internal static JsonElement Vectors { get; } = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity5-channel-vectors.json"))).RootElement;

    public sealed class BytesMessage : MessageBase
    {
        public byte[] Data { get; set; } = [];
        public override ushort MsgType => 0x0101;
        public override byte[] Pack() => Data;
        public override void Unpack(ReadOnlySpan<byte> raw) => Data = raw.ToArray();
    }

    internal sealed class Outlet : IChannelOutlet
    {
        public int Mdu { get; set; } = 431;
        public double Rtt { get; set; } = 0.1;
        public bool IsUsable { get; private set; } = true;
        public Link? Link => null;
        public bool AutoDeliver;
        public bool NullSend { get; set; }
        public bool NullResend { get; set; }
        public bool ThrowOnSend { get; set; }
        public Action? DuringSend { get; set; }
        // Plaintext envelopes only; encrypted retry behavior has its own real-method oracle.
        public ConcurrentQueue<byte[]> Sent { get; } = new();
        public ConcurrentQueue<PacketReceipt> Receipts { get; } = new();
        public ConcurrentQueue<string> Logs { get; } = new();
        public int Teardowns { get; private set; }
        private int _packetId;

        public PacketReceipt? Send(byte[] raw)
        {
            if (ThrowOnSend) throw new IOException("Controlled outlet failure.");
            if (NullSend || !IsUsable) return null;
            Sent.Enqueue(raw.ToArray());
            // Synthetic per-attempt IDs model a fresh packet hash, not a wire encoding.
            byte[] hashInput = [.. raw, 0, 0, 0, 0];
            BinaryPrimitives.WriteInt32BigEndian(hashInput.AsSpan(raw.Length), Interlocked.Increment(ref _packetId));
            var receipt = new PacketReceipt(SHA256.HashData(hashInput), 0, 1);
            Receipts.Enqueue(receipt);
            DuringSend?.Invoke();
            if (Volatile.Read(ref AutoDeliver)) receipt.Conclude(PacketReceiptStatus.Delivered, 0.1);
            return receipt;
        }

        public PacketReceipt? Resend(PacketReceipt receipt, byte[] raw) => NullResend ? null : Send(raw);
        public void Teardown()
        {
            IsUsable = false;
            Teardowns++;
        }
        public void Log(string message) => Logs.Enqueue(message);

        public static void Deliver(PacketReceipt receipt)
        {
            if (receipt.Conclude(PacketReceiptStatus.Delivered, 0.1)) receipt.Delivered?.Invoke(receipt);
        }

        public static void Timeout(PacketReceipt receipt)
        {
            if (receipt.Conclude(PacketReceiptStatus.Failed, 1)) receipt.TimedOut?.Invoke(receipt);
        }
    }

    internal static byte[] Raw(int sequence, byte[]? data = null, ushort messageType = 0x0101)
    {
        data ??= [(byte)(sequence >> 8), (byte)sequence];
        var raw = new byte[6 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(raw, messageType);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(2), (ushort)sequence);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(4), (ushort)data.Length);
        data.CopyTo(raw, 6);
        return raw;
    }

    private static int[] Window(Channel channel) => [channel.WindowSize, channel.WindowMinSize, channel.WindowMaxSize];

    [Theory]
    [InlineData(0)]
    [InlineData(Channel.SeqModulus - 16)]
    public async Task SenderDoesNotOutrunReceiverSequenceWindowWhileAnOldDeliveryProofIsMissing(int firstSequence)
    {
        var outlet = new Outlet { AutoDeliver = true };
        var channel = new Channel(outlet);
        for (var index = 0; index < firstSequence; index++) channel.Send(new BytesMessage());
        outlet.AutoDeliver = false;
        var first = channel.Send(new BytesMessage());
        for (var index = 1; index <= Channel.WindowMax; index++)
        {
            Assert.True(channel.IsReadyToSend);
            var later = channel.Send(new BytesMessage());
            Outlet.Deliver(later.Receipt!);
        }

        Assert.False(channel.IsReadyToSend);
        var error = Assert.Throws<ChannelException>(() => channel.Send(new BytesMessage()));
        Assert.Equal(ChannelExceptionType.LinkNotReady, error.Type);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = channel.WaitForSendAsync(deadline.Token);
        Assert.False(waiting.IsCompleted);
        Outlet.Deliver(first.Receipt!);
        await waiting;
        var resumed = channel.Send(new BytesMessage());
        Assert.Equal((firstSequence + Channel.WindowMax + 1) % Channel.SeqModulus, resumed.Sequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Channel.SeqModulus - 16)]
    public void ReceiverDoesNotAcknowledgeFramesItCannotRetainButReprovesDuplicates(int firstSequence)
    {
        var channel = new Channel(new Outlet());
        channel.RegisterMessageType<BytesMessage>();
        for (var index = 0; index < firstSequence; index++) Assert.True(channel.Receive(Raw(index)));
        var next = (firstSequence + 1) % Channel.SeqModulus;
        var ahead = (firstSequence + Channel.WindowMax + 1) % Channel.SeqModulus;
        Assert.True(channel.Receive(Raw(next)));
        Assert.True(channel.Receive(Raw(next)));
        Assert.False(channel.Receive(Raw(ahead)));
        Assert.True(channel.Receive(Raw(firstSequence)));
        Assert.True(channel.Receive(Raw(firstSequence)));
        Assert.True(channel.Receive(Raw(ahead)));
    }

    [Fact]
    public void EnvelopeLayoutAndSendWrapMatchPinnedReference()
    {
        var outlet = new Outlet { AutoDeliver = true };
        var channel = new Channel(outlet);
        var vectors = Vectors.GetProperty("envelopes").EnumerateArray().ToDictionary(
            item => item.GetProperty("sequence").GetInt32(), item => item.GetProperty("raw").GetString());
        for (var sequence = 0; sequence < Channel.SeqModulus + 2; sequence++)
        {
            var envelope = channel.Send(new BytesMessage { Data = "abc"u8.ToArray() });
            Assert.Equal(sequence % Channel.SeqModulus, envelope.Sequence);
            Assert.True(outlet.Sent.TryDequeue(out var raw));
            Assert.True(outlet.Receipts.TryDequeue(out _));
            if (vectors.TryGetValue(envelope.Sequence, out var expected))
                Assert.Equal(expected, Convert.ToHexStringLower(raw!));
        }
        Assert.True(channel.IsReadyToSend);
    }

    [Fact]
    public void RttBoundaryWindowsMatchEveryReferenceDeliveryRound()
    {
        foreach (var vector in Vectors.GetProperty("windows").EnumerateArray())
        {
            var outlet = new Outlet { Rtt = vector.GetProperty("rtt").GetDouble(), AutoDeliver = true };
            var channel = new Channel(outlet);
            var steps = vector.GetProperty("steps").EnumerateArray().ToArray();
            Assert.Equal(steps[0].EnumerateArray().Select(value => value.GetInt32()), Window(channel));
            foreach (var step in steps.Skip(1))
            {
                channel.Send(new BytesMessage());
                Assert.Equal(step.EnumerateArray().Select(value => value.GetInt32()), Window(channel));
            }
        }
    }

    [Fact]
    public void RetryTimeoutsFiveAttemptsAndExhaustionMatchReference()
    {
        foreach (var vector in Vectors.GetProperty("retries").EnumerateArray())
        {
            var outlet = new Outlet { Rtt = vector.GetProperty("rtt").GetDouble() };
            var channel = new Channel(outlet);
            var envelope = channel.Send(new BytesMessage { Data = "retry"u8.ToArray() });
            foreach (var step in vector.GetProperty("steps").EnumerateArray())
            {
                var receipt = outlet.Receipts.Last();
                Assert.Equal(step.GetProperty("tries").GetInt32(), envelope.Tries);
                Assert.Equal(step.GetProperty("timeout").GetDouble(), receipt.Timeout, 10);
                Assert.Equal(step.GetProperty("window").EnumerateArray().Select(value => value.GetInt32()), Window(channel));
                Outlet.Timeout(receipt);
            }
            Assert.True(vector.GetProperty("closed").GetBoolean());
            Assert.True(channel.Completion.IsCompletedSuccessfully);
            Assert.Equal(1, outlet.Teardowns);
            Assert.Equal(1 + vector.GetProperty("resends").GetInt32(), outlet.Sent.Count);
            // Retries preserve the plaintext envelope, not the ciphertext or packet hash.
            Assert.All(outlet.Sent, raw => Assert.Equal(outlet.Sent.First(), raw));
            Assert.Equal(outlet.Receipts.Count, outlet.Receipts.Select(receipt => Convert.ToHexString(receipt.Hash)).Distinct().Count());
            Assert.Equal(MessageState.Failed, envelope.State);
            Assert.False(channel.IsReadyToSend);
            Assert.All(outlet.Receipts, receipt => { Assert.Null(receipt.Delivered); Assert.Null(receipt.TimedOut); });
        }
    }

    [Fact]
    public void RetryContractsAndSuccessfulRecoveryAdjustWarmFastWindowLikeReference()
    {
        var outlet = new Outlet { Rtt = 0.18, AutoDeliver = true };
        var channel = new Channel(outlet);
        for (var i = 0; i < 40; i++) channel.Send(new BytesMessage());
        outlet.AutoDeliver = false;
        var envelope = channel.Send(new BytesMessage());
        var steps = Vectors.GetProperty("lossWindow").EnumerateArray().ToArray();
        Assert.Equal(steps[0].EnumerateArray().Select(value => value.GetInt32()), Window(channel));
        for (var retry = 1; retry <= 4; retry++)
        {
            Outlet.Timeout(outlet.Receipts.Last());
            Assert.Equal(steps[retry].EnumerateArray().Select(value => value.GetInt32()), Window(channel));
        }
        Outlet.Deliver(outlet.Receipts.Last());
        Assert.Equal(steps[^1].EnumerateArray().Select(value => value.GetInt32()), Window(channel));
        Assert.Equal(MessageState.Delivered, envelope.State);
        Assert.True(channel.IsReadyToSend);
        Assert.Equal(0, outlet.Teardowns);
    }

    [Fact]
    public void InflightCountExtendsOtherReceiptsWithoutShorteningTimeoutsOnDelivery()
    {
        var outlet = new Outlet { Rtt = 0.18 };
        var channel = new Channel(outlet);
        var expected = Vectors.GetProperty("inflightTimeouts").EnumerateArray().ToArray();
        var envelopes = new List<Envelope> { channel.Send(new BytesMessage()) };
        AssertTimeouts(expected[0], envelopes);
        envelopes.Add(channel.Send(new BytesMessage()));
        AssertTimeouts(expected[1], envelopes);
        Outlet.Timeout(envelopes[0].Receipt!);
        AssertTimeouts(expected[2], envelopes);
        Outlet.Deliver(envelopes[1].Receipt!);
        AssertTimeouts(expected[3], envelopes);
        envelopes.Add(channel.Send(new BytesMessage()));
        AssertTimeouts(expected[4], envelopes);
    }

    private static void AssertTimeouts(JsonElement expected, IEnumerable<Envelope> envelopes)
    {
        var current = envelopes.Where(envelope => envelope.State != MessageState.Delivered).ToArray();
        var entries = expected.EnumerateArray().ToArray();
        Assert.Equal(entries.Length, current.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            Assert.Equal(entries[i].GetProperty("sequence").GetInt32(), current[i].Sequence);
            Assert.Equal(entries[i].GetProperty("tries").GetInt32(), current[i].Tries);
            Assert.Equal(entries[i].GetProperty("timeout").GetDouble(), current[i].Receipt!.Timeout, 10);
        }
    }

    [Fact]
    public async Task WrapReorderDuplicatesAndReceiveWindowMatchReference()
    {
        foreach (var vector in Vectors.GetProperty("receive").EnumerateArray())
        {
            var channel = new Channel(new Outlet());
            channel.RegisterMessageType<BytesMessage>();
            for (var sequence = 0; sequence < vector.GetProperty("start").GetInt32(); sequence++)
            {
                channel.Receive(Raw(sequence));
                if (sequence % 128 == 127) await channel.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await channel.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            var received = new List<string>();
            channel.AddMessageHandler(message =>
            {
                received.Add(Convert.ToHexStringLower(((BytesMessage)message).Data));
                return true;
            });
            foreach (var step in vector.GetProperty("steps").EnumerateArray())
            {
                channel.Receive(Raw(step.GetProperty("sequence").GetInt32()));
                await channel.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(step.GetProperty("delivered").EnumerateArray().Select(value => value.GetString()), received);
            }
        }
    }

    [Fact]
    public async Task ConcurrentInWindowDeliveryAndDuplicatesRetainOneOrderedCallbackStream()
    {
        var channel = new Channel(new Outlet());
        channel.RegisterMessageType<BytesMessage>();
        var received = new List<int>();
        channel.AddMessageHandler(message =>
        {
            received.Add(BinaryPrimitives.ReadUInt16BigEndian(((BytesMessage)message).Data));
            return true;
        });
        const int batchSize = Channel.WindowMax + 1;
        for (var batch = 0; batch < 10; batch++)
        {
            var start = batch * batchSize;
            await Task.WhenAll(Enumerable.Range(start, batchSize).Reverse().Select(sequence => Task.Run(() =>
            {
                channel.Receive(Raw(sequence));
                channel.Receive(Raw(sequence));
            })));
            await channel.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(Enumerable.Range(0, 10 * batchSize), received);
    }

    [Fact]
    public async Task NoSubscriberStillAdvancesProtocolAndDelayedProofCallbacksDoNotLeakWindowSlots()
    {
        var outlet = new Outlet();
        var channel = new Channel(outlet);
        channel.RegisterMessageType<BytesMessage>();
        channel.Receive(Raw(0));
        var seen = 0;
        channel.AddMessageHandler(_ => { seen++; return true; });
        channel.Receive(Raw(1));
        await channel.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, seen);

        for (var sequence = 0; sequence < 100; sequence++)
        {
            var envelope = channel.Send(new BytesMessage());
            var receipt = outlet.Receipts.Last();
            // Settle the receipt without dispatching its optional callback.
            Assert.True(receipt.Conclude(PacketReceiptStatus.Delivered, 0.1));
            Assert.Equal(sequence, envelope.Sequence);
            Assert.True(channel.IsReadyToSend);
            Assert.Null(receipt.Delivered);
            Assert.Null(receipt.TimedOut);
        }
    }

    [Fact]
    public async Task StaleReceiptCallbacksCannotRetryOrDeliverReplacementAttempt()
    {
        var outlet = new Outlet();
        var channel = new Channel(outlet);
        var envelope = channel.Send(new BytesMessage());
        var first = outlet.Receipts.Single();
        var staleTimeout = first.TimedOut!;
        var staleDelivered = first.Delivered!;
        Outlet.Timeout(first);
        var replacement = outlet.Receipts.Last();
        Assert.NotEqual(Convert.ToHexString(first.Hash), Convert.ToHexString(replacement.Hash));
        Assert.Same(replacement, envelope.Receipt);
        Assert.Equal(2, envelope.Tries);
        staleTimeout(first);
        staleDelivered(first);
        Assert.Equal(2, envelope.Tries);
        Assert.Equal(MessageState.Sent, envelope.State);
        Outlet.Deliver(replacement);
        Assert.Equal(MessageState.Delivered, envelope.State);
        Assert.True(channel.IsReadyToSend);
        await channel.WaitForSendAsync(CancellationToken.None);
        staleTimeout(first);
        Assert.Equal(2, outlet.Sent.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullOrThrowingSendDoesNotConsumeSequence(bool throws)
    {
        var outlet = new Outlet { ThrowOnSend = throws, NullSend = !throws };
        var channel = new Channel(outlet);
        if (throws) Assert.Throws<IOException>(() => channel.Send(new BytesMessage()));
        else Assert.Throws<ChannelException>(() => channel.Send(new BytesMessage()));
        outlet.ThrowOnSend = outlet.NullSend = false;
        Assert.Equal(0, channel.Send(new BytesMessage()).Sequence);
    }

    [Fact]
    public void FailedResendFailsClosedRatherThanLeavingAnUnretryableEnvelope()
    {
        var outlet = new Outlet { NullResend = true };
        var channel = new Channel(outlet);
        channel.Send(new BytesMessage());
        Outlet.Timeout(outlet.Receipts.Single());
        Assert.True(channel.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, outlet.Teardowns);
        Assert.Contains(outlet.Logs, text => text.Contains("did not retransmit", StringComparison.Ordinal));
    }

    [Fact]
    public void ShutdownDuringSendCannotReattachReceiptCallbacks()
    {
        var outlet = new Outlet();
        var channel = new Channel(outlet);
        outlet.DuringSend = channel.Shutdown;
        Assert.Throws<ChannelException>(() => channel.Send(new BytesMessage()));
        Assert.All(outlet.Receipts, receipt => { Assert.Null(receipt.Delivered); Assert.Null(receipt.TimedOut); });
        Assert.False(channel.IsReadyToSend);
    }

    [Fact]
    public async Task WindowWaitsAreCancellableAndShutdownReleasesEveryWaiter()
    {
        var outlet = new Outlet();
        var channel = new Channel(outlet);
        channel.Send(new BytesMessage());
        channel.Send(new BytesMessage());
        using var cancellation = new CancellationTokenSource();
        var canceled = channel.WaitForSendAsync(cancellation.Token);
        var closed = channel.WaitForSendAsync(CancellationToken.None);
        Assert.False(canceled.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        channel.Shutdown();
        await Assert.ThrowsAsync<IOException>(() => closed);
    }

    [Fact]
    public async Task StalledHandlerHasBoundedQueueAndDoesNotBlockProtocolShutdown()
    {
        var outlet = new Outlet();
        var channel = new Channel(outlet);
        channel.RegisterMessageType<BytesMessage>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        channel.AddMessageHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return true;
        });
        try
        {
            channel.Receive(Raw(0));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 1; i <= Channel.MaximumPendingMessages + 1; i++) channel.Receive(Raw(i));
            Assert.True(channel.Completion.IsCompletedSuccessfully);
            Assert.Equal(1, outlet.Teardowns);
            Assert.False(channel.HandlerCompletion.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await channel.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CallbackExceptionsAreLoggedAndOtherHandlersContinue()
    {
        var outlet = new Outlet();
        var channel = new Channel(outlet);
        channel.RegisterMessageType<BytesMessage>();
        var received = 0;
        channel.AddMessageHandler(_ => throw new InvalidOperationException("channel observer marker"));
        channel.AddMessageHandler(_ => { received++; return true; });
        channel.Receive(Raw(0));
        await channel.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, received);
        Assert.Contains(outlet.Logs, text => text.Contains("channel observer marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedInputDoesNotAdvanceSequenceAndDeclaredLengthMatchesReferenceTolerance()
    {
        var outlet = new Outlet();
        var channel = new Channel(outlet);
        channel.RegisterMessageType<BytesMessage>();
        byte[]? received = null;
        channel.AddMessageHandler(message => { received = ((BytesMessage)message).Data; return true; });
        channel.Receive([1, 2, 3]);
        channel.Receive(Raw(0, messageType: 0x0202));
        channel.Receive(new byte[outlet.Mdu + 1]);
        var tolerance = Vectors.GetProperty("ignoredDeclaredLength");
        var raw = Convert.FromHexString(tolerance.GetProperty("raw").GetString()!);
        channel.Receive(raw);
        await channel.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Convert.FromHexString(tolerance.GetProperty("data").GetString()!), received);
        Assert.Equal(3, outlet.Logs.Count);
    }

    [Fact]
    public void LargerMduStillRejectsEnvelopeLengthFieldOverflowWithoutLosingSequence()
    {
        var outlet = new Outlet { Mdu = 262000 };
        var channel = new Channel(outlet);
        Assert.Equal(ushort.MaxValue, channel.Mdu);
        Assert.Equal(ChannelExceptionType.TooBig, Assert.Throws<ChannelException>(() =>
            channel.Send(new BytesMessage { Data = new byte[65536] })).Type);
        Assert.Equal(0, channel.Send(new BytesMessage { Data = new byte[65535] }).Sequence);
        Assert.Equal(65541, outlet.Sent.Single().Length);
    }
}
