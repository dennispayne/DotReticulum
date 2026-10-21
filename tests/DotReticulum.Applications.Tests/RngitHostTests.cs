using DotReticulum.Applications.Rngit;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Transport;
using DotReticulum.Tests.Interop;

namespace DotReticulum.Applications.Tests;

public sealed class RngitHostTests
{
    [Fact]
    public async Task BoundedWorkersRejectOverflowAndShutdownSettlesBlockedAndQueuedRequests()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (server, client, networkDirectory) = ReticulumNodeTests.Pair(out _);
        try
        {
            await using var serverScope = server;
            await using var clientScope = client;
            await using var host = new RngitHost(server, RngitHostConfiguration.Load(fixture.Config), queueCapacity: 1, concurrency: 1);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var caller = Identity.Generate();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Store.TransferRepositoryAsync = async (_, _, _, stop) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, stop);
            };
            await server.StartAsync(deadline.Token);
            await client.StartAsync(deadline.Token);
            await host.AnnounceAsync(deadline.Token);
            using var management = await RngitManagementClient.ConnectAsync(client, caller, host.DestinationHash,
                TimeSpan.FromSeconds(10), deadline.Token);
            var blocked = management.RequestAsync("/git/fork", new Dictionary<object, object?>
            {
                [0] = "fixtures/blocked.git", ["source"] = "rns://" + new string('1', 32) + "/fixtures/source.git",
            }, deadline.Token);
            await started.Task.WaitAsync(deadline.Token);
            var queued = management.RequestAsync("/git/list", new Dictionary<object, object?> { [0] = "fixtures/sample.git" }, deadline.Token);
            while (host.PendingRequests != 1) await Task.Delay(10, deadline.Token);
            var rejected = await management.RequestAsync("/git/list", new Dictionary<object, object?> { [0] = "fixtures/sample.git" }, deadline.Token);
            Assert.Equal(255, rejected.Data[0]);
            Assert.Contains("Server busy", System.Text.Encoding.UTF8.GetString(rejected.Data));
            Assert.Equal(1, host.PendingRequests);
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IOException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<IOException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(host.Store.Find("fixtures/blocked.git"));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "repositories", "blocked.git")));
        }
        finally { Directory.Delete(networkDirectory, true); }
    }

    [LiveFact]
    public async Task NativeManagementAndRepositoryLifecycleUseActualPinnedServer()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var caller = Identity.Generate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var port = ReticulumNodeTests.FreePort();
        await fixture.Git("-c", "user.name=Owned release", "-c", "user.email=fixture@example.invalid",
            "tag", "-a", "v1", "-m", "release", fixture.First);
        await using var python = await PyLinkNode.StartAsync("native-management", tcpServerPort: port,
            harness: "live_rngit_node.py", extraArguments:
            ["--repositories", Path.GetDirectoryName(fixture.Repository)!, "--writer", caller.HexHash, "--manage"]);
        var config = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
              enable_transport = no
            [interfaces]
              [[Pinned repository server]]
                type = TCPClientInterface
                enabled = yes
                target_host = 127.0.0.1
                target_port = {port}
            """);
        await using var node = ReticulumNode.Create(config, Path.Combine(fixture.Root, "client-network"));
        await node.StartAsync(deadline.Token);
        using var client = await RngitManagementClient.ConnectAsync(node, caller, python.Hash,
            TimeSpan.FromSeconds(45), deadline.Token);
        async Task<object?> Request(string path, Dictionary<object, object?> data) =>
            RngitManagementClient.Decode((await client.RequestAsync(path, data, deadline.Token)).Data);
        var content = "Native author against the actual pinned server";
        var created = await Request("/mgmt/work", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "create", ["title"] = "Native proposal",
            ["content"] = content, ["signature"] = caller.Sign(System.Text.Encoding.UTF8.GetBytes(content)),
            ["format"] = "markdown",
        });
        var id = RngitWire.Get(created, "id");
        var viewed = await Request("/mgmt/work", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "view", ["scope"] = "active", ["doc_id"] = id,
        });
        Assert.Equal(content, RngitWire.Text(viewed, "content"));
        foreach (var operation in new[] { "complete", "activate" })
            await Request("/mgmt/work", new() { [0] = "fixtures/sample.git", ["operation"] = operation, ["doc_id"] = id });
        var artifacts = Directory.CreateDirectory(Path.Combine(fixture.Root, "artifacts")).FullName;
        await File.WriteAllTextAsync(Path.Combine(artifacts, "owned.txt"), "Independently transported release artifact", deadline.Token);
        var remote = $"rns://{python.HashHex}/fixtures/sample.git";
        var released = await RngitReleaseClient.CreateAsync(client, caller, caller, remote, "v1:" + artifacts, null, false,
            deadline.Token, "Native release notes");
        Assert.Equal("v1", released.Tag);
        var downloaded = Directory.CreateDirectory(Path.Combine(fixture.Root, "downloaded")).FullName;
        var validated = await RngitReleaseClient.FetchAsync(client, remote, "v1:all", downloaded, caller.Hash, deadline.Token);
        Assert.True(validated.IsValid);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(artifacts, "owned.txt")),
            await File.ReadAllBytesAsync(Path.Combine(downloaded, "owned.txt")));
        var permissions = await Request("/mgmt/perms", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "rperms", ["step"] = "get",
        });
        Assert.NotNull(RngitWire.Text(permissions, "content"));

        var targetGroups = Directory.CreateDirectory(Path.Combine(fixture.Root, "native-repositories")).FullName;
        var hostConfig = Directory.CreateDirectory(Path.Combine(fixture.Root, "native-config")).FullName;
        await File.WriteAllTextAsync(Path.Combine(hostConfig, "config"), $"""
            [rngit]
            mirror_interval = 0
            [repositories]
            native = {targetGroups}
            [access]
            native = read:all, write:all, create:all, admin:all
            """);
        await using var host = new RngitHost(node, RngitHostConfiguration.Load(hostConfig));
        foreach (var kind in new[] { "fork", "mirror" })
        {
            Assert.Equal(new byte[] { 0 }, await host.HandleAsync("/git/" + kind,
                new Dictionary<object, object?> { [0] = "native/" + kind + ".git", ["source"] = remote }, caller, deadline.Token));
            var path = Path.Combine(targetGroups, kind + ".git");
            Assert.Equal(fixture.First, (await RngitClient.GitAsync(path, ["rev-parse", "HEAD"], deadline.Token)).Trim());
            Assert.Equal(new byte[] { 0 }, await host.HandleAsync("/git/sync",
                new Dictionary<object, object?> { [0] = "native/" + kind + ".git" }, caller, deadline.Token));
        }
    }

    [LiveFact]
    public async Task ActualPinnedClientUsesNativeListFetchCreateAndPermissions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var port = ReticulumNodeTests.FreePort();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var config = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
              enable_transport = no
            [interfaces]
              [[Reference client]]
                type = TCPServerInterface
                enabled = yes
                listen_ip = 127.0.0.1
                listen_port = {port}
            """);
        await using var node = ReticulumNode.Create(config, Path.Combine(fixture.Root, "server"));
        await using var host = new RngitHost(node, RngitHostConfiguration.Load(fixture.Config));
        await node.StartAsync(deadline.Token);
        await host.AnnounceAsync(deadline.Token);
        await using var python = await PyLinkNode.StartAsync("native-git-host", rnsdPort: port,
            harness: "live_rngit_client.py", extraArguments: ["--target", $"rns://{host.DestinationHashHex}/fixtures/sample.git"]);
        async Task<(byte[] Data, object? Metadata)> Request(string path, Dictionary<object, object?> request, bool signContent = false)
        {
            var command = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["path"] = path, ["request"] = Convert.ToBase64String(MsgPack.Pack(request)),
                ["sign_content"] = signContent,
            });
            var response = await python.Command(command, "response", TimeSpan.FromSeconds(40));
            return (Assert.IsType<byte[]>(MsgPack.Unpack(Convert.FromBase64String(response.GetProperty("data").GetString()!))),
                MsgPack.Unpack(Convert.FromBase64String(response.GetProperty("metadata").GetString()!)));
        }
        var request = new Dictionary<object, object?> { [0] = "fixtures/sample.git" };
        var listing = await Request("/git/list", request);
        Assert.Equal(0, listing.Data[0]);
        Assert.Contains(fixture.First, System.Text.Encoding.UTF8.GetString(listing.Data));
        request["refs"] = new object?[] { new Dictionary<object, object?> { ["ref"] = "refs/heads/main" } };
        var fetched = await Request("/git/fetch", request);
        Assert.True(fetched.Data.Length > 0);
        var metadata = Assert.IsType<Dictionary<object, object?>>(fetched.Metadata);
        Assert.Equal(0L, metadata[1L]);
        request[0] = "fixtures/created.git";
        var created = await Request("/git/create", request);
        Assert.Equal(new byte[] { 0 }, created.Data);
        Assert.NotNull(host.Store.Find("fixtures/created.git"));
        request["operation"] = "rperms";
        request["step"] = "get";
        var permissions = await Request("/mgmt/perms", request);
        Assert.Equal(0, permissions.Data[0]);
        Assert.Contains(python.HashHex, RngitWire.Text(RngitManagementClient.Decode(permissions.Data), "content"));
        var work = await Request("/mgmt/work", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "create", ["title"] = "Reference author",
            ["content"] = "Signed by the actual upstream client", ["format"] = "markdown",
        }, signContent: true);
        var docId = RngitWire.Get(RngitManagementClient.Decode(work.Data), "id");
        Assert.IsType<long>(docId);
        var document = await Request("/mgmt/work", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "view", ["scope"] = "active", ["doc_id"] = docId,
        });
        var doc = RngitManagementClient.Decode(document.Data);
        Assert.Equal("Signed by the actual upstream client", RngitWire.Text(doc, "content"));
        var meta = RngitWire.Get(doc, "meta");
        Assert.Equal(python.HashHex, RngitWire.Text(meta, "author"));
        foreach (var operation in new[] { "complete", "activate" })
            Assert.Equal(0, (await Request("/mgmt/work", new()
            {
                [0] = "fixtures/sample.git", ["operation"] = operation, ["doc_id"] = docId,
            })).Data[0]);
        await fixture.Git("-c", "user.name=Owned release", "-c", "user.email=fixture@example.invalid",
            "tag", "-a", "v1", "-m", "release", fixture.First);
        Assert.Equal(0, (await Request("/mgmt/release", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "create", ["step"] = "init", ["tag"] = "v1",
            ["hash"] = fixture.First, ["notes"] = "Reference-managed release",
        })).Data[0]);
        var artifact = System.Text.Encoding.UTF8.GetBytes("Reference client artifact");
        Assert.Equal(0, (await Request("/mgmt/release", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "create", ["step"] = "artifact", ["tag"] = "v1",
            ["artifact_name"] = "owned.txt", ["artifact_data"] = artifact,
        })).Data[0]);
        Assert.Equal(0, (await Request("/mgmt/release", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "create", ["step"] = "finalize", ["tag"] = "v1",
        })).Data[0]);
        var release = await Request("/mgmt/release", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "view", ["tag"] = "v1",
        });
        Assert.Equal("v1", RngitWire.Text(RngitManagementClient.Decode(release.Data), "tag"));
        Assert.Equal(artifact, (await Request("/mgmt/release", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "fetch", ["tag"] = "v1", ["artifact"] = "owned.txt",
        })).Data);
        request[0] = "missing/secret";
        var hidden = await Request("/git/list", request);
        Assert.Equal(RngitWire.Status(3, "Not found"), hidden.Data);
    }

    [Fact]
    public async Task HostingPreservesHelperContractAndRepositoryStateAcrossRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var caller = Identity.Generate();
        string destination;
        await using (var node = fixture.Node())
        await using (var host = new RngitHost(node, RngitHostConfiguration.Load(fixture.Config)))
        {
            destination = host.DestinationHashHex;
            var request = new Dictionary<object, object?> { [0] = "fixtures/sample.git" };
            var listing = Assert.IsType<byte[]>(await host.HandleAsync("/git/list", request, caller));
            Assert.Equal(0, listing[0]);
            Assert.Contains("@" + "refs/heads/main HEAD", System.Text.Encoding.UTF8.GetString(listing));
            Assert.Contains(fixture.First, System.Text.Encoding.UTF8.GetString(listing));
            request["operations"] = new object?[]
            {
                new Dictionary<object, object?> { ["action"] = "update_ref", ["ref"] = "refs/heads/caf\u00e9", ["sha"] = fixture.First },
            };
            Assert.Equal(new byte[] { 0 }, Assert.IsType<byte[]>(await host.HandleAsync("/git/push", request, caller)));
            Assert.Equal(fixture.First, (await fixture.Git("rev-parse", "refs/heads/caf\u00e9")).Trim());
            request["ref"] = "refs/heads/caf\u00e9";
            Assert.Equal(new byte[] { 0 }, Assert.IsType<byte[]>(await host.HandleAsync("/git/delete", request, caller)));
            Assert.Equal("refs/heads/main", (await fixture.Git("symbolic-ref", "HEAD")).Trim());
        }
        await using (var node = fixture.Node())
        await using (var host = new RngitHost(node, RngitHostConfiguration.Load(fixture.Config)))
        {
            Assert.Equal(destination, host.DestinationHashHex);
            Assert.Equal(fixture.First, (await fixture.Git("rev-parse", "HEAD")).Trim());
            Assert.Single(host.Store.Repositories);
        }
    }

    [Fact]
    public async Task HostingAuthorizationAndMalformedRequestsDoNotDiscloseOrMutate()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var caller = Identity.Generate();
        await File.AppendAllTextAsync(Path.Combine(fixture.Config, "config"), "\n");
        await using var node = fixture.Node();
        await using var host = new RngitHost(node, RngitHostConfiguration.Load(fixture.Config));
        var request = new Dictionary<object, object?> { [0] = "fixtures/sample.git" };
        Assert.Equal(RngitWire.Status(1, "Not identified"),
            await host.HandleAsync("/git/list", request, null));
        Assert.Equal(RngitWire.Status(2, "Invalid request"),
            await host.HandleAsync("/git/list", "invalid", caller));
        request[0] = "unknown/private.git";
        var denied = Assert.IsType<byte[]>(await host.HandleAsync("/git/list", request, caller));
        Assert.Equal(3, denied[0]);
        Assert.Equal("Not found", System.Text.Encoding.UTF8.GetString(denied.AsSpan(1)));
        request[0] = "fixtures/sample.git";
        request["operations"] = new object?[]
        {
            new Dictionary<object, object?> { ["action"] = "update_ref", ["ref"] = "refs/heads/../escape", ["sha"] = fixture.First },
        };
        Assert.Equal(2, Assert.IsType<byte[]>(await host.HandleAsync("/git/push", request, caller))[0]);
        Assert.Equal(fixture.First, (await fixture.Git("rev-parse", "HEAD")).Trim());
    }

    [Fact]
    public async Task FetchBundleContainsActualObjectsAndNoChangeIsStatusOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var caller = Identity.Generate();
        await using var node = fixture.Node();
        await using var host = new RngitHost(node, RngitHostConfiguration.Load(fixture.Config));
        var request = new Dictionary<object, object?>
        {
            [0] = "fixtures/sample.git",
            ["refs"] = new object?[] { new Dictionary<object, object?> { ["ref"] = "refs/heads/main", ["sha"] = fixture.First } },
        };
        var response = Assert.IsType<FileResponse>(await host.HandleAsync("/git/fetch", request, caller));
        Assert.True(response.Data.Length > 0);
        var bundle = Path.Combine(fixture.Root, "fetch.bundle");
        await File.WriteAllBytesAsync(bundle, response.Data);
        Assert.Contains(fixture.First, await fixture.Git("bundle", "list-heads", bundle));
        request["have"] = new object?[] { fixture.First };
        Assert.Equal(new byte[] { 0 }, await host.HandleAsync("/git/fetch", request, caller));
        request["have"] = new object?[] { "-injection" };
        Assert.Equal(2, Assert.IsType<byte[]>(await host.HandleAsync("/git/fetch", request, caller))[0]);
    }

    [Fact]
    public async Task NativeHostLoopbackManagementAndBareCloneUseOwnedWorkers()
    {
        await using var fixture = await Fixture.CreateAsync();
        var port = ReticulumNodeTests.FreePort();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var serverConfig = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
              enable_transport = no
            [interfaces]
              [[Loopback]]
                type = TCPServerInterface
                enabled = yes
                listen_ip = 127.0.0.1
                listen_port = {port}
            """);
        var clientConfig = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
              enable_transport = no
            [interfaces]
              [[Loopback]]
                type = TCPClientInterface
                enabled = yes
                target_host = 127.0.0.1
                target_port = {port}
            """);
        await using var server = ReticulumNode.Create(serverConfig, Path.Combine(fixture.Root, "server-rns"));
        await using var host = new RngitHost(server, RngitHostConfiguration.Load(fixture.Config));
        await server.StartAsync(deadline.Token);
        await using var client = ReticulumNode.Create(clientConfig, Path.Combine(fixture.Root, "client-rns"));
        await client.StartAsync(deadline.Token);
        await host.AnnounceAsync(deadline.Token);
        using var caller = Identity.Generate();
        using var management = await RngitManagementClient.ConnectAsync(client, caller, host.DestinationHash,
            TimeSpan.FromSeconds(30), deadline.Token);
        var listing = await management.RequestAsync("/git/list", new Dictionary<object, object?> { [0] = "fixtures/sample.git" }, deadline.Token);
        Assert.Equal(0, listing.Data[0]);
        var clone = Path.Combine(fixture.Root, "cloned.git");
        var result = await new RngitClient(client, caller).CloneAsync($"rns://{host.DestinationHashHex}/fixtures/sample.git",
            clone, TimeSpan.FromSeconds(30), cancellationToken: deadline.Token);
        Assert.True(result.NetworkBundleBytes > 0);
        Assert.Equal(fixture.First, result.Head);
        Assert.Equal("refs/heads/main", (await RngitClient.GitAsync(clone, ["symbolic-ref", "HEAD"], deadline.Token)).Trim());
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "rngit-host-" + Guid.NewGuid().ToString("N"));
        internal string Config => Path.Combine(Root, "host");
        internal string Repository => Path.Combine(Root, "repositories", "sample.git");
        internal string First { get; private set; } = "";
        internal Task<string> Git(params string[] args) => RngitClient.GitAsync(Repository, args, CancellationToken.None, isolated: true);
        internal ReticulumNode Node() => ReticulumNode.Create(RnsdConfigParser.Parse("[reticulum]\nenable_transport = no\n"), Path.Combine(Root, "node"));

        internal static async Task<Fixture> CreateAsync()
        {
            var result = new Fixture();
            Directory.CreateDirectory(result.Config);
            Directory.CreateDirectory(result.Repository);
            await result.Git("init", "--bare", "--initial-branch=main", ".");
            var tree = (await RngitClient.GitAsync(result.Repository, ["mktree"], CancellationToken.None, "", isolated: true)).Trim();
            result.First = (await result.Git("-c", "user.name=Disposable host test", "-c", "user.email=fixture@example.invalid",
                "commit-tree", tree, "-m", "owned fixture")).Trim();
            await result.Git("update-ref", "refs/heads/main", result.First);
            await File.WriteAllTextAsync(Path.Combine(result.Config, "config"), $"""
                [rngit]
                announce_interval = 0
                mirror_interval = 0
                record_stats = yes
                [pages]
                serve_nomadnet = no
                [repositories]
                fixtures = {Path.GetDirectoryName(result.Repository)}
                [access]
                fixtures = read:all, write:all, create:all, release:all, interact:all, propose:all, admin:all
                """);
            return result;
        }

        public ValueTask DisposeAsync()
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
