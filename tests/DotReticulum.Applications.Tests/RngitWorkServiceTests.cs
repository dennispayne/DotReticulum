using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rngit;
using DotReticulum.Core;

namespace DotReticulum.Applications.Tests;

/// <summary>
/// Independent vectors: RNS a29bfb51c468945c96a1ee22cae0114e993e2391 (1.5.7),
/// server.py SHA256 6257193aed6785359f4400da8d5413e7717ac5542e59666ac0f7f79e20975693.
/// GenerateRngitWorkVectors.py executes original methods with a disclosed deterministic clock.
/// </summary>
public sealed class RngitWorkServiceTests
{
    private sealed class Harness : IDisposable
    {
        public string Root { get; } = Path.Combine(FindRoot(), "TestResults", "rngit-work-" + Guid.NewGuid().ToString("N"));
        public Identity Owner { get; } = Identity.FromPrivateKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        public List<string> Diagnostics { get; } = [];
        public RngitRepositoryStore Store { get; private set; } = null!;
        public RngitWorkService Service { get; private set; } = null!;
        public string Repository => Path.Combine(Root, "group", "repo");
        public string Work => Repository + ".work";

        public Harness(string permissions = "r:all, w:all, i:all, p:all, adm:all", bool blocked = false)
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            Directory.CreateDirectory(Path.GetDirectoryName(Repository)!);
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                ArgumentList = { "init", "--quiet", "--bare", Repository },
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            Assert.True(process.WaitForExit(30000));
            Assert.Equal(0, process.ExitCode);
            File.WriteAllText(Path.Combine(Root, "config", "config"),
                $"[rngit]\nblocked_identities = {(blocked ? Owner.HexHash : "")}\n[repositories]\ngroup = '{Path.GetDirectoryName(Repository)}'\n[access]\ngroup = {permissions}\n[aliases]\nowner = {Owner.HexHash}\n");
            Restart();
        }

        public void Restart()
        {
            Store = new(RngitHostConfiguration.Load(Path.Combine(Root, "config")), Diagnostics.Add);
            Service = new(Store, Diagnostics.Add) { Clock = () => 1700000000.25 };
        }

        public Task<byte[]> Call(string op, params (object Key, object? Value)[] values) => CallAs(Owner, op, values);
        public async Task<byte[]> CallAs(Identity? identity, string op, params (object Key, object? Value)[] values) =>
            Assert.IsType<byte[]>(await Service.HandleAsync(Request(op, values), identity, CancellationToken.None));
        public Task<byte[]> Create(string op = "create", string title = "Title", string content = "Body") =>
            Call(op, ("title", title), ("content", content), ("signature", Owner.Sign(Encoding.UTF8.GetBytes(content.Trim()))));
        public async Task<byte[]> ReadPage(long id, Identity? caller, string? scope = null)
        {
            using var lease = await Store.LockRepositoryAsync("group/repo", CancellationToken.None);
            return Assert.IsType<byte[]>(scope is null
                ? await Service.ReadDocumentAsync("group/repo", id, caller, CancellationToken.None)
                : await Service.ReadDocumentAsync("group/repo", id, scope, caller, CancellationToken.None));
        }
        public async Task<byte[]> ListPage(string scope, Identity? caller)
        {
            using var lease = await Store.LockRepositoryAsync("group/repo", CancellationToken.None);
            return Assert.IsType<byte[]>(await Service.ListDocumentsAsync("group/repo", scope, caller, CancellationToken.None));
        }

        public void Dispose()
        {
            Owner.Dispose();
            Directory.Delete(Root, recursive: true);
        }

        private static string FindRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "DotReticulum.slnx"))) return d.FullName;
            throw new DirectoryNotFoundException("Repository not found.");
        }
    }

    private static Dictionary<object, object?> Request(string operation, params (object Key, object? Value)[] entries)
    {
        var request = new Dictionary<object, object?> { [0L] = "group/repo", ["operation"] = operation };
        foreach (var entry in entries) request[entry.Key] = entry.Value;
        return request;
    }
    private static Dictionary<object, object?> Payload(byte[] response)
    {
        Assert.Equal(0, response[0]);
        return Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(response.AsSpan(1)));
    }
    private static void Status(byte[] response, int code, string message = "")
    {
        Assert.Equal(code, response[0]);
        Assert.Equal(message, Encoding.UTF8.GetString(response.AsSpan(1)));
    }

    [Fact]
    public async Task PinnedReferenceResponsesAreExactIncludingPersistedDocuments()
    {
        using var h = new Harness();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rngit-work.json")));
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", fixture.RootElement.GetProperty("upstream_sha").GetString());
        Assert.Equal("6257193aed6785359f4400da8d5413e7717ac5542e59666ac0f7f79e20975693", fixture.RootElement.GetProperty("source_sha256").GetString());
        foreach (var test in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (test.TryGetProperty("repository_allowed", out var allowed))
                File.WriteAllText(h.Repository + ".allowed", allowed.GetString());
            else if (File.Exists(h.Repository + ".allowed")) File.Delete(h.Repository + ".allowed");
            if (test.TryGetProperty("document_allowed", out var documentAllowed))
                File.WriteAllText(Path.Combine(h.Work, "1.allowed"), documentAllowed.GetString());
            if (test.TryGetProperty("blocked", out _) || test.TryGetProperty("group_allowed", out _))
            {
                var path = Path.Combine(h.Root, "config", "config");
                var text = File.ReadAllText(path);
                if (test.TryGetProperty("blocked", out _)) text = text.Replace("blocked_identities = \n", $"blocked_identities = {h.Owner.HexHash}\n", StringComparison.Ordinal);
                else text = text.Replace($"blocked_identities = {h.Owner.HexHash}\n", "blocked_identities = \n", StringComparison.Ordinal);
                if (test.TryGetProperty("group_allowed", out var groupAllowed))
                    text = text.Replace("r:all, w:all, i:all, p:all, adm:all", groupAllowed.GetString(), StringComparison.Ordinal);
                File.WriteAllText(path, text);
                h.Restart();
            }
            var request = MsgPack.Unpack(Convert.FromHexString(test.GetProperty("request").GetString()!));
            var caller = test.GetProperty("identified").GetBoolean() ? h.Owner : null;
            var actual = Assert.IsType<byte[]>(await h.Service.HandleAsync(request, caller, CancellationToken.None));
            var expected = Convert.FromHexString(test.GetProperty("response").GetString()!);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Reference case {test.GetProperty("name").GetString()}: expected {Convert.ToHexString(expected)} actual {Convert.ToHexString(actual)}");
            if (test.TryGetProperty("files", out var files))
                foreach (var file in files.EnumerateObject())
                    Assert.Equal(Convert.FromHexString(file.Value.GetString()!), File.ReadAllBytes(Path.Combine(h.Work, file.Name.Replace('/', Path.DirectorySeparatorChar))));
            h.Restart();
        }
    }

    [Fact]
    public async Task ConcurrentCreatesAndCommentsHaveUniquePersistentIds()
    {
        using var h = new Harness();
        var second = new RngitWorkService(h.Store, h.Diagnostics.Add);
        var created = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(async () => i % 2 == 0 ? await h.Create()
            : Assert.IsType<byte[]>(await second.HandleAsync(Request("create", ("title", "Title"), ("content", "Body"), ("signature", h.Owner.Sign("Body"u8))), h.Owner, CancellationToken.None)))));
        Assert.Equal(Enumerable.Range(1, 24).Select(i => (long)i), created.Select(c => (long)Payload(c)["id"]!).Order());
        var comments = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => h.Call("comment", ("doc_id", 1L), ("content", $"Comment {i}")))));
        Assert.Equal(Enumerable.Range(1, 32).Select(i => (long)i), comments.Select(c => (long)Payload(c)["id"]!).Order());
        h.Restart();
        var view = Payload(await h.Call("view", ("doc_id", 1)));
        Assert.Equal(32, Assert.IsType<object?[]>(view["comments"]).Length);
        Assert.Equal(24, Assert.IsType<object?[]>(Payload(await h.Call("list"))["active"]).Length);
    }

    [Fact]
    public async Task EditValidatesUntrimmedContentButPersistsTrimmedAndOnlyOriginalAuthorMayEdit()
    {
        using var h = new Harness();
        await h.Create();
        using var other = Identity.Generate();
        Status(await h.CallAs(other, "edit", ("doc_id", 1), ("content", "changed"), ("signature", other.Sign("changed"u8))),
            1, "No access, not author");
        var signature = h.Owner.Sign(" changed "u8);
        Payload(await h.Call("edit", ("doc_id", 1), ("content", " changed "), ("title", " New "), ("signature", signature)));
        h.Restart();
        var view = Payload(await h.Call("view", ("doc_id", 1)));
        Assert.Equal("changed", view["content"]);
        var meta = Assert.IsType<Dictionary<object, object?>>(view["meta"]);
        Assert.Equal("New", meta["title"]);
        Assert.Equal(signature, Assert.IsType<byte[]>(meta["signature"]));
    }

    [Fact]
    public async Task ProposalOwnerOverridesAllowEditButNotActivationWithoutRepositoryManage()
    {
        using var h = new Harness("r:all, p:all");
        Payload(await h.Create("propose"));
        var allowed = File.ReadAllText(Path.Combine(h.Work, "1.allowed"));
        Assert.Equal($"i:{h.Owner.HexHash}\nw:{h.Owner.HexHash}\n", allowed);
        Payload(await h.Call("edit", ("doc_id", 1), ("content", "Updated"), ("signature", h.Owner.Sign("Updated"u8))));
        Status(await h.Call("activate", ("doc_id", 1)), 1, "Not allowed");
        using var other = Identity.Generate();
        Status(await h.CallAs(other, "edit", ("doc_id", 1), ("content", "Other"), ("signature", other.Sign("Other"u8))),
            1, "Not allowed");
    }

    [Fact]
    public async Task DocumentDenialHiddenBeforeDataExposureButRepositoryAdminViewBypasses()
    {
        using var h = new Harness($"r:all, w:all, i:all, p:all, adm:owner");
        await h.Create();
        File.WriteAllText(Path.Combine(h.Work, "1.allowed"), "r:none\n");
        using var other = Identity.Generate();
        Status(await h.CallAs(other, "view", ("doc_id", 1)), 3, "Document not found");
        var list = Payload(await h.CallAs(other, "list"));
        Assert.Empty(Assert.IsType<object?[]>(list["active"]));
        Payload(await h.Call("view", ("doc_id", 1)));
        // Pinned list has no repository-admin OR; none stays hidden even from admin.
        Assert.Empty(Assert.IsType<object?[]>(Payload(await h.Call("list"))["active"]));
    }

    [Fact]
    public async Task BlockedRepositoryGateCannotBeOverriddenByDocumentPermissions()
    {
        using var h = new Harness(blocked: true);
        Directory.CreateDirectory(h.Work);
        File.WriteAllText(Path.Combine(h.Work, "1.allowed"), $"adm:{h.Owner.HexHash}\nr:all\n");
        Status(await h.Call("view", ("doc_id", 1)), 3, "Not found");
        Status(await h.ReadPage(1, h.Owner), 3, "Not found");
        Assert.Empty(h.Diagnostics);
    }

    [Fact]
    public async Task PermissionAliasesValidationPersistenceAndInheritance()
    {
        using var h = new Harness("r:all, w:all, i:all, adm:owner");
        await h.Create();
        Status(await h.Call("perms", ("doc_id", 1), ("step", "set"), ("content", "# comment\nbad")),
            2, "Invalid permission \"bad\" on line 2");
        Status(await h.Call("perms", ("doc_id", 1), ("step", "set"), ("content", "r:owner\nw:none\n")), 0);
        h.Restart();
        Assert.Equal("r:owner\nw:none\n", Payload(await h.Call("perms", ("doc_id", 1), ("step", "get")))["content"]);
        using var other = Identity.Generate();
        Assert.True(h.Store.ResolveDocumentPermission(other, "group/repo", 1, "read"));
        Assert.False(h.Store.ResolveDocumentPermission(h.Owner, "group/repo", 1, "write"));
        Status(await h.CallAs(other, "perms", ("doc_id", 1), ("step", "get")), 1, "Not allowed");
        // Doc-admin alone does not bypass the top-level repository-admin gate on perms.
        File.WriteAllText(Path.Combine(h.Work, "1.allowed"), $"adm:{other.HexHash}\n");
        Status(await h.CallAs(other, "perms", ("doc_id", 1), ("step", "get")), 1, "Not allowed");
    }

    [Fact]
    public async Task CompletionActivationRetainDocumentCommentsAndMetadata()
    {
        using var h = new Harness("r:all, w:all, i:all, adm:owner");
        await h.Create();
        await h.Call("comment", ("doc_id", 1), ("content", "history"));
        var root = File.ReadAllBytes(Path.Combine(h.Work, "active", "1", "root"));
        using var other = Identity.Generate();
        Status(await h.CallAs(other, "complete", ("doc_id", 1)), 1, "Not allowed");
        Payload(await h.Call("complete", ("doc_id", 1)));
        Assert.Equal(root, File.ReadAllBytes(Path.Combine(h.Work, "completed", "1", "root")));
        Payload(await h.Call("activate", ("doc_id", 1)));
        Assert.Equal(root, File.ReadAllBytes(Path.Combine(h.Work, "active", "1", "root")));
        Assert.Single(Assert.IsType<object?[]>(Payload(await h.Call("view", ("doc_id", 1)))["comments"]));
    }

    [Fact]
    public async Task CorruptStorageDiagnosedAndNotResetAndUnwritableDestinationFails()
    {
        using var h = new Harness();
        await h.Create();
        var root = Path.Combine(h.Work, "active", "1", "root");
        byte[] corrupt = [0xc1, 0x0];
        File.WriteAllBytes(root, corrupt);
        Status(await h.Call("view", ("doc_id", 1)), 255, "Error loading document");
        Status(await h.Call("edit", ("doc_id", 1), ("content", "New"), ("signature", h.Owner.Sign("New"u8))),
            255, "Error loading document");
        Assert.Equal(corrupt, File.ReadAllBytes(root));
        Assert.NotEmpty(h.Diagnostics);
        h.Restart();
        File.Delete(root);
        Directory.CreateDirectory(root);
        // IDs remain allocated by directories; a file occupying the next document directory
        // fails persistence without touching existing state.
        File.WriteAllText(Path.Combine(h.Work, "active", "2"), "not a directory");
        File.WriteAllText(Path.Combine(h.Work, "active", "3"), "not a directory");
        // A locked parent file representing a scope is an OS-independent unwritable path.
        Directory.Delete(Path.Combine(h.Work, "active"), recursive: true);
        File.WriteAllText(Path.Combine(h.Work, "active"), "blocked");
        Status(await h.Create(), 255, "Error saving document");
        Assert.Equal("blocked", File.ReadAllText(Path.Combine(h.Work, "active")));
    }

    [Fact]
    public async Task MissingAllowedDeletionFailsWithoutDeletingDocumentThenPermsAllowsDeletionAndIdReuse()
    {
        using var h = new Harness();
        await h.Create();
        Status(await h.Call("delete", ("doc_id", 1)), 255, "Remote error");
        Assert.True(File.Exists(Path.Combine(h.Work, "active", "1", "root")));
        Status(await h.Call("perms", ("doc_id", 1), ("step", "set"), ("content", "")), 0);
        Status(await h.Call("delete", ("doc_id", 1)), 0);
        Assert.False(Directory.Exists(Path.Combine(h.Work, "active", "1")));
        Assert.Equal(1L, Payload(await h.Create())["id"]);
    }

    [Fact]
    public async Task ContentBoundaryUnicodeAndCancellationAreBounded()
    {
        using var h = new Harness();
        var text = new string('x', RngitWorkService.ContentLimit);
        Status(await h.Create(content: text), 2, "Content limit exceeded");
        var allowed = new string('x', RngitWorkService.ContentLimit - "Title".Length - "markdown".Length);
        Payload(await h.Create(content: allowed));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.HandleAsync(Request("create"), h.Owner, stop.Token));
        Assert.Single(Directory.GetDirectories(Path.Combine(h.Work, "active")));
        Status(await h.Call("comment", ("doc_id", 1), ("content", text + "x")), 2, "Content limit exceeded");
        Status(await h.Call("view", ("doc_id", "../../secret")), 2, "Invalid request");
        Status(await h.Call("view", (0, "group/../repo"), ("doc_id", 1)), 3, "Not found");
        var unicode = string.Concat(Enumerable.Repeat("\U0001f600", 1024));
        Payload(await h.Create(content: unicode));
        Assert.Equal(unicode, Payload(await h.Call("view", ("doc_id", 2)))["content"]);
        Status(await h.Call("view", ("doc_id", 1), ("scope", null)), 2, "Invalid request");
        Assert.Empty(Assert.IsType<object?[]>(Payload(await h.Call("list", ("scope", null)))["active"]));
    }

    [Fact]
    public async Task CancelledWaitingMutationDoesNotConsumeIdOrChangeStorage()
    {
        using var h = new Harness();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.Service.Clock = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return 1700000000.25;
        };
        var first = Task.Run(() => h.Create());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        using var cancellation = new CancellationTokenSource();
        var waiter = h.Service.HandleAsync(Request("comment", ("doc_id", 1), ("content", "cancelled")),
            h.Owner, cancellation.Token);
        cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter); }
        finally { release.Set(); }
        Payload(await first);
        Assert.False(File.Exists(Path.Combine(h.Work, "active", "1", "1")));
    }

    [Fact]
    public async Task OutOfRangeAllocatedIdIsAnErrorNotAResetToOne()
    {
        using var h = new Harness();
        Directory.CreateDirectory(Path.Combine(h.Work, "active", long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Status(await h.Create(), 255, "Remote error");
        Assert.False(Directory.Exists(Path.Combine(h.Work, "active", "1")));
        Assert.NotEmpty(h.Diagnostics);
    }

    [Fact]
    public async Task PageScopeIsExactWhileMgmtScopeRemainsAdvisory()
    {
        using var h = new Harness();
        await h.Create("propose");
        Payload(await h.Call("view", ("doc_id", 1), ("scope", "active")));
        Status(await h.ReadPage(1, null, "active"), 3, "Not found");
        Payload(await h.ReadPage(1, null, "proposed"));
    }

    [Fact]
    public async Task OverBudgetResponsesFailBoundedlyWithoutMutatingStoredComments()
    {
        using var h = new Harness();
        await h.Create();
        var body = new string('x', RngitWorkService.ContentLimit);
        for (var i = 0; i < 5; i++) Payload(await h.Call("comment", ("doc_id", 1), ("content", body)));
        Status(await h.Call("view", ("doc_id", 1)), 255, "Remote error");
        Assert.Equal(5, Directory.GetFiles(Path.Combine(h.Work, "active", "1")).Count(f => long.TryParse(Path.GetFileName(f), out _)));
        Assert.NotEmpty(h.Diagnostics);
    }

    [Fact]
    public async Task OptionalDiagnosticFailureDoesNotSuppressErrorResponseOrResetStorage()
    {
        using var h = new Harness();
        await h.Create();
        var path = Path.Combine(h.Work, "active", "1", "root");
        File.WriteAllBytes(path, [0xc1]);
        var service = new RngitWorkService(h.Store, _ => throw new InvalidOperationException("observer failure"));
        Status(Assert.IsType<byte[]>(await service.HandleAsync(Request("view", ("doc_id", 1)), h.Owner, CancellationToken.None)),
            255, "Error loading document");
        Assert.Equal(new byte[] { 0xc1 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task AnonymousPageReadsPublicDocumentsButMgmtAlwaysRequiresIdentification()
    {
        using var h = new Harness();
        await h.Create();
        Status(await h.CallAs(null, "view", ("doc_id", 1)), 1, "Not identified");
        Payload(await h.ReadPage(1, null));
        Assert.Single(Assert.IsType<object?[]>(Payload(await h.ListPage("active", null))["active"]));
        File.WriteAllText(Path.Combine(h.Work, "1.allowed"), "r:none\n");
        Status(await h.ReadPage(1, null), 3, "Document not found");
    }

    [Fact]
    public async Task ManagementSharesLifecycleLeaseAndRechecksAuthorizationAfterWaiting()
    {
        using var h = new Harness();
        Task<object?> pending;
        using (await h.Store.LockRepositoryAsync("group/repo", CancellationToken.None))
        {
            pending = h.Service.HandleAsync(Request("create", ("title", "Title"), ("content", "Body"),
                ("signature", h.Owner.Sign("Body"u8))), h.Owner, CancellationToken.None);
            Assert.False(pending.IsCompleted);
            File.WriteAllText(h.Repository + ".allowed", "r:none\n");
        }
        Status(Assert.IsType<byte[]>(await pending), 3, "Not found");
        Assert.False(Directory.Exists(h.Work));
    }
}
