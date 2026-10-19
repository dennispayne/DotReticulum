using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotReticulum.Applications.Rngit;

namespace DotReticulum.Applications.Tests;

public sealed partial class RngitRepositoryStoreTests
{
    [Theory]
    [InlineData("fixture-access-token", "")]
    [InlineData("fixture-credential-user", "fixture-credential-password")]
    public async Task CredentialedExternalGitFailuresKeepExitContextWithoutLoggingSecrets(string username, string password)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await using var http = new OwnedDumbGitHttpServer(Path.Combine(directory, "public"));
        var source = new UriBuilder(http.Source) { UserName = username, Password = password }.Uri.AbsoluteUri + "/missing";
        var store = Store();
        store.AuthorizeExternalSource = candidate => candidate == source;
        var request = Request("public/credential-mirror.git");
        request["source"] = source;
        Assert.Equal(255, Bytes(await store.HandleMirrorAsync(request, owner, token))[0]);
        Assert.Empty(store.Repositories);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(directory, "public")));

        Success(await store.HandleCreateAsync(Request("public/sync.git"), owner, token));
        var path = store.Find("public/sync.git")!.Path;
        await Git(path, "config", "repository.rngit.type", "mirror");
        await Git(path, "config", "repository.rngit.upstream.source", source);
        Assert.Equal(255, Bytes(await store.HandleSyncAsync(Request("public/sync.git"), owner, token))[0]);
        Assert.Contains(diagnostics, message => message.StartsWith("Repository mirror failed (External Git exit ", StringComparison.Ordinal));
        Assert.Contains(diagnostics, message => message.StartsWith("Repository sync failed for public/sync.git (External Git exit ", StringComparison.Ordinal));
        Assert.All(diagnostics, message =>
        {
            Assert.DoesNotContain(source, message, StringComparison.Ordinal);
            Assert.DoesNotContain(username, message, StringComparison.Ordinal);
            if (password.Length > 0) Assert.DoesNotContain(password, message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task NativeAdapterFailureDiagnosticsNeverExposeExceptionMessageCredentials()
    {
        var source = "rns://0123456789abcdef0123456789abcdef/g/r";
        const string sensitive = "https://fixture-native-user:fixture-native-password@example.invalid/private";
        var store = Store();
        store.TransferRepositoryAsync = (_, _, _, _) => throw new IOException("Adapter error contains " + sensitive);
        var request = Request("public/adapter.git");
        request["source"] = source;
        Assert.Equal(255, Bytes(await store.HandleForkAsync(request, owner, CancellationToken.None))[0]);
        Success(await store.HandleCreateAsync(Request("public/sync.git"), owner, CancellationToken.None));
        var path = store.Find("public/sync.git")!.Path;
        await Git(path, "config", "repository.rngit.type", "mirror");
        await Git(path, "config", "repository.rngit.upstream.source", source);
        Assert.Equal(255, Bytes(await store.HandleSyncAsync(Request("public/sync.git"), owner, CancellationToken.None))[0]);
        Assert.Contains("Repository fork failed (IOException).", diagnostics);
        Assert.Contains("Repository sync failed for public/sync.git (IOException).", diagnostics);
        Assert.All(diagnostics, message =>
        {
            Assert.DoesNotContain(source, message, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-native-user", message, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-native-password", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Adapter error contains", message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AuthorizedDumbHttpForkMirrorManualAndPeriodicSyncRetrieveRealGitObjects()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var seed = await SeedAsync();
        await Git(seed, "update-server-info");
        var first = (await Git(seed, "rev-parse", "refs/heads/main")).Trim();
        await using var http = new OwnedDumbGitHttpServer(seed);
        using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            Assert.Contains("ref: refs/heads/main", await probe.GetStringAsync(http.Source + "/HEAD", token), StringComparison.Ordinal);

        var store = Store();
        var mirrorRequest = Request("public/http-mirror.git");
        mirrorRequest["source"] = http.Source;
        var forkRequest = Request("public/http-fork.git");
        forkRequest["source"] = http.Source;
        Assert.Equal(1, Bytes(await store.HandleMirrorAsync(mirrorRequest, owner, token))[0]);
        Assert.Equal(1, Bytes(await store.HandleForkAsync(forkRequest, owner, token))[0]);
        store.AuthorizeExternalSource = source => source == http.Source;
        Success(await store.HandleMirrorAsync(mirrorRequest, owner, token));
        Success(await store.HandleForkAsync(forkRequest, owner, token));
        var mirror = store.Find("public/http-mirror.git")!.Path;
        var fork = store.Find("public/http-fork.git")!.Path;
        foreach (var path in new[] { mirror, fork })
        {
            Assert.Equal(first, (await Git(path, "rev-parse", "refs/heads/main")).Trim());
            Assert.Equal("refs/heads/main", (await Git(path, "symbolic-ref", "HEAD")).Trim());
            Assert.Equal(http.Source, (await Git(path, "config", "repository.rngit.upstream.source")).Trim());
            await Git(path, "symbolic-ref", "HEAD", "refs/heads/maintainer");
        }
        Assert.Equal("mirror", (await Git(mirror, "config", "repository.rngit.type")).Trim());
        Assert.Equal("fork", (await Git(fork, "config", "repository.rngit.type")).Trim());

        var second = await AppendHttpCommitAsync(seed, "second HTTP object\n", first, token);
        await Git(seed, "update-ref", "refs/heads/next", second);
        await Git(seed, "symbolic-ref", "HEAD", "refs/heads/next");
        await Git(seed, "update-server-info");
        await Assert.ThrowsAsync<IOException>(() => Git(mirror, "cat-file", "-e", second));
        await Assert.ThrowsAsync<IOException>(() => Git(fork, "cat-file", "-e", second));
        Success(await store.HandleSyncAsync(mirrorRequest, owner, token));
        Success(await store.HandleSyncAsync(forkRequest, owner, token));
        Assert.Equal("refs/heads/next", (await Git(mirror, "symbolic-ref", "HEAD")).Trim());
        Assert.Equal("refs/heads/maintainer", (await Git(fork, "symbolic-ref", "HEAD")).Trim());
        foreach (var path in new[] { mirror, fork })
        {
            Assert.Equal(second, (await Git(path, "rev-parse", "refs/heads/next")).Trim());
            await Git(path, "cat-file", "-e", second + "^{commit}");
            Assert.True(long.Parse((await Git(path, "config", "repository.rngit.upstream.sync")).Trim(), CultureInfo.InvariantCulture) > 0);
        }

        var third = await AppendHttpCommitAsync(seed, "periodic HTTP object\n", second, token);
        await Git(seed, "update-ref", "refs/heads/next", third);
        await Git(seed, "update-server-info");
        await Assert.ThrowsAsync<IOException>(() => Git(mirror, "cat-file", "-e", third));
        await Git(mirror, "symbolic-ref", "HEAD", "refs/heads/maintainer");
        foreach (var path in new[] { mirror, fork })
            await Git(path, "config", "repository.rngit.upstream.sync", "0");
        store.MirrorInterval = TimeSpan.FromDays(1);
        await store.SyncMirrorsAsync(token);
        Assert.Equal(third, (await Git(mirror, "rev-parse", "refs/heads/next")).Trim());
        await Git(mirror, "cat-file", "-e", third + "^{commit}");
        Assert.Equal("refs/heads/next", (await Git(mirror, "symbolic-ref", "HEAD")).Trim());
        Assert.Equal(second, (await Git(fork, "rev-parse", "refs/heads/next")).Trim());
        Assert.Equal("refs/heads/maintainer", (await Git(fork, "symbolic-ref", "HEAD")).Trim());
        await Assert.ThrowsAsync<IOException>(() => Git(fork, "cat-file", "-e", third));
        Assert.Equal("0", (await Git(fork, "config", "repository.rngit.upstream.sync")).Trim());
        Assert.True(long.Parse((await Git(mirror, "config", "repository.rngit.upstream.sync")).Trim(), CultureInfo.InvariantCulture) > 0);
        Assert.Contains(http.ObjectRequests, request => request == "objects/" + second[..2] + "/" + second[2..]);
        Assert.Contains(http.ObjectRequests, request => request == "objects/" + third[..2] + "/" + third[2..]);
    }

    private static async Task<string> AppendHttpCommitAsync(string path, string content, string parent, CancellationToken token)
    {
        var blob = (await RngitClient.GitAsync(path, ["hash-object", "-w", "--stdin"], token, content, isolated: true)).Trim();
        var tree = (await RngitClient.GitAsync(path, ["mktree"], token, $"100644 blob {blob}\tfile.txt\n", isolated: true)).Trim();
        return (await RngitClient.GitAsync(path,
            ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit-tree", tree, "-p", parent],
            token, content, isolated: true)).Trim();
    }

    private sealed class OwnedDumbGitHttpServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly string root;
        private readonly Task serving;
        public ConcurrentQueue<string> ObjectRequests { get; } = new();
        public string Source { get; }

        public OwnedDumbGitHttpServer(string root)
        {
            this.root = Path.GetFullPath(root);
            listener.Start(32);
            Source = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/source.git";
            serving = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                    try { await ServeRequestAsync(client.GetStream(), requestTimeout.Token); }
                    catch (IOException) { /* Git may close speculative HTTP requests after discovering missing objects. */ }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }

        private async Task ServeRequestAsync(NetworkStream stream, CancellationToken token)
        {
            var header = new byte[16 * 1024];
            var length = 0;
            while (length < header.Length)
            {
                var read = await stream.ReadAsync(header.AsMemory(length, 1), token);
                if (read == 0) return;
                length += read;
                if (length >= 4 && header.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
            }
            var firstLine = Encoding.ASCII.GetString(header, 0, length).Split("\r\n", StringSplitOptions.None)[0].Split(' ');
            var relative = firstLine.Length == 3 && firstLine[0] == "GET" ? ResolvePath(firstLine[1]) : null;
            var path = relative is null ? null : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            byte[] content = [];
            var found = path is not null && File.Exists(path);
            if (found)
            {
                if (relative!.StartsWith("objects/", StringComparison.Ordinal)) ObjectRequests.Enqueue(relative);
                content = await File.ReadAllBytesAsync(path!, token);
            }
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: application/octet-stream\r\nContent-Length: {content.Length}\r\nConnection: close\r\nCache-Control: no-cache\r\n\r\n");
            await stream.WriteAsync(response, token);
            if (content.Length > 0) await stream.WriteAsync(content, token);
        }

        private static string? ResolvePath(string target)
        {
            var path = Uri.UnescapeDataString(target.Split('?')[0]);
            if (!path.StartsWith("/source.git/", StringComparison.Ordinal) || path.Any(c => char.IsControl(c) || c is '\\' or ':')) return null;
            var relative = path["/source.git/".Length..];
            if (relative.Split('/').Any(part => part is "" or "." or "..")) return null;
            return relative is "HEAD" or "info/refs" || relative.StartsWith("objects/", StringComparison.Ordinal) ? relative : null;
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; }
            finally { stop.Dispose(); }
        }
    }
}
