using System.Net;
using System.Net.Sockets;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public class ReticulumNodeTests
{
    internal static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    internal static string TempDir() => Path.Combine(Path.GetTempPath(), "dotret-node-" + Guid.NewGuid().ToString("N"));

    internal static (ReticulumNode Server, ReticulumNode Client, string Dir) Pair() => Pair(out _);

    internal static (ReticulumNode Server, ReticulumNode Client, string Dir) Pair(out int port)
    {
        port = FreePort();
        var dir = TempDir();
        var server = ReticulumNode.Create(RnsdConfigParser.Parse($"""
            [reticulum]
              enable_transport = no
            [interfaces]
              [[S]]
                type = TCPServerInterface
                listen_ip = 127.0.0.1
                listen_port = {port}
            """), Path.Combine(dir, "server"));
        var client = ReticulumNode.Create(RnsdConfigParser.Parse($"""
            [interfaces]
              [[C]]
                type = TCPClientInterface
                target_host = 127.0.0.1
                target_port = {port}
            """), Path.Combine(dir, "client"));
        return (server, client, dir);
    }

    [Fact]
    public async Task TwoNodes_AnnounceAndResolveIdentity()
    {
        var (server, client, dir) = Pair();
        try
        {
            await server.StartAsync();
            await client.StartAsync();
            var identity = server.LoadOrCreateIdentity("app_identity");
            Assert.Equal(identity.HexHash, server.LoadOrCreateIdentity("app_identity").HexHash);
            var destination = Destination.Create(DestinationType.Single, "dotreticulum", ["tests", "node"], identity);
            var local = new LocalDestination(identity, destination);
            server.Router.RegisterLocalDestination(local);
            await Task.Delay(500);
            await server.Router.AnnounceAsync(local);

            var resolved = await client.ResolveIdentityAsync(destination.Hash, TimeSpan.FromSeconds(20));
            Assert.NotNull(resolved);
            Assert.Equal(identity.HexHash, resolved.HexHash);
            Assert.True(client.Router.HasPath(destination.Hash));
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Open_MissingConfig_Throws() =>
        Assert.Throws<FileNotFoundException>(() => ReticulumNode.Open(Path.Combine(TempDir(), "nope")));
}
