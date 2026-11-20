using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Tests.Interop;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public sealed class NativeProofProbeTheoryAttribute : TheoryAttribute
{
    public NativeProofProbeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1")
            Skip = "Set DOTRETICULUM_LIVE=1 with the pinned actual Python/RNS environment.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_NATIVE_EXE")))
            Skip = "Set DOTRETICULUM_NATIVE_EXE to the selected published NativeAOT Applications executable.";
    }
}

[Trait("Category", "Integration")]
public sealed class NativeProofProbeTests
{
    [NativeProofProbeTheory]
    [Trait("Category", "Live")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualPrimaryAndNativeProbeProofsBothDirectionsRecoverAfterAbruptTransportRestart(bool implicitProof)
    {
        var root = Path.Combine(Path.GetTempPath(), "native-proof-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var port = FreePort();
        File.WriteAllText(Path.Combine(root, "config"), $"""
            [reticulum]
            share_instance=no
            enable_transport=yes
            respond_to_probes=yes
            use_implicit_proof={implicitProof}
            [interfaces]
            [[Native]]
            type=TCPServerInterface
            enabled=yes
            listen_ip=127.0.0.1
            listen_port={port}
            """);
        NativeResourceTests.NativePeer? native = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            native = new NativeResourceTests.NativePeer("rnsd", "--config", root);
            await WaitForListenerAsync(port, native, stop.Token);
            var implicitOption = implicitProof ? "yes" : "no";
            await using var python = await PyLinkNode.StartAsync("proof-policy", rnsdPort: port, harness: "proof_probe_node.py",
                extraArguments: ["--implicit-proof", implicitOption]);
            var clientRoot = Path.Combine(root, "client");
            Directory.CreateDirectory(clientRoot);
            var clientConfig = RnsdConfigParser.Parse($"""
                [reticulum]
                share_instance=no
                [interfaces]
                [[Native]]
                type=TCPClientInterface
                enabled=yes
                target_host=127.0.0.1
                target_port={port}
                """);
            await using var client = ReticulumNode.Create(clientConfig, clientRoot);
            await client.StartAsync(stop.Token);
            var nativeHash = ReadProbeHash(root);
            for (var lifetime = 0; lifetime < 2; lifetime++)
            {
                if (lifetime != 0)
                {
                    native.Dispose();
                    native = new NativeResourceTests.NativePeer("rnsd", "--config", root);
                    await WaitForListenerAsync(port, native, stop.Token);
                    Assert.Equal(nativeHash, ReadProbeHash(root));
                }
                await python.NextAsync("announce", lifetime, TimeSpan.FromSeconds(45),
                    observation => observation.GetProperty("dest").GetString() == Convert.ToHexStringLower(nativeHash));
                var delivered = await python.Command($"probe {Convert.ToHexStringLower(nativeHash)}", "receipt", TimeSpan.FromSeconds(20));
                Assert.True(delivered.GetProperty("proved").GetBoolean(), python.Log + native.Output);
                Assert.Equal(implicitProof ? 64 : 96, delivered.GetProperty("proof_length").GetInt32());
                await python.Command("announce", "announced", TimeSpan.FromSeconds(10));
                Assert.True(await client.WaitForPathAsync(python.Hash, TimeSpan.FromSeconds(20), stop.Token),
                    python.Log + native.Output);
                using var identity = await client.ResolveIdentityAsync(python.Hash, TimeSpan.FromSeconds(20), stop.Token);
                Assert.NotNull(identity);
                var destination = Destination.Create(DestinationType.Single, "rnstransport", ["probe"], identity);
                Assert.Equal(python.Hash, destination.Hash);
                var packet = Packet.Create(PacketType.Data, DestinationType.Single, python.Hash,
                    identity.Encrypt(System.Text.Encoding.ASCII.GetBytes($"native-hop-probe-{lifetime}")));
                var receipt = await client.Router.SendWithReceiptAsync(packet, identity, TimeSpan.FromSeconds(15), stop.Token);
                Assert.NotNull(receipt);
                Assert.True(await receipt.Completion.WaitAsync(stop.Token), python.Log + native.Output);
                Assert.Equal(implicitProof ? 64 : 96, receipt.ProofPacket!.Payload.Length);
                var attempt = await python.Command($"link {Convert.ToHexStringLower(nativeHash)}", "link_attempt", TimeSpan.FromSeconds(12));
                Assert.False(attempt.GetProperty("active").GetBoolean());
                var healthy = await python.Command($"probe {Convert.ToHexStringLower(nativeHash)}", "receipt", TimeSpan.FromSeconds(20));
                Assert.True(healthy.GetProperty("proved").GetBoolean(), python.Log + native.Output);
                Assert.False(client.Completion.IsCompleted);
            }
        }
        finally
        {
            native?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] ReadProbeHash(string root)
    {
        var key = File.ReadAllBytes(Path.Combine(root, "storage", "transport_identity"));
        try
        {
            using var identity = Identity.FromPrivateKey(key);
            return Destination.Create(DestinationType.Single, "rnstransport", ["probe"], identity).Hash;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForListenerAsync(int port, NativeResourceTests.NativePeer peer, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, token);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline) { await Task.Delay(50, token); }
            catch (SocketException error) { throw new IOException("Selected native rnsd did not listen: " + peer.Output, error); }
        }
    }
}
