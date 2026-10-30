using DotReticulum.Applications;
using DotReticulum.Transport;

if (args is ["--help"])
{
    Console.WriteLine("Usage: ChannelBufferConsumer <config> <destination hash> <input file> <output file>");
    return 0;
}

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: ChannelBufferConsumer <config> <destination hash> <input file> <output file>");
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
await using var node = ReticulumNode.Open(args[0], (_, message) => Console.Error.WriteLine(message));
await node.StartAsync(deadline.Token);
var connection = await ClientLinks.ConnectAsync(node, Convert.FromHexString(args[1]),
    TimeSpan.FromSeconds(30), deadline.Token);
var link = connection.Link ?? throw new IOException(connection.Error);
try
{
    var channel = link.GetChannel();
    using var reader = ChannelBuffer.CreateReader(2, channel);
    await using var writer = ChannelBuffer.CreateWriter(1, channel);
    await using var input = File.OpenRead(args[2]);
    await using var output = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await input.CopyToAsync(writer, deadline.Token);
    await writer.CloseAsync(deadline.Token);
    Console.WriteLine("Outbound public Buffer stream closed.");
    var buffer = new byte[81920];
    long received = 0;
    long nextProgress = 1_000_000;
    while (true)
    {
        var count = await reader.ReadAsync(buffer, deadline.Token);
        if (count == 0) break;
        await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
        received += count;
        if (received >= nextProgress)
        {
            Console.WriteLine($"Inbound public Buffer: {received} bytes; link={link.Status}, channel_completed={channel.Completion.IsCompleted}.");
            nextProgress = (received / 1_000_000 + 1) * 1_000_000;
        }
    }
    if (!reader.EndOfStream || channel.Completion.IsCompleted || link.Status != LinkStatus.Active)
        throw new IOException("Buffer EOF was not received on an active link.");
    Console.WriteLine("Both public Buffer streams completed with EOF on an active link.");
}
finally
{
    await link.TeardownAsync(deadline.Token);
}
return 0;
