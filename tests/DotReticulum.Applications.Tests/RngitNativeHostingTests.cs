using System.Text;
using System.Text.RegularExpressions;
using DotReticulum.Applications.Rngit;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;
using DotReticulum.Tests.Interop;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Integration")]
public sealed class RngitNativeHostingTests
{
    public sealed class NativeReferenceFactAttribute : FactAttribute
    {
        public NativeReferenceFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_NATIVE_EXE")) ||
                Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1")
                Skip = "Requires DOTRETICULUM_NATIVE_EXE and DOTRETICULUM_LIVE=1 with the verified Python reference.";
        }
    }

    [Trait("Category", "Live")]
    [NativeReferenceFact]
    public async Task PublishedHostServesRepositoryCollaborationTemplatesMediaAndDownloadsAcrossNativeLinks()
    {
        await using var fixture = await RngitHostTests.Fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var caller = Identity.Generate();
        using var blocked = Identity.Generate();
        var backend = Environment.GetEnvironmentVariable("DOTRETICULUM_MEDIA_BACKEND");
        var convertMedia = backend is { Length: > 0 };
        var configPath = Path.Combine(fixture.Config, "config");
        var hostConfiguration = (await File.ReadAllTextAsync(configPath)).Replace("serve_nomadnet = no",
            $"serve_nomadnet = yes\nmedia_conversion = {(convertMedia ? "yes" : "no")}", StringComparison.Ordinal)
            .Replace("admin:all", "admin:all, stats:all", StringComparison.Ordinal)
            .Replace("record_stats = yes", $"record_stats = yes\nnode_name = Native acceptance node\nblocked_identities = {blocked.HexHash}", StringComparison.Ordinal);
        await File.WriteAllTextAsync(configPath, hostConfiguration);
        var templates = Directory.CreateDirectory(Path.Combine(fixture.Config, "templates")).FullName;
        await File.WriteAllTextAsync(Path.Combine(templates, "base.mu"), "Native wrapper {NODE_NAME}\n{GEN_TIME}\n{NAVIGATION}\n{PAGE_CONTENT}");
        var readme = "# Native acceptance\n\nOwned resource download.\n";
        var readmeHash = (await RngitClient.GitAsync(fixture.Repository, ["hash-object", "-w", "--stdin"],
            deadline.Token, readme, isolated: true)).Trim();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DotReticulum.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var png = await File.ReadAllBytesAsync(Path.Combine(root.FullName, "assets", "branding", "dotreticulum.png"), deadline.Token);
        var imageHash = Encoding.UTF8.GetString(await RngitClient.GitBytesAsync(fixture.Repository,
            ["hash-object", "-w", "--stdin"], deadline.Token, png, isolated: true)).Trim();
        var tree = (await RngitClient.GitAsync(fixture.Repository, ["mktree"], deadline.Token,
            $"100644 blob {readmeHash}\tREADME.md\n100644 blob {imageHash}\timage.png\n", isolated: true)).Trim();
        var commit = (await fixture.Git("-c", "user.name=Native acceptance", "-c", "user.email=native@example.invalid",
            "commit-tree", tree, "-p", fixture.First, "-m", "native pages")).Trim();
        await fixture.Git("update-ref", "refs/heads/main", commit);
        await fixture.Git("tag", "v1", commit);

        var port = ReticulumNodeTests.FreePort();
        var serverNetwork = Directory.CreateDirectory(Path.Combine(fixture.Root, "server-network")).FullName;
        await File.WriteAllTextAsync(Path.Combine(serverNetwork, "config"), $"""
            [reticulum]
              share_instance = no
            enable_transport = no
            [interfaces]
            [[Owned native server]]
            type = TCPServerInterface
            enabled = yes
            listen_ip = 127.0.0.1
            listen_port = {port}
            """);
        var native = Environment.GetEnvironmentVariable("DOTRETICULUM_NATIVE_EXE")!;
        var printed = await RngitProcess.RunAsync(native, ["rngit", "node", "--config", fixture.Config, "-p"], stop: deadline.Token);
        Assert.True(printed.Code == 0, printed.Error);
        var info = Encoding.UTF8.GetString(printed.Output);
        byte[] Destination(string label)
        {
            var match = Regex.Match(info, label + @"\s*:\s*<([0-9a-f]{32})>");
            Assert.True(match.Success, info);
            return Convert.FromHexString(match.Groups[1].Value);
        }
        var environment = new Dictionary<string, string>();
        if (backend is { Length: > 0 })
        {
            environment["PATH"] = Path.GetDirectoryName(backend) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            environment["RNGIT_MEDIA_BACKEND"] = "ffmpeg";
        }
        using var host = new NativeResourceTests.NativePeer(environment,
            "rngit", "node", "--config", fixture.Config, "--rnsconfig", serverNetwork);
        while (!host.Output.Contains("listening on", StringComparison.Ordinal))
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, deadline.Token);
        }
        await using var node = ReticulumNode.Create(RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
            enable_transport = no
            [interfaces]
            [[Owned native host]]
            type = TCPClientInterface
            enabled = yes
            target_host = 127.0.0.1
            target_port = {port}
            """), Path.Combine(fixture.Root, "managed-client"));
        await node.StartAsync(deadline.Token);
        using var management = await RngitManagementClient.ConnectAsync(node, caller, Destination("Repositories Destination"),
            TimeSpan.FromSeconds(30), deadline.Token);
        var content = "Signed native-host work document";
        var created = RngitManagementClient.Decode((await management.RequestAsync("/mgmt/work", new Dictionary<object, object?>
        {
            [0] = "fixtures/sample.git", ["operation"] = "create", ["title"] = "Native document",
            ["content"] = content, ["format"] = "markdown", ["signature"] = caller.Sign(Encoding.UTF8.GetBytes(content)),
        }, deadline.Token)).Data);
        var id = RngitWire.Get(created, "id");
        Assert.IsType<long>(id);
        var artifactDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "artifacts")).FullName;
        var payload = Encoding.UTF8.GetBytes("Native-host artifact");
        await File.WriteAllBytesAsync(Path.Combine(artifactDirectory, "owned.txt"), payload, deadline.Token);
        await RngitReleaseClient.CreateAsync(management, caller, caller,
            $"rns://{Convert.ToHexStringLower(Destination("Repositories Destination"))}/fixtures/sample.git",
            "v1:" + artifactDirectory, "native", false, deadline.Token, "Native release notes");
        using var pages = await RngitManagementClient.ConnectAsync(node, caller, Destination("Nomad Network Destination"),
            TimeSpan.FromSeconds(30), deadline.Token);
        var data = new Dictionary<object, object?>
        {
            ["var_g"] = "fixtures", ["var_r"] = "sample.git", ["var_ref"] = "HEAD",
            ["var_path"] = "README.md", ["var_h"] = commit, ["var_t"] = "v1",
            ["var_scope"] = "active", ["var_id"] = Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture), ["var_a"] = "owned.txt",
        };
        foreach (var (path, expected) in new[]
        {
            ("/page/index.mu", "Native acceptance node"), ("/page/group.mu", "sample.git"),
            ("/page/repo.mu", "Native acceptance"), ("/page/tree.mu", "README.md"),
            ("/page/blob.mu", "Owned resource download"), ("/page/refs.mu", "main"),
            ("/page/commits.mu", "native pages"), ("/page/commit.mu", "native pages"),
            ("/page/stats.mu", "Stats for"), ("/page/releases.mu", "v1"),
            ("/page/release.mu", "Native release notes"), ("/page/work.mu", "Native document"),
            ("/page/work_doc.mu", content),
        })
        {
            var response = (await pages.RequestAsync(path, data, deadline.Token)).Data;
            var text = Encoding.UTF8.GetString(response);
            Assert.Contains("Native wrapper Native acceptance node", text);
            Assert.Contains("Generated in ", text);
            Assert.DoesNotContain("Unknown generation time", text);
            Assert.True(text.Contains(expected, StringComparison.OrdinalIgnoreCase), $"{path}: {text}\nNative host: {host.Output}");
            Assert.DoesNotContain(">>Error", text);
        }
        Assert.Equal(Encoding.UTF8.GetBytes(readme), (await pages.RequestAsync("/file/download", data, deadline.Token)).Data);
        Assert.Equal(payload, (await pages.RequestAsync("/file/artifact", data, deadline.Token)).Data);
        Assert.Equal(Encoding.UTF8.GetBytes(content), (await pages.RequestAsync("/file/workdoc", data, deadline.Token)).Data);
        var media = (await pages.RequestAsync("/media", new Dictionary<object, object?>
        {
            ["key"] = "owned", ["path"] = "/media/fixtures/sample.git/HEAD/image.png",
        }, deadline.Token)).Data;
        Assert.NotEmpty(media);
        if (convertMedia) Assert.NotNull(RngitMedia.WebpInfo(media));
        else Assert.Equal(png, media);
        await using var reference = await PyLinkNode.StartAsync("published-host-reference", rnsdPort: port,
            harness: "live_rngit_client.py", extraArguments:
            ["--target", $"rns://{Convert.ToHexStringLower(Destination("Repositories Destination"))}/fixtures/sample.git"]);
        async Task<byte[]> ReferenceRequest(string path, Dictionary<object, object?> request, bool sign = false)
        {
            var command = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["path"] = path, ["request"] = Convert.ToBase64String(MsgPack.Pack(request)), ["sign_content"] = sign,
            });
            var response = await reference.Command(command, "response", TimeSpan.FromSeconds(30));
            return Assert.IsType<byte[]>(MsgPack.Unpack(Convert.FromBase64String(response.GetProperty("data").GetString()!)));
        }
        Assert.Equal(0, (await ReferenceRequest("/git/list", new() { [0] = "fixtures/sample.git" }))[0]);
        var referenceBundle = await ReferenceRequest("/git/fetch", new()
        {
            [0] = "fixtures/sample.git", ["refs"] = new object?[]
            {
                new Dictionary<object, object?> { ["ref"] = "refs/heads/main" },
            },
        });
        Assert.Equal((byte)'#', referenceBundle[0]);
        var referenceDocument = RngitManagementClient.Decode(await ReferenceRequest("/mgmt/work", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "create", ["title"] = "Pinned author on native host",
            ["content"] = "Actual upstream-signed native-host document", ["format"] = "markdown",
        }, sign: true));
        Assert.IsType<long>(RngitWire.Get(referenceDocument, "id"));
        Assert.Equal(payload, await ReferenceRequest("/mgmt/release", new()
        {
            [0] = "fixtures/sample.git", ["operation"] = "fetch", ["tag"] = "v1", ["artifact"] = "owned.txt",
        }));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(async number =>
        {
            var body = $"Concurrent native document {number}";
            var response = RngitManagementClient.Decode((await management.RequestAsync("/mgmt/work", new Dictionary<object, object?>
            {
                [0] = "fixtures/sample.git", ["operation"] = "create", ["title"] = body, ["content"] = body,
                ["signature"] = caller.Sign(Encoding.UTF8.GetBytes(body)), ["format"] = "markdown",
            }, deadline.Token)).Data);
            return Assert.IsType<long>(RngitWire.Get(response, "id"));
        }));
        Assert.Equal(4, concurrent.Distinct().Count());
        using var blockedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (byte[] Data, object? Metadata)? blockedResponse = null;
        var rejected = await Record.ExceptionAsync(async () =>
        {
            using var connection = await RngitManagementClient.ConnectAsync(node, blocked, Destination("Repositories Destination"),
                TimeSpan.FromSeconds(3), blockedDeadline.Token);
            blockedResponse = await connection.RequestAsync("/git/list", new Dictionary<object, object?> { [0] = "fixtures/sample.git" }, blockedDeadline.Token);
        });
        if (rejected is null)
        {
            // The request can be denied before the asynchronous disconnect observer runs.
            Assert.NotNull(blockedResponse);
            Assert.Equal(RngitWire.Status(3, "Not found"), blockedResponse.Value.Data);
            Assert.Null(blockedResponse.Value.Metadata);
        }
        else
            Assert.True(rejected is IOException or InvalidOperationException, $"Unexpected blocked-peer outcome: {rejected}\n{host.Output}");
        while (!host.Output.Contains("Blocked Git peer " + blocked.HexHash, StringComparison.Ordinal))
            await Task.Delay(20, deadline.Token);
        Assert.Contains("Blocked Git peer " + blocked.HexHash, host.Output);
        Assert.Contains("Native wrapper", Encoding.UTF8.GetString((await pages.RequestAsync("/page/index.mu", data, deadline.Token)).Data));
        Assert.DoesNotContain("Git peer request", host.Output);
    }
}
