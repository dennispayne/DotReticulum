using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rngit;
using DotReticulum.Core;

namespace DotReticulum.Applications.Tests;

// Pinned server.py:2097-2172,2262-2645,2713-2838,3141-3366,4443-4643.
// SHA-256 6257193aed6785359f4400da8d5413e7717ac5542e59666ac0f7f79e20975693.
// GenerateRngitRepositoryStoreVectors.py executes actual upstream handlers. Its Windows
// static .allowed classification shim is recorded in the fixture; not live interop.
[Trait("Category", "Integration")]
public sealed partial class RngitRepositoryStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Directory.GetCurrentDirectory(), "rngit-store-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Identity owner = Identity.FromPrivateKey(Enumerable.Range(1, 64).Select(i => (byte)i).ToArray());
    private readonly JsonDocument vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rngit-repository-store.json")));
    private readonly List<string> diagnostics = [];

    public RngitRepositoryStoreTests()
    {
        foreach (var group in new[] { "public", "readonly", "hidden" }) Directory.CreateDirectory(Path.Combine(directory, group));
    }

    public void Dispose()
    {
        owner.Dispose();
        vectors.Dispose();
        TestDirectory.DeleteWithRetry(directory);
    }

    private RngitHostConfiguration Config(bool blocked = false)
    {
        File.WriteAllText(Path.Combine(directory, "config"),
            $"[rngit]\nblocked_identities = {(blocked ? owner.HexHash : "")}\n[repositories]\n"
            + $"public = \"{Path.Combine(directory, "public")}\"\nreadonly = \"{Path.Combine(directory, "readonly")}\"\nhidden = \"{Path.Combine(directory, "hidden")}\"\n"
            + $"[aliases]\nowner = {owner.HexHash}\n[access]\npublic = r:all, c:all, adm:owner\nreadonly = r:all\nhidden = r:none\n");
        return RngitHostConfiguration.Load(directory);
    }

    private RngitRepositoryStore Store(bool blocked = false) => new(Config(blocked), diagnostics.Add);
    private static Dictionary<object, object?> Request(string repository) => new() { [0] = repository };
    private static Dictionary<object, object?> Perms(string scope, bool group, string step, object? content = null) =>
        new() { [group ? 2 : 0] = scope, ["operation"] = group ? "gperms" : "rperms", ["step"] = step, ["content"] = content };
    private static byte[] Bytes(object? value) => Assert.IsType<byte[]>(value);
    private void Success(object? value)
    {
        var bytes = Bytes(value);
        Assert.True(bytes.AsSpan().SequenceEqual(new byte[] { 0 }),
            (bytes.Length > 1 ? System.Text.Encoding.UTF8.GetString(bytes.AsSpan(1)) : Convert.ToHexString(bytes))
            + Environment.NewLine + string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task ActualUpstreamHandlerResponsesMatchExactly()
    {
        var store = Store();
        using var other = Identity.FromPublicKey(Convert.FromHexString(vectors.RootElement.GetProperty("other_public").GetString()!));
        Assert.Equal(vectors.RootElement.GetProperty("identity_hash").GetString(), owner.HexHash);
        var requests = new (string Name, string Verb, object? Data, Identity? Caller)[]
        {
            ("unidentified", "create", Request("public/new.git"), null),
            ("invalid-map", "create", null, owner),
            ("no-repository", "create", new Dictionary<object, object?>(), owner),
            ("invalid-path", "create", Request("bad"), owner),
            ("missing-group", "create", Request("absent/new.git"), owner),
            ("read-only-create", "create", Request("readonly/new.git"), owner),
            ("hidden-create", "create", Request("hidden/new.git"), owner),
            ("create", "create", Request("public/new.git"), owner),
            ("existing", "create", Request("public/new.git"), owner),
            ("ordinary-sync", "sync", Request("public/new.git"), owner),
            ("perms-invalid", "perms", new Dictionary<object, object?>(), owner),
            ("group-get", "perms", Perms("public", true, "get"), owner),
            ("repo-get", "perms", Perms("public/new.git", false, "get"), owner),
            ("repo-admin-cannot-set", "perms", Perms("public/new.git", false, "set", "r:all"), other),
            ("invalid-permission", "perms", Perms("public", true, "set", "# note\nbad:all"), owner),
            ("group-set", "perms", Perms("public", true, "set", "w:owner\n"), owner),
            ("group-reloaded-get", "perms", Perms("public", true, "get"), owner),
            ("repo-set", "perms", Perms("public/new.git", false, "set", "r:all\nw:none\n"), owner),
            ("repo-reloaded-get", "perms", Perms("public/new.git", false, "get"), owner),
        };
        var expected = vectors.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!, v => Convert.FromHexString(v.GetProperty("response").GetString()!));
        foreach (var (name, verb, data, caller) in requests)
        {
            if (name == "repo-set") File.Delete(Path.Combine(directory, "public", "new.git.allowed"));
            var actual = verb switch
            {
                "create" => await store.HandleCreateAsync(data, caller, CancellationToken.None),
                "sync" => await store.HandleSyncAsync(data, caller, CancellationToken.None),
                _ => await store.HandlePermissionsAsync(data, caller, CancellationToken.None),
            };
            Assert.True(expected[name].SequenceEqual(Bytes(actual)), $"{name}: {Convert.ToHexString(Bytes(actual))}");
        }
    }

    [Fact]
    public async Task ActualUpstreamPrecedenceAndBlocklistDecisionsMatch()
    {
        Success(await Store().HandleCreateAsync(Request("public/new.git"), owner, CancellationToken.None));
        File.WriteAllText(Path.Combine(directory, "public.allowed"), "w:owner\n");
        var repoPath = Path.Combine(directory, "public", "new.git");
        Directory.CreateDirectory(repoPath + ".work");
        foreach (var vector in vectors.RootElement.GetProperty("decisions").EnumerateArray())
        {
            File.WriteAllText(repoPath + ".allowed", vector.GetProperty("allowed").GetString());
            File.WriteAllText(Path.Combine(repoPath + ".work", "1.allowed"), vector.GetProperty("document").GetString());
            var store = Store(vector.GetProperty("blocked").GetBoolean());
            Assert.Equal(vector.GetProperty("repository").GetBoolean(), store.ResolvePermission(owner, "public/new.git", "write"));
            Assert.Equal(vector.GetProperty("group").GetBoolean(), store.ResolveGroupPermission(owner, "public", "write"));
            Assert.Equal(vector.GetProperty("doc").GetBoolean(), store.ResolveDocumentPermission(owner, "public/new.git", 1, "write"));
        }
    }

    [Fact]
    public async Task DecodedNumericMapKeysAndPythonLineSeparatorsAreSupported()
    {
        var store = Store();
        Success(await store.HandleCreateAsync(MsgPack.Unpack(MsgPack.Pack(Request("public/new.git"))), owner, CancellationToken.None));
        var request = Perms("public/new.git", false, "set", "r:all\r\nw:none\u2028i:all");
        Success(await store.HandlePermissionsAsync(MsgPack.Unpack(MsgPack.Pack(request)), owner, CancellationToken.None));
        Assert.False(store.ResolvePermission(owner, "public/new.git", "write"));
        Assert.True(store.ResolveAnonymousPermission("public/new.git", "interact"));
    }

    [Theory]
    [InlineData("public/../evil")]
    [InlineData("public/..")]
    [InlineData("../evil")]
    [InlineData("public/C:evil")]
    [InlineData("public/a\\b")]
    [InlineData("public/NUL")]
    [InlineData("public/aux.git")]
    [InlineData("public/COM1")]
    [InlineData("public/x.")]
    [InlineData("public/x ")]
    [InlineData("public/")]
    [InlineData("/x")]
    public async Task UnsafePathsAreRejectedWithoutFilesystemChanges(string path)
    {
        var store = Store();
        Assert.Equal(2, Bytes(await store.HandleCreateAsync(Request(path), owner, CancellationToken.None))[0]);
        Assert.Empty(store.Repositories);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(directory, "public")));
    }

    [Fact]
    public async Task ConcurrentCreatesRegisterExactlyOnceAndExposeDetachedSnapshot()
    {
        var store = Store();
        var old = store.Repositories;
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.HandleCreateAsync(Request("public/new.git"), owner, CancellationToken.None)));
        Assert.Single(results, r => Bytes(r).SequenceEqual(new byte[] { 0 }));
        Assert.Empty(old);
        Assert.Single(store.Repositories);
        Assert.NotNull(Store().Find("public/new.git"));
        Assert.Equal("adm:" + owner.HexHash, File.ReadAllText(Path.Combine(directory, "public", "new.git.allowed")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory, "public"), ".rngit-store-*"));
    }

    [Fact]
    public async Task DiscoveryRequiresBareRepositoriesAndExcludesWorkAndReleaseDirectories()
    {
        foreach (var name in new[] { "valid", "ignored.work", "ignored.releases", "not-bare" })
        {
            var path = Path.Combine(directory, "public", name);
            Directory.CreateDirectory(path);
            await Git(path, "init", "--bare", ".");
            if (name == "not-bare") await Git(path, "config", "core.bare", "false");
        }
        Directory.CreateDirectory(Path.Combine(directory, "public", "not-git"));
        var store = Store();
        Assert.Single(store.Repositories);
        Assert.NotNull(store.Find("public/valid"));
        Assert.Null(store.Find("public/not-bare"));
        Assert.Null(store.Find("public/ignored.work"));
    }

    [Fact]
    public async Task PermissionsPersistOverwriteReloadAndRejectInvalidContent()
    {
        var store = Store();
        Success(await store.HandleCreateAsync(Request("public/new.git"), owner, CancellationToken.None));
        Success(await store.HandlePermissionsAsync(Perms("public/new.git", false, "set", "w:none\n"), owner, CancellationToken.None));
        Assert.False(Store().ResolvePermission(owner, "public/new.git", "write"));
        Assert.Equal(2, Bytes(await store.HandlePermissionsAsync(Perms("public/new.git", false, "set", "invalid:all"), owner, CancellationToken.None))[0]);
        Assert.False(store.ResolvePermission(owner, "public/new.git", "write"));
        Assert.Equal(2, Bytes(await store.HandlePermissionsAsync(Perms("public", true, "set", new string('x', RngitRepositoryStore.MaxPermissionBytes + 1)), owner, CancellationToken.None))[0]);
        Assert.Equal(255, Bytes(await store.HandlePermissionsAsync(Perms("public", true, "set", 42), owner, CancellationToken.None))[0]);
        File.WriteAllText(Path.Combine(directory, "public", "new.git.allowed"), new string('x', RngitRepositoryStore.MaxPermissionBytes + 1));
        Assert.False(store.ResolvePermission(owner, "public/new.git", "read"));
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public async Task RepositoryAdminCanReadButCannotSetWithoutGroupAdmin()
    {
        var store = Store();
        Success(await store.HandleCreateAsync(Request("public/new.git"), owner, CancellationToken.None));
        using var other = Identity.Generate();
        File.WriteAllText(Path.Combine(directory, "public", "new.git.allowed"), "adm:" + other.HexHash);
        Assert.Equal(0, Bytes(await store.HandlePermissionsAsync(Perms("public/new.git", false, "get"), other, CancellationToken.None))[0]);
        Assert.Equal(1, Bytes(await store.HandlePermissionsAsync(Perms("public/new.git", false, "set", "r:all"), other, CancellationToken.None))[0]);
    }

    [Fact]
    public async Task MirrorForkUseAuthorizedExternalGitAndPreserveDifferentHeadSemantics()
    {
        var seed = await SeedAsync();
        var source = new Uri(seed).AbsoluteUri;
        var store = Store();
        var request = Request("public/mirror.git");
        request["source"] = source;
        Assert.Equal(1, Bytes(await store.HandleMirrorAsync(request, owner, CancellationToken.None))[0]);
        store.AuthorizeExternalSource = url => url == source;
        Success(await store.HandleMirrorAsync(request, owner, CancellationToken.None));
        request[0] = "public/fork.git";
        Success(await store.HandleForkAsync(request, owner, CancellationToken.None));
        foreach (var type in new[] { "mirror", "fork" })
        {
            var repo = store.Find("public/" + type + ".git")!;
            Assert.Equal(type, (await Git(repo.Path, "config", "repository.rngit.type")).Trim());
            Assert.Equal(source, (await Git(repo.Path, "config", "repository.rngit.upstream.source")).Trim());
            Assert.True(long.Parse((await Git(repo.Path, "config", "repository.rngit.upstream.sync")).Trim()) > 0);
            await Git(repo.Path, "symbolic-ref", "HEAD", "refs/heads/maintainer");
            Success(await store.HandleSyncAsync(Request("public/" + type + ".git"), owner, CancellationToken.None));
            Assert.Equal(type == "mirror" ? "refs/heads/main" : "refs/heads/maintainer", (await Git(repo.Path, "symbolic-ref", "HEAD")).Trim());
        }
        var calls = 0;
        store.AuthorizeExternalSource = _ => { calls++; return true; };
        store.MirrorInterval = TimeSpan.Zero;
        await Task.Delay(1100);
        await store.SyncMirrorsAsync(CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NativeTransferCallbackIsRequiredAndFailureOrCancellationCleansOwnedPaths()
    {
        var store = Store();
        var request = Request("public/native.git");
        request["source"] = "rns://0123456789abcdef0123456789abcdef/group/source.git";
        Assert.Equal(255, Bytes(await store.HandleForkAsync(request, owner, CancellationToken.None))[0]);
        store.TransferRepositoryAsync = (_, path, _, _) =>
        {
            Directory.CreateDirectory(path);
            var partial = Path.Combine(path, "partial");
            File.WriteAllText(partial, "partial");
            File.SetAttributes(partial, FileAttributes.ReadOnly);
            throw new IOException("Injected transfer failure");
        };
        Assert.Equal(255, Bytes(await store.HandleMirrorAsync(request, owner, CancellationToken.None))[0]);
        store.TransferRepositoryAsync = async (_, path, fetch, token) =>
        {
            Assert.False(fetch);
            Directory.CreateDirectory(path);
            await Task.Delay(Timeout.Infinite, token);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.HandleForkAsync(request, owner, stop.Token));
        Assert.Empty(store.Repositories);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(directory, "public")));
    }

    [Fact]
    public async Task ProtectedStateAndTransfersAreNotAccessedBeforeAuthorization()
    {
        var store = Store();
        var calls = 0;
        store.TransferRepositoryAsync = (_, _, _, _) => { calls++; throw new IOException("Must not execute"); };
        var request = Request("hidden/new.git");
        request["source"] = "rns://0123456789abcdef0123456789abcdef/g/r";
        Assert.Equal(1, Bytes(await store.HandleForkAsync(request, null, CancellationToken.None))[0]);
        Assert.Equal(3, Bytes(await store.HandleMirrorAsync(request, owner, CancellationToken.None))[0]);
        Assert.Equal(0, calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(directory, "hidden")));
    }

    [Fact]
    public async Task NativeMirrorAndForkRestoreHeadOnSuccessfulFetchAndFailure()
    {
        var store = Store();
        store.TransferRepositoryAsync = async (_, path, fetch, token) =>
        {
            if (!fetch) Directory.CreateDirectory(path);
            if (!fetch) await RngitClient.GitAsync(path, ["init", "--bare", "."], token, isolated: true);
            await RngitClient.GitAsync(path, ["symbolic-ref", "HEAD", "refs/heads/upstream"], token, isolated: true);
        };
        foreach (var type in new[] { "mirror", "fork" })
        {
            var request = Request("public/" + type + ".git");
            request["source"] = "rns://0123456789abcdef0123456789abcdef/g/r";
            Success(type == "mirror" ? await store.HandleMirrorAsync(request, owner, CancellationToken.None)
                : await store.HandleForkAsync(request, owner, CancellationToken.None));
            var path = store.Find((string)request[0]!)!.Path;
            await Git(path, "symbolic-ref", "HEAD", "refs/heads/local");
            Success(await store.HandleSyncAsync(request, owner, CancellationToken.None));
            Assert.Equal(type == "fork" ? "refs/heads/local" : "refs/heads/upstream", (await Git(path, "symbolic-ref", "HEAD")).Trim());
        }
        store.TransferRepositoryAsync = async (_, path, _, token) =>
        {
            await RngitClient.GitAsync(path, ["symbolic-ref", "HEAD", "refs/heads/failed-transfer"], token, isolated: true);
            throw new IOException("Injected fetch failure");
        };
        Assert.Equal(255, Bytes(await store.HandleSyncAsync(Request("public/fork.git"), owner, CancellationToken.None))[0]);
        Assert.Equal("refs/heads/local", (await Git(store.Find("public/fork.git")!.Path, "symbolic-ref", "HEAD")).Trim());
    }

    [Fact]
    public async Task AnonymousGrantsNeverUseIdentityAdminOrNonemptyOverrides()
    {
        var store = Store();
        Success(await store.HandleCreateAsync(Request("public/new.git"), owner, CancellationToken.None));
        Assert.True(store.ResolveAnonymousPermission("public/new.git", "read"));
        Assert.False(store.ResolveAnonymousPermission("public/new.git", "write"));
        var path = store.Find("public/new.git")!.Path;
        File.WriteAllText(path + ".allowed", "r:" + owner.HexHash);
        Assert.False(store.ResolveAnonymousPermission("public/new.git", "read"));
        Directory.CreateDirectory(path + ".work");
        File.WriteAllText(Path.Combine(path + ".work", "1.allowed"), "r:all");
        Assert.True(store.ResolveAnonymousDocumentPermission("public/new.git", 1, "read"));
        File.WriteAllText(Path.Combine(path + ".work", "1.allowed"), "r:none");
        Assert.False(store.ResolveAnonymousDocumentPermission("public/new.git", 1, "read"));
    }

    [Fact]
    public async Task SharedRepositoryLockSerializesSyncWithoutDoubleAcquisition()
    {
        var store = Store();
        Success(await store.HandleCreateAsync(Request("public/new.git"), owner, CancellationToken.None));
        using var held = await store.LockRepositoryAsync("public/new.git", CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.HandleSyncAsync(Request("public/new.git"), owner, stop.Token));
    }

    [RngitUnixFact]
    public async Task LinkedRepositoryAndDanglingPermissionFileAreNeverTraversed()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The fact attribute must skip Unix link tests on Windows.");
        var seed = await SeedAsync();
        Directory.CreateSymbolicLink(Path.Combine(directory, "public", "linked.git"), seed);
        var store = Store();
        Assert.Null(store.Find("public/linked.git"));
        Success(await store.HandleCreateAsync(Request("public/new.git"), owner, CancellationToken.None));
        var permission = store.Find("public/new.git")!.Path + ".allowed";
        File.Delete(permission);
        var missing = Path.Combine(directory, "must-not-create");
        File.CreateSymbolicLink(permission, missing);
        Assert.False(store.ResolvePermission(owner, "public/new.git", "read"));
        Assert.Equal(3, Bytes(await store.HandlePermissionsAsync(Perms("public/new.git", false, "set", "r:all"), owner, CancellationToken.None))[0]);
        Assert.False(File.Exists(missing));
    }

    [RngitUnixFact]
    public async Task ExecutablePermissionsFailClosedRequireInjectionAndCannotBeModifiedRemotely()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The fact attribute must skip Unix mode tests on Windows.");
        var store = Store();
        Success(await store.HandleCreateAsync(Request("public/new.git"), owner, CancellationToken.None));
        var path = Path.Combine(directory, "public", "new.git.allowed");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.False(store.ResolvePermission(owner, "public/new.git", "read"));
        store.PermissionExecutor = _ => "adm:" + owner.HexHash;
        Assert.True(store.ResolvePermission(owner, "public/new.git", "read"));
        Assert.Equal(1, Bytes(await store.HandlePermissionsAsync(Perms("public/new.git", false, "set", "r:all"), owner, CancellationToken.None))[0]);
        store.PermissionExecutor = _ => new string('x', RngitRepositoryStore.MaxPermissionBytes + 1);
        Assert.False(store.ResolvePermission(owner, "public/new.git", "read"));
    }

    private async Task<string> SeedAsync()
    {
        var path = Path.Combine(directory, "seed.git");
        Directory.CreateDirectory(path);
        await Git(path, "init", "--bare", ".");
        var blob = (await RngitClient.GitAsync(path, ["hash-object", "-w", "--stdin"], CancellationToken.None, "fixture\n", isolated: true)).Trim();
        var tree = (await RngitClient.GitAsync(path, ["mktree"], CancellationToken.None, $"100644 blob {blob}\tfile.txt\n", isolated: true)).Trim();
        var commit = (await RngitClient.GitAsync(path, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit-tree", tree],
            CancellationToken.None, "fixture\n", isolated: true)).Trim();
        await Git(path, "update-ref", "refs/heads/main", commit);
        await Git(path, "symbolic-ref", "HEAD", "refs/heads/main");
        return path;
    }

    private static Task<string> Git(string path, params string[] args) => RngitClient.GitAsync(path, args, CancellationToken.None, isolated: true);
}
