using System.Diagnostics;
using System.Text.RegularExpressions;
using DotReticulum.Applications.Rngit;
using DotReticulum.Tests.Interop;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Integration")]
public sealed class RngitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedObjectVerificationPreservesGitOwnedPlaceholderHeadAndRejectsCorruption(bool corrupt)
    {
        var root = Directory.CreateTempSubdirectory("rngit object verification ").FullName;
        var directory = Path.Combine(root, new string('p', 96));
        try
        {
            Directory.CreateDirectory(directory);
            await RngitClient.GitAsync(directory, ["init", "--bare", "--initial-branch=main", "."],
                CancellationToken.None, isolated: true);
            var sha = (await RngitClient.GitAsync(directory, ["hash-object", "-w", "--stdin"],
                CancellationToken.None, input: "owned object", isolated: true)).Trim();
            const string head = "ref: refs/heads/.invalid\n";
            await File.WriteAllTextAsync(Path.Combine(directory, "HEAD"), head);
            if (corrupt)
            {
                var path = Path.Combine(directory, "objects", sha[..2], sha[2..]);
                File.SetAttributes(path, FileAttributes.Normal);
                await File.WriteAllBytesAsync(path, [0, 1, 2]);
                await Assert.ThrowsAsync<IOException>(() =>
                    RngitClient.VerifyImportedObjectsAsync(directory, "sha1", [sha], CancellationToken.None));
            }
            else
                await RngitClient.VerifyImportedObjectsAsync(directory, "sha1", [sha], CancellationToken.None);
            Assert.Equal(head, await File.ReadAllTextAsync(Path.Combine(directory, "HEAD")));
            Assert.Empty(Directory.GetDirectories(directory, "rngit-verify-*"));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            TestDirectory.DeleteWithRetry(root);
        }
    }

    [Theory]
    [InlineData("http://0123456789abcdef0123456789abcdef/group/repo")]
    [InlineData("rns://0123456789abcdef0123456789abcdef/group/repo/extra")]
    [InlineData("rns://0123456789abcdef0123456789abcdef/../repo")]
    [InlineData("rns://0123456789abcdef0123456789abcdef/group/repo?query")]
    [InlineData("rns://0123456789abcdef0123456789abcdef/group/%2Frepo")]
    [InlineData("rns://short/group/repo")]
    public void Address_RejectsAmbiguousOrInvalidUrls(string url) => Assert.Throws<ArgumentException>(() => RngitAddress.Parse(url));

    [Theory]
    [InlineData("--timeout", "NaN")]
    [InlineData("--timeout", "0")]
    [InlineData("--max-response", "-1")]
    [InlineData("--timeout", "4294968")]
    public async Task Cli_RejectsInvalidLimitsBeforeOpeningNode(string option, string value)
    {
        using var error = new StringWriter();
        var code = await RngitApp.RunAsync(["clone", "rns://0123456789abcdef0123456789abcdef/group/repo", "unused", option, value],
            TextWriter.Null, error, CancellationToken.None);
        Assert.Equal(2, code);
        Assert.Contains("must be positive", error.ToString());
    }

    // Real pinned upstream ReticulumGitNode, not an emulated response fixture.
    // Exercises resource-based list, resource-based fetch request (>MDU),
    // metadata/file response resources, >25 ref batching, annotated tag and full history.
    [Trait("Category", "Live")]
    [LiveFact]
    public async Task LiveRngit_CloneMatchesUpstreamHeadTreeAndEveryRef_AndRejectsFailures()
    {
        var port = ReticulumNodeTests.FreePort();
        var root = Path.Combine(Directory.GetCurrentDirectory(), "rngit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            var repositories = Directory.CreateDirectory(Path.Combine(root, "repositories")).FullName;
            async Task<string> WorkGit(params string[] args)
            {
                var result = await RunGit(work, false, args);
                Assert.True(result.Code == 0, result.Error);
                return result.Output;
            }
            await WorkGit("init", "-b", "main");
            await WorkGit("config", "user.name", "rngit fixture");
            await WorkGit("config", "user.email", "fixture@example.invalid");
            var payload = new byte[131_101];
            new Random(971).NextBytes(payload);
            await File.WriteAllBytesAsync(Path.Combine(work, "payload.bin"), payload);
            await WorkGit("add", ".");
            await WorkGit("commit", "-m", "fixture base");
            await WorkGit("branch", "feature");
            await File.WriteAllTextAsync(Path.Combine(work, "hello.txt"), "Real upstream rngit loopback fixture\n");
            await WorkGit("add", ".");
            await WorkGit("commit", "-m", "fixture HEAD");
            await WorkGit("tag", "-a", "v1", "-m", "fixture annotated tag");
            for (var index = 0; index < 27; index++) await WorkGit("branch", $"batch-{index:00}");
            var bare = Path.Combine(repositories, "sample.git");
            await WorkGit("clone", "--bare", work, bare);
            var head = await Git(bare, "rev-parse", "HEAD");
            var tree = await Git(bare, "rev-parse", "HEAD^{tree}");
            var refs = await Git(bare, "for-each-ref", "--format=%(objectname) %(refname)");
            var parent = await Git(bare, "rev-parse", "HEAD^");
            var seedBundle = Path.Combine(root, "seed.bundle");
            await Git(bare, "bundle", "create", seedBundle, "feature");

            await using var python = await PyLinkNode.StartAsync("rngit", tcpServerPort: port, harness: "live_rngit_node.py",
                extraArguments: ["--repositories", repositories]);
            var config = Path.Combine(root, "config");
            File.WriteAllText(config, $"""
                [reticulum]
                  share_instance = no
                  enable_transport = no
                [interfaces]
                  [[Python rngit]]
                    type = TCPClientInterface
                    enabled = yes
                    target_host = 127.0.0.1
                    target_port = {port}
                """);
            var destination = Path.Combine(root, "clone.git");
            var url = $"rns://{python.HashHex}/fixtures/sample.git";
            using var output = new StringWriter();
            using var error = new StringWriter();
            async Task<int> Clone(string remote, string path, params string[] extra) =>
                await RngitApp.RunAsync(["clone", remote, path, "--config", config, "--timeout", "90", .. extra], output, error, CancellationToken.None);

            Assert.True(await Clone(url, destination) == 0, error + "\nPython:\n" + python.Log);
            Assert.Contains("HEAD " + head, output.ToString());
            Assert.Contains("TREE " + tree, output.ToString());
            Assert.Equal(head, await Git(destination, "rev-parse", "HEAD"));
            Assert.Equal(tree, await Git(destination, "rev-parse", "HEAD^{tree}"));
            Assert.Equal(refs, await Git(destination, "for-each-ref", "--format=%(objectname) %(refname)"));
            Assert.Equal("", await Git(destination, "fsck", "--full", "--strict"));
            Assert.Equal("refs/heads/main", await Git(destination, "symbolic-ref", "HEAD"));
            Assert.Equal(url, await Git(destination, "config", "remote.origin.url"));
            Assert.Equal("true", await Git(destination, "rev-parse", "--is-bare-repository"));

            var seeded = Path.Combine(root, "seeded.git");
            Directory.CreateDirectory(seeded);
            await Git(seeded, "init", "--bare", ".");
            await Git(seeded, "bundle", "unbundle", seedBundle);
            await Git(seeded, "update-ref", "refs/heads/main", parent);
            await Git(seeded, "symbolic-ref", "HEAD", "refs/heads/main");
            Assert.NotEqual(0, (await GitResult(seeded, "cat-file", "-e", head)).Code);
            Assert.NotEqual(0, (await GitResult(seeded, "cat-file", "-e", tree)).Code);
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            Assert.True(await RngitApp.RunAsync(
                ["fetch", url, seeded, "--config", config, "--timeout", "90", "--single-branch"],
                output, error, CancellationToken.None) == 0, error + "\nPython:\n" + python.Log);
            var received = Regex.Match(output.ToString(), @"Received ([0-9]+) bundle bytes");
            Assert.True(received.Success, output.ToString());
            Assert.True(long.Parse(received.Groups[1].Value) > 0, output.ToString());
            Assert.Contains("Remote HEAD refs/heads/main " + head, error.ToString());
            Assert.Equal(head, await Git(seeded, "rev-parse", "HEAD"));
            Assert.Equal(tree, await Git(seeded, "rev-parse", "HEAD^{tree}"));
            Assert.Equal(0, (await GitResult(seeded, "cat-file", "-e", head)).Code);
            Assert.Equal(0, (await GitResult(seeded, "cat-file", "-e", tree)).Code);
            Assert.Equal("", await Git(seeded, "fsck", "--full", "--strict"));
            Assert.Equal("refs/heads/main", await Git(seeded, "for-each-ref", "--format=%(refname)"));
            await Git(seeded, "merge-base", "--is-ancestor", parent, "HEAD");
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await RngitApp.RunAsync(
                ["fetch", url, seeded, "--config", config, "--timeout", "90", "--single-branch"],
                output, error, CancellationToken.None));
            Assert.Contains("Received 0 bundle bytes", output.ToString());
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            Assert.True(await RngitApp.RunAsync(
                ["fetch", url, seeded, "--config", config, "--timeout", "90"],
                output, error, CancellationToken.None) == 0, error + "\nPython:\n" + python.Log);
            Assert.Equal(refs, await Git(seeded, "for-each-ref", "--format=%(objectname) %(refname)"));
            Assert.Equal("", await Git(seeded, "fsck", "--full", "--strict"));

            Assert.Equal(1, await Clone(url, destination));
            Assert.Equal(head, await Git(destination, "rev-parse", "HEAD"));
            var denied = Path.Combine(root, "denied.git");
            error.GetStringBuilder().Clear();
            Assert.Equal(1, await Clone($"rns://{python.HashHex}/hidden/sample.git", denied));
            Assert.Contains("status 3", error.ToString());
            Assert.False(Directory.Exists(denied));

            var oversized = Path.Combine(root, "oversized.git");
            error.GetStringBuilder().Clear();
            Assert.Equal(1, await Clone(url, oversized, "--max-response", "16384"));
            Assert.False(Directory.Exists(oversized));
            Assert.Empty(Directory.GetDirectories(root, ".rngit-*"));

            var timedout = Path.Combine(root, "timeout.git");
            var clock = Stopwatch.StartNew();
            error.GetStringBuilder().Clear();
            Assert.Equal(1, await RngitApp.RunAsync(
                ["clone", "rns://00000000000000000000000000000000/fixtures/sample.git", timedout, "--config", config, "--timeout", "0.5"],
                output, error, CancellationToken.None));
            Assert.True(error.ToString().Contains("timeout expired", StringComparison.Ordinal), error.ToString());
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), clock.Elapsed.ToString());
            Assert.False(Directory.Exists(timedout));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
            TestDirectory.DeleteWithRetry(root);
        }
    }

    private static async Task<string> Git(string path, params string[] args)
    {
        var result = await GitResult(path, args);
        Assert.True(result.Code == 0, result.Error);
        return result.Output;
    }

    private static Task<(int Code, string Output, string Error)> GitResult(string path, params string[] args) =>
        RunGit(path, true, args);

    private static async Task<(int Code, string Output, string Error)> RunGit(string path, bool bare, string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (bare) info.ArgumentList.Add("--git-dir=" + path);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, (await stdout).Trim(), await stderr);
    }
}
