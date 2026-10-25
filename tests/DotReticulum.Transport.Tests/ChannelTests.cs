using System.Security.Cryptography;
using System.Text;
using DotReticulum.Core;
using static DotReticulum.Transport.Tests.LinkTests;

namespace DotReticulum.Transport.Tests;

// Channel and stream Buffer. Wire layouts are checked against bytes produced by upstream RNS.Channel.Envelope / RNS.Buffer.StreamDataMessage; live: LiveLinkTests.
public class ChannelTests
{
    private sealed class TextMessage : MessageBase
    {
        public const ushort Type = 0x0101;
        public string Text { get; set; } = "";
        public override ushort MsgType => Type;
        public override byte[] Pack() => Encoding.UTF8.GetBytes(Text);
        public override void Unpack(ReadOnlySpan<byte> raw) => Text = Encoding.UTF8.GetString(raw);
    }

    private sealed class ReservedMessage : MessageBase
    {
        public override ushort MsgType => 0xF001;
        public override byte[] Pack() => [];
        public override void Unpack(ReadOnlySpan<byte> raw) { }
    }

    private static async Task<(Link Initiator, Link Responder)> ConnectAsync(Topology topo)
    {
        var accepted = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        topo.Destination.LinkEstablished = l => accepted.TrySetResult(l);
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = topo.Initiator.Router.Links.Connect(topo.Destination.Identity, topo.Destination.Destination.Hash, l => established.TrySetResult(l));
        await Within(established.Task);
        return (link, await Within(accepted.Task));
    }

    [Fact]
    public async Task ChannelInitializationCannotProveAnEnvelopeBeforeItsTypesAndHandlerExist()
    {
        await using var topo = await BuildAsync(false);
        var (initiator, responder) = await ConnectAsync(topo);
        var configuring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var initialization = Task.Run(() => responder.GetChannel(channel =>
        {
            configuring.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Channel initialization was not released.");
            channel.RegisterMessageType<TextMessage>();
            channel.AddMessageHandler(message => received.TrySetResult(((TextMessage)message).Text));
        }));
        try
        {
            await configuring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var sender = initiator.GetChannel();
            sender.RegisterMessageType<TextMessage>();
            var envelope = sender.Send(new TextMessage { Text = "first version" });
            await Task.Delay(100);
            Assert.NotEqual(MessageState.Delivered, envelope.State);
        }
        finally { release.Set(); }
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("first version", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void StreamDataMessage_PacksLikeUpstream()
    {
        Assert.Equal("8005616263", Convert.ToHexStringLower(new StreamDataMessage(5, "abc"u8.ToArray(), eof: true).Pack()));

        var compressed = Convert.FromHexString("425a6839314159265359ca3d8dfb000000010420000400200021008283177245385090ca3d8dfb");
        var message = new StreamDataMessage();
        message.Unpack([.. Convert.FromHexString("7fff"), .. compressed]);
        Assert.Equal(0x3FFF, message.StreamId);
        Assert.True(message.Compressed);
        Assert.False(message.Eof);
        Assert.Equal(new string('a', 200), Encoding.ASCII.GetString(message.Data));
    }

    [Fact]
    public async Task Envelope_LayoutMatchesUpstream_AndOrderedDeliveryWorks()
    {
        await using var topo = await BuildAsync(false);
        var (initiator, responder) = await ConnectAsync(topo);
        var rx = responder.GetChannel();
        rx.RegisterMessageType<TextMessage>();
        var got = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rx.AddMessageHandler(m =>
        {
            lock (got)
            {
                got.Add(((TextMessage)m).Text);
                if (got.Count == 40) done.TrySetResult();
            }

            return true;
        });

        var tx = initiator.GetChannel();
        tx.RegisterMessageType<TextMessage>();
        for (var i = 0; i < 40; i++)
        {
            while (!tx.IsReadyToSend) await Task.Delay(5);
            var envelope = tx.Send(new TextMessage { Text = "message " + i });
            Assert.Equal(i, envelope.Sequence);
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Enumerable.Range(0, 40).Select(i => "message " + i), got);
        for (var i = 0; i < 100 && tx.WindowSize < 5; i++) await Task.Delay(20);
        Assert.True(tx.WindowSize > Channel.Window, "window should have grown as deliveries were proven");
    }

    [Fact]
    public async Task Channel_RejectsReservedTooBigAndFullWindow()
    {
        await using var topo = await BuildAsync(false);
        var (initiator, _) = await ConnectAsync(topo);
        var channel = initiator.GetChannel();
        Assert.Equal(ChannelExceptionType.InvalidMessageType, Assert.Throws<ChannelException>(() => channel.RegisterMessageType<ReservedMessage>()).Type);
        channel.RegisterMessageType<TextMessage>();
        Assert.Equal(Link.ComputeMdu(500) - 6, channel.Mdu);
        Assert.Equal(ChannelExceptionType.TooBig, Assert.Throws<ChannelException>(() => channel.Send(new TextMessage { Text = new string('x', channel.Mdu + 1) })).Type);

        // the responder never registers a channel, so nothing proves these and the window (2) fills up
        channel.Send(new TextMessage { Text = "1" });
        channel.Send(new TextMessage { Text = "2" });
        Assert.False(channel.IsReadyToSend);
        Assert.Equal(ChannelExceptionType.LinkNotReady, Assert.Throws<ChannelException>(() => channel.Send(new TextMessage { Text = "3" })).Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Buffer_StreamsCompressibleAndRandomDataBothWays_WithEof(bool viaTransport)
    {
        await using var topo = await BuildAsync(viaTransport);
        var (initiator, responder) = await ConnectAsync(topo);
        var (initiatorReader, initiatorWriter) = ChannelBuffer.CreateBidirectional(1, 2, initiator.GetChannel());
        var (responderReader, responderWriter) = ChannelBuffer.CreateBidirectional(2, 1, responder.GetChannel());

        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("reticulum stream line\n", 6000)));
        var random = new byte[50_000];
        RandomNumberGenerator.Fill(random);
        byte[] payload = [.. text, .. random];

        var received = ReadAllAsync(responderReader);
        await initiatorWriter.WriteAsync(payload);
        await initiatorWriter.CloseAsync();
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await Within(received, 60)));

        var reply = Encoding.ASCII.GetBytes("pong from the responder");
        var back = ReadAllAsync(initiatorReader);
        await responderWriter.WriteAsync(reply);
        await responderWriter.CloseAsync();
        Assert.Equal(reply, await Within(back, 30));
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }
}
