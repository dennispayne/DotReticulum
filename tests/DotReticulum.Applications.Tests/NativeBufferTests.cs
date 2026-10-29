using System.Security.Cryptography;
using DotReticulum.Tests.Interop;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Integration")]
public sealed class NativeBufferTests
{
    public sealed class NativeBufferTheoryAttribute : TheoryAttribute
    {
        public NativeBufferTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1")
                Skip = "Live pinned-Python interop requires DOTRETICULUM_LIVE=1.";
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_BUFFER_CONSUMER")))
                Skip = "Requires a separately published NativeAOT DOTRETICULUM_BUFFER_CONSUMER; no managed fallback.";
        }
    }

    [Trait("Category", "Live")]
    [NativeBufferTheory]
    [InlineData("text")]
    [InlineData("random")]
    public async Task PublishedPublicReaderAndWriterExchangeCompressedAndBinaryStreamsWithPinnedPython(string returnMode)
    {
        var executable = Environment.GetEnvironmentVariable("DOTRETICULUM_BUFFER_CONSUMER")!;
        Assert.True(File.Exists(executable), "The configured native public-Buffer consumer is missing.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var directory = ReticulumNodeTests.TempDir();
        Directory.CreateDirectory(directory);
        var port = ReticulumNodeTests.FreePort();
        try
        {
            await using var python = await PyLinkNode.StartAsync("", tcpServerPort: port);
            var config = PyTool.WriteConfig(Path.Combine(directory, "candidate"), port);
            var inputPath = Path.Combine(directory, "input.bin");
            var outputPath = Path.Combine(directory, "output.bin");
            var input = new byte[340_000];
            for (var index = 0; index < 190_000; index++) input[index] = (byte)(index % 31);
            RandomNumberGenerator.Fill(input.AsSpan(190_000));
            await File.WriteAllBytesAsync(inputPath, input, deadline.Token);
            using var native = new NativeResourceTests.NativePeer(executable: executable, environment: null,
                args: [config, python.HashHex, inputPath, outputPath]);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var interfaces = await python.Command("interfaces", "interfaces", TimeSpan.FromSeconds(5));
                if (interfaces.GetProperty("interfaces").EnumerateArray().Any(item =>
                    item.GetProperty("name").GetString() == "Client on PyTCPServer" && item.GetProperty("online").GetBoolean()))
                    break;
                await Task.Delay(25, deadline.Token);
            }
            await python.Command("announce", "announced", TimeSpan.FromSeconds(5));
            await python.NextAsync("link_up", 0, TimeSpan.FromSeconds(30));
            var received = await python.NextAsync("buffer_done", 0, TimeSpan.FromSeconds(60));
            Assert.Equal(input.Length, received.GetProperty("size").GetInt32());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(input)), received.GetProperty("sha256").GetString());
            Assert.True(received.GetProperty("compressed_chunks").GetInt32() > 0, python.Log);
            Assert.True(received.GetProperty("plain_chunks").GetInt32() > 0, python.Log);
            var sent = await python.Command($"bufsend 200000 native-buffer {returnMode}", "buffer_sent", TimeSpan.FromSeconds(60));
            await native.WaitForSuccessAsync(deadline.Token);
            var output = await File.ReadAllBytesAsync(outputPath, deadline.Token);
            Assert.Equal(200_000, output.Length);
            Assert.Equal(sent.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(output)));
            Assert.Contains("Both public Buffer streams completed with EOF on an active link.", native.Output);
            Assert.Empty(python.Events("error"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
