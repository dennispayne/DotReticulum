using System.Globalization;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rngit;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

// New vectors execute unmodified RNS 1.5.7 a29bfb51c468945c96a1ee22cae0114e993e2391.
// GenerateRngitReleaseVectors.py records exact source hashes and actual imports.
// C# owns fixture setup, request sequences, assertions and cleanup; no network is claimed.
public sealed class RngitReleaseTests
{
    private static readonly byte[] Artifact = Encoding.UTF8.GetBytes("upstream release artifact\n");
    private const string Notes = "# Heading\n\nRelease notes.\n> Quote\n";
    private const long Timestamp = 1720000000;
    private static readonly string Sha = new('1', 40);
    private sealed record Case(string Name, Dictionary<object, object?>? Data, bool Identified = true);

    private static Case[] Cases() =>
    [
        new("unidentified", new() { [0] = "fixtures/repo.git", ["operation"] = "list" }, false),
        new("non-map", null),
        new("no-repository", new() { ["operation"] = "list" }),
        new("no-operation", new() { [0] = "fixtures/repo.git" }),
        new("hidden", Request("list", repository: "hidden/repo.git")),
        new("release-denied", Request("create", "v1", "init", repository: "readonly/repo.git")),
        new("empty-list", Request("list")),
        new("thanks-not-management", Request("thanks", "v1")),
        new("missing-tag", Request("view")),
        new("invalid-tag", Request("view", "bad/tag")),
        new("missing-release", Request("view", "v1")),
        new("no-latest", Request("view", "latest")),
        new("missing-git-tag", Request("create", "absent", "init")),
        new("init", Request("create", "v1", "init", extra: new() { ["notes"] = Notes, ["hash"] = Sha })),
        new("duplicate-init", Request("create", "v1", "init")),
        new("draft-view", Request("view", "v1")),
        new("artifact-missing-data", Request("create", "v1", "artifact", extra: new() { ["artifact_name"] = "payload.bin" })),
        new("artifact", Request("create", "v1", "artifact", extra: new() { ["artifact_name"] = "payload.bin", ["artifact_data"] = Artifact })),
        new("artifact-text", Request("create", "v1", "artifact", extra: new() { ["artifact_name"] = "text.txt", ["artifact_data"] = "unicode \u03bb" })),
        new("fetch", Request("fetch", "v1", extra: new() { ["artifact"] = "payload.bin" })),
        new("missing-artifact", Request("fetch", "v1", extra: new() { ["artifact"] = "missing" })),
        new("latest", Request("latest", "v1")),
        new("finalize", Request("create", "v1", "finalize")),
        new("finalize-twice", Request("create", "v1", "finalize")),
        new("published-denies-write", Request("create", "v1", "artifact", extra: new() { ["artifact_name"] = "payload.bin", ["artifact_data"] = Artifact })),
        new("published-view", Request("view", "latest")),
        new("published-list", Request("list")),
        new("delete", Request("delete", "v1")),
        new("dangling-latest-view", Request("view", "latest")),
        new("deleted-list", Request("list")),
        new("delete-absent", Request("delete", "v1")),
    ];

    private static Dictionary<object, object?> Request(string operation, string? tag = null, string? step = null,
        string repository = "fixtures/repo.git", Dictionary<object, object?>? extra = null)
    {
        var result = new Dictionary<object, object?> { [0] = repository, ["operation"] = operation };
        if (tag is not null) result["tag"] = tag;
        if (step is not null) result["step"] = step;
        if (extra is not null) foreach (var pair in extra) result.Add(pair.Key, pair.Value);
        return result;
    }

    [Fact]
    public async Task PinnedHandlerVectorsMatchSuccessDenialDraftArtifactsPublicationAndDeletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = RepoRoot();
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_GENERATE_RELEASE") == "1")
        {
            var python = Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON")
                ?? throw new InvalidOperationException("DOTRETICULUM_PYTHON required for vector generation");
            var input = Path.Combine(fixture.Root, "requests.json");
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new
            {
                cases = Cases().Select(item => new { name = item.Name, request = item.Data is null ? "" : Convert.ToHexString(MsgPack.Pack(item.Data)), identified = item.Identified }),
                manifest = new { artifact = Convert.ToHexString(Artifact), timestamp = Timestamp,
                    released = DateTimeOffset.FromUnixTimeSeconds(Timestamp).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), commit = Sha, notes = Notes }
            }));
            var generated = await RngitProcess.RunAsync(python, [Path.Combine(root, "tests", "DotReticulum.Applications.Tests", "GenerateRngitReleaseVectors.py"),
                "--rns-config", fixture.RnsConfig, "--server-config", fixture.Config, "--requests", input,
                "--output", Path.Combine(root, "tests", "DotReticulum.Applications.Tests", "Fixtures", "rngit-release-vectors.json")],
                root, timeout: TimeSpan.FromSeconds(60), maxOutputBytes: 1024 * 1024,
                environment: new Dictionary<string, string?>
                {
                    ["GIT_CONFIG_COUNT"] = "2", ["GIT_CONFIG_KEY_0"] = "safe.bareRepository", ["GIT_CONFIG_VALUE_0"] = "all",
                    ["GIT_CONFIG_KEY_1"] = "core.autocrlf", ["GIT_CONFIG_VALUE_1"] = OperatingSystem.IsWindows() ? "true" : "false"
                });
            Assert.True(generated.Code == 0, generated.Error + Encoding.UTF8.GetString(generated.Output));
            // Recreate the independently hosted .NET fixture after upstream mutations.
            Directory.Delete(fixture.Repository + ".releases", recursive: true);
        }
        var file = Environment.GetEnvironmentVariable("DOTRETICULUM_GENERATE_RELEASE") == "1"
            ? Path.Combine(root, "tests", "DotReticulum.Applications.Tests", "Fixtures", "rngit-release-vectors.json")
            : Path.Combine(AppContext.BaseDirectory, "Fixtures", "rngit-release-vectors.json");
        using var vectors = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", vectors.RootElement.GetProperty("upstream").GetString());
        Assert.Equal("6257193aed6785359f4400da8d5413e7717ac5542e59666ac0f7f79e20975693", vectors.RootElement.GetProperty("server_sha256").GetString());
        var service = new RngitReleaseService(fixture.Store);
        foreach (var item in Cases())
        {
            var actual = await service.HandleAsync(item.Data, item.Identified ? fixture.Identity : null, CancellationToken.None);
            var expected = vectors.RootElement.GetProperty("cases").EnumerateArray().Single(vector => vector.GetProperty("name").GetString() == item.Name);
            if (expected.TryGetProperty("file", out var data))
            {
                var response = Assert.IsType<FileResponse>(actual);
                Assert.Equal(Convert.FromHexString(data.GetString()!), response.Data);
                Assert.Equal(Convert.FromHexString(expected.GetProperty("metadata").GetString()!), MsgPack.Pack(response.Metadata));
            }
            else CompareResponse(Convert.FromHexString(expected.GetProperty("response").GetString()!), Assert.IsType<byte[]>(actual), item.Name);
        }
    }

    private static void CompareResponse(byte[] expected, byte[] actual, string name)
    {
        Assert.True(expected[0] == actual[0], $"{name}: expected status {expected[0]}, actual {actual[0]}: {Encoding.UTF8.GetString(actual.AsSpan(1))}");
        if (expected[0] != 0 || expected.Length == 1) { Assert.Equal(expected, actual); return; }
        Assert.True(MsgPack.TryUnpack(expected.AsSpan(1), out var expectedData));
        Assert.True(MsgPack.TryUnpack(actual.AsSpan(1), out var actualData));
        Normalize(expectedData);
        Normalize(actualData);
        Assert.Equal(MsgPack.Pack(expectedData), MsgPack.Pack(actualData));
    }

    private static void Normalize(object? value)
    {
        if (value is Dictionary<object, object?> map)
        {
            if (map.ContainsKey("created")) map["created"] = 0;
            // Upstream enumerates artifact files with os.listdir; their order is filesystem-dependent.
            if (map.GetValueOrDefault("artifacts") is object[] artifacts)
                map["artifacts"] = artifacts.OrderBy(item => (string)((Dictionary<object, object?>)item!)["name"]!,
                    StringComparer.Ordinal).ToArray();
            foreach (var item in map.Values) Normalize(item);
        }
        else if (value is object[] list) foreach (var item in list) Normalize(item);
    }

    [Fact]
    public async Task DraftSurvivesRestartConcurrentArtifactWritesAndCancellation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new RngitReleaseService(fixture.Store);
        Assert.Equal(new byte[] { 0 }, await service.HandleAsync(Request("create", "v1", "init"), fixture.Identity, CancellationToken.None));
        var restarted = new RngitReleaseService(fixture.Store);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async index =>
        {
            Assert.Equal(new byte[] { 0 }, await (index % 2 == 0 ? restarted : service).HandleAsync(Request("create", "v1", "artifact",
                extra: new() { ["artifact_name"] = $"file-{index}.bin", ["artifact_data"] = Artifact }), fixture.Identity, CancellationToken.None));
        }));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarted.HandleAsync(
            Request("create", "v1", "finalize"), fixture.Identity, canceled.Token));
        var info = await restarted.ReadReleaseAsync(fixture.Store.Find("fixtures/repo.git")!, "v1", CancellationToken.None);
        Assert.Equal("draft", info!["status"]);
        Assert.Equal(20, Assert.IsType<Dictionary<object, object?>[]>(info["artifacts"]).Length);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Repository + ".releases", ".release*",
            SearchOption.AllDirectories));
        Assert.Equal(new byte[] { 0 }, await restarted.HandleAsync(Request("create", "v1", "finalize"), fixture.Identity, CancellationToken.None));
    }

    [Fact]
    public async Task ServiceInstancesSerializeInitAndCancellationWhileWaitingDoesNotLeakLease()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = new RngitReleaseService(fixture.Store);
        var second = new RngitReleaseService(fixture.Store);
        var results = await Task.WhenAll(first.HandleAsync(Request("create", "v1", "init"), fixture.Identity, CancellationToken.None),
            second.HandleAsync(Request("create", "v1", "init"), fixture.Identity, CancellationToken.None));
        Assert.Equal(new byte[] { 0, 1 }, results.Select(result => Assert.IsType<byte[]>(result)[0]).Order().ToArray());
        using (await fixture.Store.LockRepositoryAsync("fixtures/repo.git", CancellationToken.None))
        {
            using var cancellation = new CancellationTokenSource();
            var waiting = second.HandleAsync(Request("create", "v1", "finalize"), fixture.Identity, cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        Assert.Equal(new byte[] { 0 }, await first.HandleAsync(Request("create", "v1", "finalize"), fixture.Identity, CancellationToken.None));
        Assert.Equal(new byte[] { 0 }, await second.HandleAsync(Request("latest", "v1"), fixture.Identity, CancellationToken.None));
        Assert.Equal(new byte[] { 0 }, await second.HandleAsync(Request("latest", "v1"), fixture.Identity, CancellationToken.None));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("bad\\escape")]
    [InlineData("C:drive")]
    [InlineData("CON")]
    public async Task UnsafePathsRejectedBeforeMutation(string name)
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new RngitReleaseService(fixture.Store);
        var response = Assert.IsType<byte[]>(await service.HandleAsync(Request("delete", name), fixture.Identity, CancellationToken.None));
        Assert.Equal(2, response[0]);
        Assert.True(Directory.Exists(fixture.Repository));
    }

    [Fact]
    public async Task ThanksUsesBoundedLinkDedupAndCorruptionNeverResets()
    {
        await using var fixture = await Fixture.CreateAsync();
        var messages = new List<string>();
        var service = new RngitReleaseService(fixture.Store, messages.Add);
        await service.HandleAsync(Request("create", "v1", "init"), fixture.Identity, CancellationToken.None);
        var repository = fixture.Store.Find("fixtures/repo.git")!;
        var link = new byte[16];
        Assert.Equal(1, await service.AddThanksAsync(repository, "v1", link, CancellationToken.None));
        Assert.Equal(1, await service.AddThanksAsync(repository, "v1", link, CancellationToken.None));
        link[0] = 1;
        Assert.Equal(2, await service.AddThanksAsync(repository, "v1", link, CancellationToken.None));
        var path = Path.Combine(fixture.Repository + ".releases", "v1", "THANKS");
        await File.WriteAllBytesAsync(path, [0xc1]);
        var response = Assert.IsType<byte[]>(await service.HandleAsync(Request("view", "v1"), fixture.Identity, CancellationToken.None));
        Assert.Equal(255, response[0]);
        Assert.Equal(new byte[] { 0xc1 }, await File.ReadAllBytesAsync(path));
        Assert.Contains(messages, message => message.Contains("THANKS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedFieldsSizeLimitsAndFailedArtifactPublicationPreserveDraft()
    {
        await using var fixture = await Fixture.CreateAsync();
        var messages = new List<string>();
        var service = new RngitReleaseService(fixture.Store, messages.Add);
        foreach (var request in new[]
        {
            Request("create", "v1", "init", extra: new() { ["hash"] = 5 }),
            Request("create", "v1", "init", extra: new() { ["notes"] = 5 }),
            Request("create", "v1", "init", extra: new() { ["notes_format"] = false })
        })
            Assert.Equal(2, Assert.IsType<byte[]>(await service.HandleAsync(request, fixture.Identity, CancellationToken.None))[0]);
        Assert.False(Directory.Exists(fixture.Repository + ".releases"));
        Assert.Equal(new byte[] { 0 }, await service.HandleAsync(Request("create", "v1", "init",
            extra: new() { ["notes"] = "Micron notes", ["notes_format"] = "micron" }), fixture.Identity, CancellationToken.None));
        var directory = Path.Combine(fixture.Repository + ".releases", "v1");
        Assert.Equal("Micron notes", await File.ReadAllTextAsync(Path.Combine(directory, "RELEASE.mu")));
        var blocked = Path.Combine(directory, "artifacts", "blocked");
        Directory.CreateDirectory(blocked);
        var result = Assert.IsType<byte[]>(await service.HandleAsync(Request("create", "v1", "artifact",
            extra: new() { ["artifact_name"] = "blocked", ["artifact_data"] = Artifact }), fixture.Identity, CancellationToken.None));
        Assert.Equal(255, result[0]);
        Assert.True(Directory.Exists(blocked));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory, ".release-write-*", SearchOption.AllDirectories));
        Assert.Contains(messages, message => message.Contains("failed", StringComparison.Ordinal));
        var oversized = Path.Combine(directory, "artifacts", "oversized");
        using (var stream = File.Create(oversized)) stream.SetLength((long)RngitReleaseService.MaximumArtifactBytes + 1);
        Assert.Equal(255, Assert.IsType<byte[]>(await service.HandleAsync(Request("fetch", "v1",
            extra: new() { ["artifact"] = "oversized" }), fixture.Identity, CancellationToken.None))[0]);
        Assert.Equal("draft", (await service.ReadReleaseAsync(fixture.Store.Find("fixtures/repo.git")!, "v1", CancellationToken.None))!["status"]);
    }

    [Fact]
    public async Task CorruptMetadataIsDiagnosedWithoutSilentlyResettingDraft()
    {
        await using var fixture = await Fixture.CreateAsync();
        var messages = new List<string>();
        var service = new RngitReleaseService(fixture.Store, messages.Add);
        await service.HandleAsync(Request("create", "v1", "init"), fixture.Identity, CancellationToken.None);
        var path = Path.Combine(fixture.Repository + ".releases", "v1", "META");
        const string corrupt = "tag = v1\nstatus = draft\ncreated = not-a-number\n";
        await File.WriteAllTextAsync(path, corrupt);
        var list = Assert.IsType<byte[]>(await service.HandleAsync(Request("list"), fixture.Identity, CancellationToken.None));
        Assert.Equal(0, list[0]);
        Assert.NotEmpty(messages);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
        Assert.Equal(255, Assert.IsType<byte[]>(await service.HandleAsync(Request("view", "v1"), fixture.Identity, CancellationToken.None))[0]);
    }

    [Fact]
    public async Task LinkedReleaseDirectoryCannotBeReadOrMutatedAndAuthorizationHidesIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new RngitReleaseService(fixture.Store);
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside")).FullName;
        var sentinel = Path.Combine(outside, "sentinel");
        await File.WriteAllTextAsync(sentinel, "must remain");
        var linked = fixture.Repository + ".releases";
        if (OperatingSystem.IsWindows())
        {
            var result = await RngitProcess.RunAsync("pwsh", ["-NoProfile", "-Command",
                $"New-Item -ItemType Junction -Path '{linked.Replace("'", "''", StringComparison.Ordinal)}' -Target '{outside.Replace("'", "''", StringComparison.Ordinal)}' | Out-Null"], fixture.Root);
            Assert.True(result.Code == 0, result.Error);
        }
        else Directory.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Equal(255, Assert.IsType<byte[]>(await service.HandleAsync(Request("list"), fixture.Identity, CancellationToken.None))[0]);
            Assert.Equal(255, Assert.IsType<byte[]>(await service.HandleAsync(Request("delete", "v1"), fixture.Identity, CancellationToken.None))[0]);
            Assert.Equal(1, Assert.IsType<byte[]>(await service.HandleAsync(Request("list"), null, CancellationToken.None))[0]);
            Assert.Equal("must remain", await File.ReadAllTextAsync(sentinel));
            Directory.Move(outside, outside + "-moved");
            Assert.Equal(255, Assert.IsType<byte[]>(await service.HandleAsync(Request("list"), fixture.Identity, CancellationToken.None))[0]);
        }
        finally
        {
            if (OperatingSystem.IsWindows()) Directory.Delete(linked);
            else File.Delete(linked);
        }
    }

    [Fact]
    public void AuthenticatedManifestStillRejectsUnsafeArtifactPathsAndIncompleteOrigin()
    {
        using var signer = Identity.Generate();
        var meta = new Dictionary<object, object?> { ["name"] = "package", ["version"] = "v1",
            ["origin"] = new byte[16], ["path"] = "fixtures/repo.git",
            ["artifacts"] = new object[] { new Dictionary<object, object?> { ["name"] = "..",
                ["rsg"] = RngitSignatures.CreateRsg(signer, Artifact) } } };
        var bytes = RngitSignatures.CreateRsg(signer, Encoding.UTF8.GetBytes(Notes), true, meta);
        Assert.Throws<InvalidDataException>(() => RngitReleaseManifests.Validate(bytes));
        meta["origin"] = new byte[15];
        bytes = RngitSignatures.CreateRsg(signer, Encoding.UTF8.GetBytes(Notes), true, meta);
        Assert.Throws<InvalidDataException>(() => RngitReleaseManifests.Validate(bytes));
    }

    [Fact]
    public async Task IndependentManifestVectorsVerifyOfflineAndDetectTamperingWrongSignerMissingAndCorrupt()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var vectors = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rngit-release-vectors.json")));
        var manifest = Convert.FromHexString(vectors.RootElement.GetProperty("manifest").GetString()!);
        var created = RngitReleaseManifests.Create(fixture.Identity, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
            "fixtures/repo.git", "package", "v1", Notes, Sha, new Dictionary<string, byte[]> { ["payload.bin"] = Artifact },
            DateTimeOffset.FromUnixTimeSeconds(Timestamp));
        Assert.Equal(manifest, created);
        var path = Path.Combine(fixture.Root, "manifest.rsm");
        await File.WriteAllBytesAsync(path, manifest);
        var artifactPath = Path.Combine(fixture.Root, "payload.bin");
        await File.WriteAllBytesAsync(artifactPath, Artifact);
        Assert.True((await RngitReleaseClient.VerifyAsync(path, fixture.Identity.Hash.ToArray())).IsValid);
        Assert.True((await RngitReleaseClient.VerifyAsync(path, artifactPattern: "p[ae]yload.*")).IsValid);
        Assert.True((await RngitReleaseClient.VerifyAsync(path, artifactPattern: "[!q]*.bin")).IsValid);
        await Assert.ThrowsAsync<InvalidDataException>(() => RngitReleaseClient.VerifyAsync(path, artifactPattern: "missing*"));
        using var other = Identity.Generate();
        await Assert.ThrowsAsync<InvalidDataException>(() => RngitReleaseClient.VerifyAsync(path, other.Hash.ToArray()));
        await File.WriteAllBytesAsync(artifactPath, [1, 2, 3]);
        Assert.False((await RngitReleaseClient.VerifyAsync(path)).IsValid);
        File.Delete(artifactPath);
        Assert.False((await RngitReleaseClient.VerifyAsync(path)).IsValid);
        manifest[0] ^= 1;
        await File.WriteAllBytesAsync(path, manifest);
        await Assert.ThrowsAsync<InvalidDataException>(() => RngitReleaseClient.VerifyAsync(path));
    }

    [Fact]
    public async Task LocalPackagingUsesActualRsmAndSidecarsWithoutOpeningNetwork()
    {
        await using var fixture = await Fixture.CreateAsync();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "dist")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(directory, "payload.bin"), Artifact);
        var result = await RngitReleaseClient.CreateAsync(null, fixture.Identity, fixture.Identity,
            "rns://000102030405060708090a0b0c0d0e0f/fixtures/repo.git", "v1:" + directory,
            "package", true, CancellationToken.None, Notes);
        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(File.Exists(Path.Combine(directory, "payload.bin.rsg")));
        Assert.True((await RngitReleaseClient.VerifyAsync(result.ManifestPath, fixture.Identity.Hash.ToArray())).IsValid);
        await Assert.ThrowsAsync<ArgumentException>(() => RngitReleaseClient.CreateAsync(null, fixture.Identity, fixture.Identity,
            "rns://000102030405060708090a0b0c0d0e0f/fixtures/repo.git", "v1:" + directory,
            "package", true, CancellationToken.None));
    }

    private static string RepoRoot()
    {
        return PresentationProcessFixture.RepositoryRoot();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required string Repository { get; init; }
        public required string Config { get; init; }
        public required string RnsConfig { get; init; }
        public required Identity Identity { get; init; }
        public required RngitRepositoryStore Store { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Directory.CreateDirectory(Path.Combine(RepoRoot(), "TestResults", "release-" + Guid.NewGuid().ToString("N"))).FullName;
            var config = Directory.CreateDirectory(Path.Combine(root, "config")).FullName;
            var rns = Directory.CreateDirectory(Path.Combine(root, "rns")).FullName;
            await File.WriteAllTextAsync(Path.Combine(rns, "config"), "[reticulum]\n  enable_transport = false\n  share_instance = false\n[interfaces]\n");
            var group = Directory.CreateDirectory(Path.Combine(root, "repositories")).FullName;
            var repository = Path.Combine(group, "repo.git");
            var git = await RngitProcess.RunAsync("git", ["init", "--bare", repository], root);
            Assert.True(git.Code == 0, git.Error);
            var tree = await RngitProcess.RunAsync("git", ["--git-dir=" + repository, "mktree"], repository, stdin: []);
            Assert.True(tree.Code == 0, tree.Error);
            var commit = await RngitProcess.RunAsync("git", ["--git-dir=" + repository, "-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid",
                "commit-tree", Encoding.UTF8.GetString(tree.Output).Trim(), "-m", "release fixture"], repository);
            Assert.True(commit.Code == 0, commit.Error);
            var tag = await RngitProcess.RunAsync("git", ["--git-dir=" + repository, "update-ref", "refs/tags/v1", Encoding.UTF8.GetString(commit.Output).Trim()], repository);
            Assert.True(tag.Code == 0, tag.Error);
            var readonlyGroup = Directory.CreateDirectory(Path.Combine(root, "readonly")).FullName;
            var hiddenGroup = Directory.CreateDirectory(Path.Combine(root, "hidden")).FullName;
            foreach (var directory in new[] { readonlyGroup, hiddenGroup })
            {
                var clone = await RngitProcess.RunAsync("git", ["clone", "--bare", repository, Path.Combine(directory, "repo.git")], root);
                Assert.True(clone.Code == 0, clone.Error);
            }
            await File.WriteAllTextAsync(Path.Combine(config, "config"), $"[rngit]\nannounce_interval = 0\nrecord_stats = false\n[repositories]\nfixtures = {group.Replace('\\', '/')}\nreadonly = {readonlyGroup.Replace('\\', '/')}\nhidden = {hiddenGroup.Replace('\\', '/')}\n[access]\nfixtures = r:all, rel:all\nreadonly = r:all\nhidden = r:none\n[pages]\nserve_nomadnet = false\n");
            return new() { Root = root, Repository = repository, Config = config, RnsConfig = rns,
                Identity = Identity.FromPrivateKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()),
                Store = new RngitRepositoryStore(RngitHostConfiguration.Load(config)) };
        }

        public ValueTask DisposeAsync()
        {
            Identity.Dispose();
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
