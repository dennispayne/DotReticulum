using System.Net;
using System.Net.Sockets;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public class RnsdConfigTests
{
    [Fact]
    public void Parse_ReadsTransportLoggingAndInterfaces()
    {
        var config = RnsdConfigParser.Parse("""
            # comment
            [reticulum]
              enable_transport = Yes
              share_instance = No

            [logging]
              loglevel = 6

            [interfaces]
              [[Server]]
                type = TCPServerInterface
                interface_enabled = yes
                listen_ip = 127.0.0.1
                listen_port = 4242   # trailing comment
                mode = gateway

              [[Client]]
                type = TCPClientInterface
                enabled = no
                target_host = example.org
                target_port = 4243

              [[Radio]]
                type = RNodeInterface
                enabled = no
            """);

        Assert.True(config.EnableTransport);
        Assert.Equal(6, config.LogLevel);
        Assert.Equal(2, config.Interfaces.Count);
        var server = config.Interfaces[0];
        Assert.Equal(("Server", RnsdInterfaceType.TcpServer, true, InterfaceMode.Gateway), (server.Name, server.Type, server.Enabled, server.Mode));
        Assert.Equal(4242, server.GetInt("listen_port", 0));
        Assert.False(config.Interfaces[1].Enabled);
    }

    [Fact]
    public void Parse_DefaultsToNoTransportAndLogLevel4()
    {
        var config = RnsdConfigParser.Parse("[reticulum]\n[interfaces]\n");
        Assert.False(config.EnableTransport);
        Assert.Equal(4, config.LogLevel);
        Assert.Empty(config.Interfaces);
    }

    [Theory]
    [InlineData("[interfaces]\n[[X]]\ntype = AutoInterface\n")]
    [InlineData("[interfaces]\n[[X]]\nenabled = yes\n")]
    [InlineData("[reticulum]\nenable_transport = maybe\n")]
    [InlineData("[logging]\nloglevel = 9\n")]
    [InlineData("key = value\n")]
    [InlineData("[reticulum\n")]
    public void Parse_RejectsInvalidConfigurations(string text) => Assert.Throws<FormatException>(() => RnsdConfigParser.Parse(text));

    [Fact]
    public void ExampleConfig_ParsesAndBuildsInterfaces()
    {
        var config = RnsdConfigParser.Parse(RnsdApp.ExampleConfig);
        Assert.True(config.EnableTransport);
        Assert.NotEmpty(config.Interfaces);
        foreach (var iface in config.Interfaces.Where(i => i.Enabled && i.Type != RnsdInterfaceType.Serial))
            Assert.NotNull(RnsdHost.CreateInterface(iface));
    }
}

public class RnsdAppTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rnsd-test-" + Guid.NewGuid().ToString("N"));

    public RnsdAppTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<TcpClientPacketInterface> ConnectAsync(int port, DateTime deadline)
    {
        while (true)
        {
            var client = new TcpClientPacketInterface("127.0.0.1", port);
            try { await client.StartAsync(CancellationToken.None); return client; }
            catch (Exception) when (DateTime.UtcNow < deadline) { await client.DisposeAsync(); await Task.Delay(50); }
        }
    }

    [Fact]
    public async Task Help_PrintsUsageAndExitsZero()
    {
        var output = new StringWriter();
        Assert.Equal(0, await RnsdApp.RunAsync(["--help"], output, new StringWriter(), CancellationToken.None));
        Assert.Contains("--config", output.ToString());
    }

    [Fact]
    public async Task UnknownArgumentAndMissingConfig_Fail()
    {
        var error = new StringWriter();
        Assert.Equal(1, await RnsdApp.RunAsync(["--bogus"], new StringWriter(), error, CancellationToken.None));
        Assert.Equal(1, await RnsdApp.RunAsync(["--config", Path.Combine(_dir, "missing")], new StringWriter(), error, CancellationToken.None));
        Assert.NotEqual("", error.ToString());
    }

    [Fact]
    public async Task Run_RelaysAnnouncesBetweenClients_ShutsDownGracefully_AndPersistsIdentity()
    {
        var port = FreePort();
        File.WriteAllText(Path.Combine(_dir, "config"), $"""
            [reticulum]
              enable_transport = yes
            [logging]
              loglevel = 5
            [interfaces]
              [[Server]]
                type = TCPServerInterface
                listen_ip = 127.0.0.1
                listen_port = {port}
            """);

        var output = new StringWriter();
        using var stop = new CancellationTokenSource();
        var run = RnsdApp.RunAsync(["--config", _dir], output, new StringWriter(), stop.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        await using var a = await ConnectAsync(port, deadline);
        await using var b = await ConnectAsync(port, deadline);
        await Task.Delay(300);

        var identity = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "dotreticulum", ["tests", "rnsd"], identity);
        await a.SendAsync(Announce.Create(identity, destination).ToArray(), CancellationToken.None);

        using var receiveCts = new CancellationTokenSource(deadline - DateTime.UtcNow);
        Packet? relayed = null;
        await foreach (var raw in b.ReceiveAsync(receiveCts.Token))
        {
            if (Packet.TryParse(raw, out var packet) && packet.PacketType == PacketType.Announce) { relayed = packet; break; }
        }

        Assert.NotNull(relayed);
        Assert.Equal(destination.Hash, relayed.DestinationHash.ToArray());
        Assert.Equal(HeaderType.Header2, relayed.HeaderType);

        await stop.CancelAsync();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("rnsd ready", output.ToString());
        Assert.True(File.Exists(Path.Combine(_dir, "storage", "transport_identity")));
    }
}
