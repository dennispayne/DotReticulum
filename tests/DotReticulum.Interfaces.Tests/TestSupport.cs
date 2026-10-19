using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;

namespace DotReticulum.Interfaces.Tests;

internal static class TestSupport
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public static byte[] MakePacket(int tag, int extra = 0)
    {
        var payload = new byte[4 + extra];
        payload[0] = (byte)tag;
        payload[1] = (byte)(tag >> 8);
        payload[2] = 0x7E;
        payload[3] = 0x7D;
        return Packet.Create(PacketType.Data, DestinationType.Single, new byte[16], payload).ToArray();
    }

    public static int TagOf(ReadOnlyMemory<byte> packet) =>
        Packet.Parse(packet).Payload.Span[0] | (Packet.Parse(packet).Payload.Span[1] << 8);

    public static async Task<ReadOnlyMemory<byte>> Next(IAsyncEnumerator<ReadOnlyMemory<byte>> e)
    {
        var move = e.MoveNextAsync().AsTask();
        Assert.True(await move.WaitAsync(Patience), "stream ended unexpectedly");
        return e.Current;
    }

    public static async Task<List<ReadOnlyMemory<byte>>> Take(IAsyncEnumerator<ReadOnlyMemory<byte>> e, int count)
    {
        var list = new List<ReadOnlyMemory<byte>>();
        for (var i = 0; i < count; i++)
        {
            list.Add(await Next(e));
        }

        return list;
    }

    public static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(10);
        }
    }

    public static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    /// <summary>Two connected in-memory duplex streams (what one writes the other reads).</summary>
    public static (Stream A, Stream B) CreateDuplexPair()
    {
        var ab = new Pipe();
        var ba = new Pipe();
        return (new DuplexStream(ba.Reader.AsStream(), ab.Writer.AsStream()), new DuplexStream(ab.Reader.AsStream(), ba.Writer.AsStream()));
    }

    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                output.Dispose();
                input.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
