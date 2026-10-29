using System.Net;
using DotReticulum.Applications.Rngit;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using Xunit.Abstractions;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Integration")]
public sealed class I2pNativeTests(ITestOutputHelper output)
{
    public sealed class ActualI2pFactAttribute : FactAttribute
    {
        public ActualI2pFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_I2P_SAM")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_NATIVE_EXE")))
                Skip = "Requires an explicitly owned DOTRETICULUM_I2P_SAM router and published DOTRETICULUM_NATIVE_EXE.";
        }
    }

    [Trait("Category", "Live")]
    [ActualI2pFact]
    public async Task PublishedProbeExchangesEncryptedDataAndValidatedProofsAcrossFreshProcesses()
    {
        var sam = Environment.GetEnvironmentVariable("DOTRETICULUM_I2P_SAM")!;
        if (!IPEndPoint.TryParse(sam, out var endpoint) || !IPAddress.IsLoopback(endpoint.Address) ||
            endpoint.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || endpoint.Port == 0)
            throw new InvalidOperationException("The actual I2P test requires an explicitly owned IPv4 loopback SAM endpoint.");
        Assert.Equal(sam, Environment.GetEnvironmentVariable("I2P_SAM_ADDRESS"));
        var native = Environment.GetEnvironmentVariable("DOTRETICULUM_NATIVE_EXE")!;
        Assert.True(File.Exists(native), "The configured published native executable is missing.");
        var directory = Path.Combine(AppContext.BaseDirectory, "i2p-native-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await using var node = ReticulumNode.Create(RnsdConfigParser.Parse("""
                [reticulum]
                  share_instance = no
                  enable_transport = no
                [interfaces]
                  [[Actual I2P receiver]]
                    type = I2PInterface
                    enabled = yes
                    connectable = yes
                """), Path.Combine(directory, "receiver"), (_, message) => output.WriteLine(message));
            using var identity = Identity.Generate();
            using var destination = new LocalDestination(identity,
                Destination.Create(DestinationType.Single, "dotreticulum", ["native", "i2p"], identity))
            {
                ProofStrategy = ProofStrategy.All,
            };
            var received = System.Threading.Channels.Channel.CreateBounded<(byte[] Payload, ReceivedPacket Packet)>(2);
            destination.PacketDecrypted = (data, packet) =>
                received.Writer.WriteAsync((data, packet), deadline.Token);
            node.Router.RegisterLocalDestination(destination);
            await node.StartAsync(deadline.Token);
            var bearer = Assert.IsType<I2pPacketInterface>(Assert.Single(node.Interfaces).Interface);
            while (bearer.Base32Address is null) await Task.Delay(50, deadline.Token);
            var configuration = $"""
                [reticulum]
                  share_instance = no
                  enable_transport = no
                [interfaces]
                  [[Actual I2P candidate]]
                    type = I2PInterface
                    enabled = yes
                    peers = {bearer.Base32Address}
                """;
            var announced = new HashSet<I2pPacketPeer>();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var candidate = Directory.CreateDirectory(Path.Combine(directory, $"candidate-{attempt}")).FullName;
                await File.WriteAllTextAsync(Path.Combine(candidate, "config"), configuration, deadline.Token);
                var probe = RngitProcess.RunAsync(native,
                    ["rnprobe", "--config", candidate, "-v", "-s", "257", "-n", "1", "-t", "90",
                        "dotreticulum.native.i2p", Convert.ToHexStringLower(destination.Destination.Hash)],
                    timeout: TimeSpan.FromSeconds(110), maxOutputBytes: 65536, stop: deadline.Token);

                async Task<I2pPacketPeer?> AnnounceWhenConnectedAsync()
                {
                    while (!probe.IsCompleted)
                    {
                        var peer = bearer.Peers.SingleOrDefault(p => !p.IsInitiator && p.IsConnected && !announced.Contains(p));
                        if (peer is not null)
                        {
                            announced.Add(peer);
                            await node.Router.AnnounceAsync(destination, peer, cancellationToken: deadline.Token);
                            return peer;
                        }
                        await Task.Delay(50, deadline.Token);
                    }
                    return null;
                }

                var announcement = AnnounceWhenConnectedAsync();
                await Task.WhenAll(probe, announcement);
                var result = await probe;
                Assert.True(result.Code == 0, $"Native I2P process {attempt + 1}: {result.Error}\n{System.Text.Encoding.UTF8.GetString(result.Output)}");
                Assert.NotNull(await announcement);
                var delivered = await received.Reader.ReadAsync(deadline.Token);
                Assert.Equal(257, delivered.Payload.Length);
                Assert.Equal(destination.Destination.Hash, delivered.Packet.Packet.DestinationHash.ToArray());
                Assert.False(Assert.IsType<I2pPacketPeer>(delivered.Packet.Source).IsInitiator);
                Assert.Same(await announcement, delivered.Packet.Source);
                Assert.Contains("Sent 1, received 1, packet loss 0%", System.Text.Encoding.UTF8.GetString(result.Output));
            }
            Assert.False(received.Reader.TryRead(out _));
            output.WriteLine("Two fresh isolated published-native rnprobe processes used only I2P: the receiver announced after each actual stream connected, decrypted both 257-byte packets on their respective accepted children, and each native sender validated the signed delivery proof. No TCP/UDP/shared-instance fallback; this is cold-start native integration, not persisted-candidate recovery, Python interoperability or public-overlay acceptance.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
