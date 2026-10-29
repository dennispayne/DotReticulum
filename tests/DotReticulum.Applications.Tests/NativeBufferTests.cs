using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Tests.Interop;
using Xunit.Abstractions;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Integration")]
public sealed class NativeBufferTests(ITestOutputHelper log)
{
    private static string? PrerequisiteSkip() =>
        Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1"
            ? "Live pinned-Python interop requires DOTRETICULUM_LIVE=1."
            : string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_BUFFER_CONSUMER"))
                ? "Requires a separately published NativeAOT DOTRETICULUM_BUFFER_CONSUMER; no managed fallback."
                : null;

    public sealed class NativeBufferTheoryAttribute : TheoryAttribute
    {
        public NativeBufferTheoryAttribute() => Skip = PrerequisiteSkip();
    }

    public sealed class NativeBufferSoakFactAttribute : FactAttribute
    {
        public NativeBufferSoakFactAttribute()
        {
            Skip = PrerequisiteSkip();
            if (Skip is null && Environment.GetEnvironmentVariable("DOTRETICULUM_BUFFER_SOAK") != "1")
                Skip = "Bounded native streaming endurance is opt-in: set DOTRETICULUM_BUFFER_SOAK=1.";
        }
    }

    [Trait("Category", "Live")]
    [NativeBufferTheory]
    [InlineData("text", false)]
    [InlineData("random", false)]
    [InlineData("text", true)]
    [InlineData("random", true)]
    public Task PublishedPublicReaderAndWriterExchangeCompressedAndBinaryStreamsWithPinnedPython(string returnMode, bool impair) =>
        ExchangeAsync(returnMode, impair);

    [Trait("Category", "Live")]
    [Trait("Category", "NativeSoak")]
    [NativeBufferSoakFact]
    public Task PublishedPublicStreamsSustainBidirectionalByteExactDeliveryAcrossConstrainedLostFrames() =>
        ExchangeAsync("random", true, inputSize: 16_000_000, compressibleBytes: 8_000_000,
            returnSize: 8_000_000, bitrate: 2_000_000);

    private async Task ExchangeAsync(string returnMode, bool impair, int inputSize = 340_000,
        int compressibleBytes = 190_000, int returnSize = 200_000, int bitrate = 10_000_000)
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
            await using var bridge = impair
                ? new Parity4TransportLiveTests.ImpairedBridge(port, bitrate: bitrate, latencyMilliseconds: 20)
                : null;
            bridge?.Start();
            bridge?.DropNextChannel(fromClient: true);
            var config = PyTool.WriteConfig(Path.Combine(directory, "candidate"), bridge?.Port ?? port);
            var inputPath = Path.Combine(directory, "input.bin");
            var outputPath = Path.Combine(directory, "output.bin");
            var input = new byte[inputSize];
            for (var index = 0; index < compressibleBytes; index++) input[index] = (byte)(index % 31);
            RandomNumberGenerator.Fill(input.AsSpan(compressibleBytes));
            await File.WriteAllBytesAsync(inputPath, input, deadline.Token);
            using var native = new NativeResourceTests.NativePeer(executable: executable, environment: null,
                args: [config, python.HashHex, inputPath, outputPath]);
            var elapsed = Stopwatch.StartNew();
            try
            {
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
                JsonElement received;
                try { received = await python.NextAsync("buffer_done", 0, TimeSpan.FromSeconds(60)); }
                catch (TimeoutException)
                {
                    log.WriteLine("Pinned buffer state: " +
                        await python.Command("bufferstatus", "buffer_status", TimeSpan.FromSeconds(5)));
                    throw;
                }
                Assert.Equal(input.Length, received.GetProperty("size").GetInt32());
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(input)), received.GetProperty("sha256").GetString());
                Assert.True(received.GetProperty("compressed_chunks").GetInt32() > 0, python.Log);
                Assert.True(received.GetProperty("plain_chunks").GetInt32() > 0, python.Log);
                if (bridge is not null)
                {
                    Assert.Equal(1, bridge.Dropped);
                    bridge.DropNextChannel(fromClient: false);
                }
                var sent = await python.Command($"bufsend {returnSize} native-buffer {returnMode}", "buffer_sent", TimeSpan.FromSeconds(60));
                await native.WaitForSuccessAsync(deadline.Token);
                var output = await File.ReadAllBytesAsync(outputPath, deadline.Token);
                Assert.Equal(returnSize, output.Length);
                Assert.Equal(sent.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(output)));
                Assert.Contains("Both public Buffer streams completed with EOF on an active link.", native.Output);
                Assert.Empty(python.Events("error"));
                if (bridge is not null)
                {
                    Assert.Equal(2, bridge.Dropped);
                    Assert.Equal(1, bridge.Sessions);
                }
                log.WriteLine($"Native public Buffer: {inputSize} bytes out, {returnSize} bytes back ({returnMode}), " +
                    $"{bridge?.Dropped ?? 0} Channel frames lost, {elapsed.Elapsed.TotalSeconds:F2}s, " +
                    $"compressed/plain outbound chunks={received.GetProperty("compressed_chunks")}/{received.GetProperty("plain_chunks")}; EOF on active link.");
            }
            finally
            {
                log.WriteLine("Native consumer:\n" + native.Output);
                log.WriteLine("Pinned peer:\n" + python.Log);
                if (bridge is not null) log.WriteLine($"Bridge: {bridge.Sessions} sessions, {bridge.Dropped} dropped frames.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
