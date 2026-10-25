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
              share_instance = no
              enable_transport = no
            [interfaces]
              [[S]]
                type = TCPServerInterface
                enabled = yes
                listen_ip = 127.0.0.1
                listen_port = {port}
            """), Path.Combine(dir, "server"));
        var client = ReticulumNode.Create(RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
            [interfaces]
              [[C]]
                type = TCPClientInterface
                enabled = yes
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
    public async Task Open_MissingConfig_CreatesDefaultConfiguration()
    {
        var directory = TempDir();
        try
        {
            await using var node = ReticulumNode.Open(directory);
            Assert.True(File.Exists(Path.Combine(directory, "config")));
            Assert.True(node.Config.SharedInstance.Enabled);
            Assert.False(node.Config.EnableTransport);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task IdentityPathsPreserveToolSubdirectoriesWithoutAllowingTraversal()
    {
        var directory = TempDir();
        try
        {
            await using var node = ReticulumNode.Create(RnsdConfigParser.Parse("[reticulum]\nshare_instance=no"), directory);
            using var first = node.LoadOrCreateIdentity(Path.Combine("identities", "rncp"));
            using var second = node.LoadOrCreateIdentity(Path.Combine("identities", "rncp"));
            Assert.Equal(first.Hash, second.Hash);
            Assert.True(File.Exists(Path.Combine(directory, "storage", "identities", "rncp")));
            foreach (var invalid in new[] { "..", "../escape", "identities/../escape", "identities\\..\\escape", "/absolute", "C:\\absolute", "a//b" })
                Assert.Throws<ArgumentException>(() => node.LoadOrCreateIdentity(invalid));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CachedIdentityWithoutRouteDoesNotStartLinkEstablishment()
    {
        var directory = TempDir();
        try
        {
            await using var node = ReticulumNode.Create(RnsdConfigParser.Parse("[reticulum]\nshare_instance=no"), directory);
            using var identity = Identity.Generate();
            var destination = Destination.Create(DestinationType.Single, "cached-peer", ["echo"], identity);
            node.Router.Engine.Known.Remember(destination.Hash, new byte[32], identity.ExportPublicKey(), null);
            await node.StartAsync();

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result = await ClientLinks.ConnectAsync(node, destination.Hash, TimeSpan.FromMilliseconds(150), deadline.Token);

            Assert.False(result.Ok);
            Assert.Equal("Path not found", result.Error);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RestartRestoresValidatedPathOnStableConfiguredBearerWithoutNewAnnounce()
    {
        var (server, client, directory) = Pair();
        try
        {
            using var identity = Identity.Generate();
            var local = new LocalDestination(identity, Destination.Create(DestinationType.Single, "restart-tests", ["echo"], identity))
            { AcceptLinks = true };
            local.RegisterRequestHandler("/echo", request => request.Data, RequestPolicy.All);
            server.Router.RegisterLocalDestination(local);
            await server.StartAsync();
            await client.StartAsync();
            await server.Router.AnnounceAsync(local);
            Assert.True(await client.WaitForPathAsync(local.Destination.Hash, TimeSpan.FromSeconds(10)));
            Assert.True(await client.RetainDestinationAsync(local.Destination.Hash));
            await client.PersistAsync();
            await client.DisposeAsync();
            await using var restarted = ReticulumNode.Create(client.Config, client.ConfigDir);
            await restarted.StartAsync();
            Assert.True(restarted.Router.HasPath(local.Destination.Hash));
            using var recalled = restarted.Router.Engine.Known.Recall(local.Destination.Hash);
            Assert.NotNull(recalled);
            Assert.Equal(identity.Hash, recalled.Hash);
            var connected = await ClientLinks.ConnectAsync(restarted, local.Destination.Hash, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.True(connected.Ok, connected.Error);
            var request = connected.Link!.Request("/echo", new byte[] { 7, 8, 9 });
            Assert.NotNull(request);
            Assert.Equal(new byte[] { 7, 8, 9 }, Assert.IsType<byte[]>(await request.ResponseTask.WaitAsync(TimeSpan.FromSeconds(10))));
            connected.Link.Teardown();
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
