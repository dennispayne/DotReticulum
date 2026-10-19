using DotReticulum.Applications.Rncp;
using DotReticulum.Applications.Rnx;
using DotReticulum.Core;

namespace DotReticulum.Applications.Tests;

public class RncpRnxTests
{
    [Fact]
    public void ShellSplit_FollowsPosixQuoting()
    {
        Assert.Equal(["a", "b c", "d e", "f"], CliSupport.ShellSplit("a 'b c' \"d e\" f"));
        Assert.Equal(["x y", "z"], CliSupport.ShellSplit("x\\ y z"));
    }

    [Fact]
    public async Task RncpHelp_PrintsUsage()
    {
        var output = new StringWriter();
        Assert.Equal(0, await RncpApp.RunAsync(["--help"], output, TextWriter.Null, CancellationToken.None));
        Assert.Contains("rncp -l", output.ToString());
        Assert.Equal(0, await RnxApp.RunAsync(["-h"], output, TextWriter.Null, CancellationToken.None));
        Assert.Equal(2, await RncpApp.RunAsync(["--bogus"], output, TextWriter.Null, CancellationToken.None));
    }

    [Fact]
    public void RnxExecute_LimitsAndMissingCommand()
    {
        var ok = RnxListener.Execute("dotnet --version", 30, 3, 0, null);
        Assert.Equal(true, ok[0]);
        Assert.Equal(0L, ok[1]);
        Assert.Equal(3, ((byte[])ok[2]!).Length);
        Assert.Empty((byte[])ok[3]!);
        Assert.True((long)ok[4]! > 3);
        Assert.NotNull(ok[7]);

        var missing = RnxListener.Execute("definitely-not-a-command-xyz", 5, null, null, null);
        Assert.Equal(false, missing[0]);
    }

    [Fact]
    public async Task Rncp_SendFetchAndAuthorisation()
    {
        var (server, client, dir) = ReticulumNodeTests.Pair();
        try
        {
            await server.StartAsync();
            await client.StartAsync();
            var serverIdentity = server.LoadOrCreateIdentity("rncp");
            var clientIdentity = client.LoadOrCreateIdentity("rncp");
            var save = Directory.CreateDirectory(Path.Combine(dir, "save")).FullName;
            var jail = Directory.CreateDirectory(Path.Combine(dir, "jail")).FullName;
            var data = new byte[300_000];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(Path.Combine(jail, "a.bin"), data);
            File.WriteAllText(Path.Combine(dir, "secret.txt"), "secret");

            var listener = new RncpListener(server.Router, serverIdentity, new RncpListenerOptions
            {
                AllowedIdentities = [clientIdentity.Hash],
                AllowFetch = true,
                FetchJail = jail,
                SaveDirectory = save,
            });
            await Task.Delay(500);
            await listener.AnnounceAsync();

            var source = Path.Combine(dir, "up.bin");
            File.WriteAllBytes(source, data);
            var sent = await RncpClient.SendAsync(client, clientIdentity, listener.DestinationHash, source, TimeSpan.FromSeconds(20), true, null, CancellationToken.None);
            Assert.True(sent.Success, sent.Message);
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(save, "up.bin")));

            var fetchDir = Directory.CreateDirectory(Path.Combine(dir, "fetched")).FullName;
            var fetched = await RncpClient.FetchAsync(client, clientIdentity, listener.DestinationHash, "a.bin", fetchDir, TimeSpan.FromSeconds(20), false, null, CancellationToken.None);
            Assert.True(fetched.Success, fetched.Message);
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(fetchDir, "a.bin")));

            var escaped = await RncpClient.FetchAsync(client, clientIdentity, listener.DestinationHash, "../secret.txt", fetchDir, TimeSpan.FromSeconds(20), false, null, CancellationToken.None);
            Assert.False(escaped.Success);
            Assert.False(File.Exists(Path.Combine(fetchDir, "secret.txt")));

            using var stranger = Identity.Generate();
            var rejected = await RncpClient.SendAsync(client, stranger, listener.DestinationHash, source, TimeSpan.FromSeconds(10), true, null, CancellationToken.None);
            Assert.False(rejected.Success);
            Assert.Single(Directory.GetFiles(save));
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Rnx_ExecutesForAllowedIdentityOnly()
    {
        var (server, client, dir) = ReticulumNodeTests.Pair();
        try
        {
            await server.StartAsync();
            await client.StartAsync();
            var serverIdentity = server.LoadOrCreateIdentity("rnx");
            var clientIdentity = client.LoadOrCreateIdentity("rnx");
            var listener = new RnxListener(server.Router, serverIdentity, new RnxListenerOptions { AllowedIdentities = [clientIdentity.Hash] });
            await Task.Delay(500);
            await listener.AnnounceAsync();

            var good = new RnxClient(client, clientIdentity, listener.DestinationHash);
            var result = await good.ExecuteAsync("dotnet --version", TimeSpan.FromSeconds(20));
            Assert.True(result.Ok, result.Error);
            Assert.Equal(0L, result.ReturnCode);
            Assert.Matches(@"^\d+\.\d+", System.Text.Encoding.UTF8.GetString(result.Stdout));
            await good.CloseAsync();

            using var stranger = Identity.Generate();
            var bad = new RnxClient(client, stranger, listener.DestinationHash);
            var denied = await bad.ExecuteAsync("dotnet --version", TimeSpan.FromSeconds(5));
            Assert.False(denied.Ok);
            await bad.CloseAsync();
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
