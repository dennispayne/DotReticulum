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
await using var node = ReticulumNode.Open(args[0]);
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
    await reader.CopyToAsync(output, deadline.Token);
    if (!reader.EndOfStream || link.Status != LinkStatus.Active)
        throw new IOException("Buffer EOF was not received on an active link.");
    Console.WriteLine("Both public Buffer streams completed with EOF on an active link.");
}
finally
{
    await link.TeardownAsync(deadline.Token);
}
return 0;
