using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rnsh;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public sealed class RnshTerminalFactAttribute : FactAttribute
{
    public RnshTerminalFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            Skip = "The rnsh PTY backend supports Windows/Linux only; Darwin terminal support remains unimplemented in TODO.md.";
    }
}

public sealed class RnshTerminalTheoryAttribute : TheoryAttribute
{
    public RnshTerminalTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            Skip = "The rnsh PTY backend supports Windows/Linux only; Darwin terminal support remains unimplemented in TODO.md.";
    }
}

public sealed class RnshTests
{
    // Executed pinned Utilities/rnsh/protocol.py; source SHA256 is retained in the fixture.
    [Fact]
    public void PinnedReferenceMessagesRoundTrip()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rnsh-vectors.json")));
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", fixture.RootElement.GetProperty("revision").GetString());
        foreach (var item in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            MessageBase message = item.GetProperty("type").GetUInt16() switch
            {
                0xac00 => new RnshNoop(), 0xac02 => new RnshWindow(), 0xac03 => new RnshExecute(),
                0xac04 => new RnshStream(), 0xac05 => new RnshVersion(), 0xac06 => new RnshError(),
                0xac07 => new RnshExited(), _ => throw new InvalidDataException(),
            };
            var raw = Convert.FromHexString(item.GetProperty("hex").GetString()!);
            message.Unpack(raw);
            Assert.Equal(raw, message.Pack());
        }
    }

    [Fact]
    public void MalformedMessagesFailWithoutStartingProcess()
    {
        Assert.Throws<FormatException>(() => new RnshExecute().Unpack(MsgPack.Pack(new object?[] { "not argv" })));
        Assert.Throws<FormatException>(() => new RnshVersion().Unpack(MsgPack.Pack(new object?[] { "version", "one" })));
        Assert.Throws<IOException>(() => new RnshStream().Unpack([0]));
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("-w", "NaN")]
    [InlineData("invalid-destination")]
    public async Task ParserRejectsInvalidInput(params string[] args)
    {
        var error = new StringWriter();
        Assert.Equal(2, await RnshApp.RunAsync(args, TextWriter.Null, error, CancellationToken.None));
        Assert.Contains("rnsh:", error.ToString());
    }

    [Fact]
    public async Task AllowedIdentityIsValidatedOnlyInListenerMode()
    {
        // args.py accepts this option syntactically; rnsh.py has no selected action.
        Assert.Equal(1, await RnshApp.RunAsync(["-a", "invalid"], TextWriter.Null, TextWriter.Null, CancellationToken.None));
        Assert.Equal(2, await RnshApp.RunAsync(["-l", "-a", "invalid"], TextWriter.Null, TextWriter.Null, CancellationToken.None));
    }

    [Fact]
    public async Task PipeChildPreservesBinaryIoAndExit()
    {
        var command = new RnshExecute { PipeStdin = true, PipeStdout = true, PipeStderr = true };
        var argv = OperatingSystem.IsWindows()
            ? new[] { "powershell.exe", "-NoProfile", "-Command", "$s=[Console]::OpenStandardInput();$o=[Console]::OpenStandardOutput();$s.CopyTo($o);[Console]::Error.Write('err');exit 7" }
            : new[] { "/bin/sh", "-c", "cat; printf err >&2; exit 7" };
        await using var child = RnshProcess.Start(argv, command, null);
        var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var reading = child.Stdout.CopyToAsync(output);
        var errors = child.Stderr.CopyToAsync(error);
        await child.Stdin.WriteAsync(data);
        child.CloseInput();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal(OperatingSystem.IsLinux() ? 0 : 7, await child.WaitAsync(deadline.Token));
        await Task.WhenAll(reading, errors).WaitAsync(deadline.Token);
        Assert.Equal(data, output.ToArray());
        Assert.Equal("err", Encoding.UTF8.GetString(error.ToArray()));
    }

    [Fact]
    public async Task FailedSpawnDoesNotPoisonNextSession()
    {
        var command = new RnshExecute { PipeStdin = true, PipeStdout = true, PipeStderr = true };
        Assert.ThrowsAny<Exception>(() => RnshProcess.Start(["dotreticulum-rnsh-missing-executable"], command, null));
        await using var child = RnshProcess.Start(["dotnet", "--version"], command, null);
        child.CloseInput();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal(0, await child.WaitAsync(deadline.Token));
    }

    [Fact]
    public void InteractiveEscapesAreFragmentSafeAndLineModeDoesNotLeakEditedBytes()
    {
        var escapes = new RnshEscapes();
        Assert.Empty(escapes.Process("~"u8, out _));
        Assert.Equal("~"u8.ToArray(), escapes.Process("~"u8, out _));
        Assert.Equal("\r"u8.ToArray(), escapes.Process("\r~L"u8, out var enabled));
        Assert.Contains("enabled", Encoding.UTF8.GetString(enabled));
        Assert.Empty(escapes.Process("ab\b"u8, out _));
        Assert.Equal("ac\r"u8.ToArray(), escapes.Process("c\r"u8, out _));
        Assert.Empty(escapes.Process("~?"u8, out var help));
        Assert.Contains("Terminate", Encoding.UTF8.GetString(help));
        Assert.Empty(escapes.Process("\r~."u8, out _).AsSpan(1).ToArray());
        Assert.True(escapes.Terminate);
    }

    [RnshTerminalFact]
    public async Task NativeTerminalHasTtyResizeAndOwnedShutdown()
    {
        var command = new RnshExecute { Window = new RnshWindow { Rows = 31, Columns = 97, HorizontalPixels = 0, VerticalPixels = 0 } };
        var argv = OperatingSystem.IsWindows()
            ? new[] { "cmd.exe", "/c", "echo terminal-ready" }
            : new[] { "/bin/sh", "-c", "test -t 0 && test -t 1 && stty size" };
        await using var child = RnshProcess.Start(argv, command, null);
        using var output = new MemoryStream();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pump = child.Stdout.CopyToAsync(output, deadline.Token);
        Assert.Equal(0, await child.WaitAsync(deadline.Token));
        await pump;
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.True(text.Contains(OperatingSystem.IsWindows() ? "terminal-ready" : "31 97", StringComparison.Ordinal),
            "Terminal output: " + JsonSerializer.Serialize(text));
    }

    [RnshTerminalFact]
    public async Task ActualTerminalFlagsChangeAndRestoreExactly()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DotReticulum.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var probe = Path.Combine(root.FullName, "tests", "Fixtures", "RnshTerminalProbe", "bin", "Release", "net10.0", "RnshTerminalProbe.dll");
        Assert.True(File.Exists(probe), "Build the owning Applications.Tests project so its terminal probe reference is built.");
        await using var child = RnshProcess.Start(["dotnet", probe], new RnshExecute
        {
            Window = new RnshWindow { Rows = 24, Columns = 80, HorizontalPixels = 0, VerticalPixels = 0 },
        }, null);
        using var output = new MemoryStream();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pump = child.Stdout.CopyToAsync(output, deadline.Token);
        Assert.Equal(0, await child.WaitAsync(deadline.Token));
        await pump;
        Assert.Contains("terminal-modes-restored", Encoding.UTF8.GetString(output.ToArray()));
    }

    [RnshTerminalTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task EachPipeAndTerminalCombinationPreservesChildEndpoints(bool inputPipe, bool outputPipe, bool errorPipe)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DotReticulum.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var probe = Path.Combine(root.FullName, "tests", "Fixtures", "RnshTerminalProbe", "bin", "Release", "net10.0", "RnshTerminalProbe.dll");
        await using var child = RnshProcess.Start(["dotnet", probe, "streams", inputPipe.ToString(), outputPipe.ToString(), errorPipe.ToString()],
            new RnshExecute { PipeStdin = inputPipe, PipeStdout = outputPipe, PipeStderr = errorPipe,
                Window = new RnshWindow { Rows = 24, Columns = 80, HorizontalPixels = 0, VerticalPixels = 0 } }, null);
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pumping = Task.WhenAll(child.Stdout.CopyToAsync(output, deadline.Token), child.Stderr.CopyToAsync(error, deadline.Token));
        Assert.Equal(0, await child.WaitAsync(deadline.Token));
        await pumping;
        var outText = Encoding.UTF8.GetString(output.ToArray());
        var errText = Encoding.UTF8.GetString(error.ToArray());
        Assert.Contains("stdout-marker", outText);
        Assert.Contains("stderr-marker", !errorPipe && !outputPipe ? outText : errText);
        if (outputPipe) Assert.DoesNotContain("stderr-marker", outText);
        if (errorPipe) Assert.DoesNotContain("stdout-marker", errText);
    }

    [Fact]
    public Task RedirectedEndpointsPreserveChildStreamsWithoutTerminalBackend() =>
        EachPipeAndTerminalCombinationPreservesChildEndpoints(true, true, true);

    [Fact]
    public async Task DisposingSessionChildKillsOwnedTreeAndSettlesWait()
    {
        var command = new RnshExecute { PipeStdin = true, PipeStdout = true, PipeStderr = true };
        var argv = OperatingSystem.IsWindows()
            ? new[] { "cmd.exe", "/c", "ping -n 120 127.0.0.1 >nul" }
            : new[] { "/bin/sh", "-c", "sleep 120 & wait" };
        // The wait continuation can race disposal, so repeat to expose an unreadable exit code.
        for (var i = 0; i < 10; i++)
        {
            var child = RnshProcess.Start(argv, command, null);
            var pending = child.WaitAsync(CancellationToken.None);
            Assert.False(pending.IsCompleted);
            await child.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            await pending.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task ChannelSessionExecutesAndRejectsRemoteCommandWithoutAffectingNextLink()
    {
        var (server, client, dir) = ReticulumNodeTests.Pair();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            using var serverIdentity = server.LoadOrCreateIdentity("rnsh");
            using var clientIdentity = client.LoadOrCreateIdentity("rnsh");
            var destination = Destination.Create(DestinationType.Single, "rnsh", [], serverIdentity);
            var runs = new List<Task>();
            var options = new RnshListenerOptions { IsAllowed = id => id.AsSpan().SequenceEqual(clientIdentity.Hash), AllowRemoteCommand = false, DefaultCommand = ["dotnet", "--version"] };
            var local = new LocalDestination(serverIdentity, destination)
            {
                AcceptLinks = true,
                LinkEstablished = link =>
                {
                    var session = new RnshSession(link);
                    runs.Add(Task.Run(async () =>
                    {
                        await using (session) await session.ServeAsync(options, deadline.Token);
                    }));
                },
            };
            server.Router.RegisterLocalDestination(local);
            await server.StartAsync(deadline.Token);
            await client.StartAsync(deadline.Token);
            await Task.Delay(300, deadline.Token);
            await server.Router.AnnounceAsync(local, cancellationToken: deadline.Token);
            async Task<string> Execute(string[] argv)
            {
                var connection = await ClientLinks.ConnectAsync(client, destination.Hash, TimeSpan.FromSeconds(10), deadline.Token);
                Assert.NotNull(connection.Link);
                await using var session = new RnshSession(connection.Link!);
                connection.Link!.Identify(clientIdentity);
                using var stdout = new MemoryStream();
                var code = await session.InitiateAsync(new RnshExecute { Command = argv, PipeStdin = true, PipeStdout = true, PipeStderr = true },
                    Stream.Null, stdout, Stream.Null, TimeSpan.FromSeconds(10), deadline.Token);
                Assert.Equal(0, code);
                return Encoding.UTF8.GetString(stdout.ToArray());
            }
            var denied = await Assert.ThrowsAsync<IOException>(() => Execute(["dotnet", "--info"]));
            Assert.Contains("not allowed", denied.Message);
            Assert.Contains("10.", await Execute([]));
            await deadline.CancelAsync();
            await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await deadline.CancelAsync();
            await client.DisposeAsync(); await server.DisposeAsync();
            Directory.Delete(dir, true);
        }
    }
}
