using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rncp;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Rnx;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

// Oracle: GenerateParity7TransferVectors.py executes pinned RNS a29bfb51c468945c96a1ee22cae0114e993e2391.
// Source hashes and verified actual import path are recorded in Fixtures/Parity7Transfer.json.
[Trait("Category", "Unit")]
public sealed class Parity7TransferTests
{
    internal static string Command(string script) =>
        "pwsh -NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    [Fact]
    public void BinaryOutputAndPythonSliceLimits_AgreeWithPinnedHandler()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Parity7Transfer.json")));
        var command = Command("[Console]::OpenStandardOutput().Write([byte[]](0,255,65,66,67,68),0,6);"
            + "[Console]::OpenStandardError().Write([byte[]](254,0,69,70),0,4);exit 7");
        foreach (var expected in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            long? limit = expected.GetProperty("limit").ValueKind == JsonValueKind.Null ? null : expected.GetProperty("limit").GetInt64();
            double? timeout = expected.GetProperty("timeout").ValueKind == JsonValueKind.Null ? null : expected.GetProperty("timeout").GetDouble();
            var actual = RnxListener.Execute(command, timeout, limit, limit, null);
            Assert.Equal(expected.GetProperty("executed").GetBoolean(), actual[0]);
            Assert.Equal(expected.GetProperty("returnCode").GetInt64(), actual[1]);
            Assert.Equal(Convert.FromHexString(expected.GetProperty("stdout").GetString()!), (byte[])actual[2]!);
            Assert.Equal(Convert.FromHexString(expected.GetProperty("stderr").GetString()!), (byte[])actual[3]!);
            Assert.Equal(expected.GetProperty("stdoutLength").GetInt64(), actual[4]);
            Assert.Equal(expected.GetProperty("stderrLength").GetInt64(), actual[5]);
            Assert.Equal(expected.GetProperty("concluded").GetBoolean(), actual[7] is not null);
        }
        var missing = RnxListener.Execute("dotreticulum-no-such-parity7-command", 1, null, null, null);
        Assert.Equal(false, missing[0]);
        Assert.All(missing[1..6], Assert.Null);
    }

    [Fact]
    public void OutputIsDrainedButRetainedMemoryIsBounded()
    {
        var command = Command("$b=[byte[]]::new(2097152);[Console]::OpenStandardOutput().Write($b,0,$b.Length);"
            + "[Console]::OpenStandardError().Write($b,0,$b.Length)");
        var result = RnxListener.Execute(command, 20, null, -1, null, maxOutputBytes: 4096);
        Assert.Equal(0L, result[1]);
        Assert.Equal(4096, ((byte[])result[2]!).Length);
        Assert.Equal(4096, ((byte[])result[3]!).Length);
        Assert.Equal(2097152L, result[4]);
        Assert.Equal(2097152L, result[5]);
    }

    [Fact]
    public void TimeoutCoversBlockedStdinAndRetainsPartialOutput()
    {
        var command = Command("[Console]::OpenStandardOutput().WriteByte(65);Start-Sleep -Seconds 30");
        var clock = Stopwatch.StartNew();
        var result = RnxListener.Execute(command, 2, null, null, new byte[2 * 1024 * 1024]);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(12));
        Assert.Equal(true, result[0]);
        Assert.NotEqual(0L, result[1]);
        Assert.Equal(new byte[] { 65 }, (byte[])result[2]!);
        Assert.Null(result[7]);
    }

    [Fact]
    public async Task CancellationKillsOwnedProcessTree()
    {
        var child = Convert.ToBase64String(Encoding.Unicode.GetBytes("Start-Sleep -Seconds 60"));
        var command = Command("$p=Start-Process pwsh -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','" + child
            + "' -PassThru -NoNewWindow;[Console]::WriteLine($p.Id);Start-Sleep -Seconds 60");
        var result = RnxListener.Execute(command, 3, null, null, null);
        Assert.Null(result[7]);
        Assert.True(int.TryParse(Encoding.UTF8.GetString((byte[])result[2]!).Trim(), out var childPid), "No owned child PID was observed.");
        try
        {
            using var process = Process.GetProcessById(childPid);
            Assert.True(process.HasExited || process.WaitForExit(5000), "Owned descendant survived timeout.");
        }
        catch (ArgumentException) { }

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Run(() => RnxListener.Execute(Command("Start-Sleep -Seconds 60"), 30, null, null, null, cancel.Token)));
    }

    [Theory]
    [InlineData("rncp")]
    [InlineData("rnx")]
    [InlineData("rnsd")]
    public async Task ParserExitCodes_AgreeWithPinnedCli(string tool)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Parity7Transfer.json")));
        foreach (var expected in fixture.RootElement.GetProperty("cli").EnumerateArray().Where(c => c.GetProperty("tool").GetString() == tool))
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var args = expected.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray();
            var code = tool switch
            {
                "rncp" => await RncpApp.RunAsync(args, output, error, CancellationToken.None),
                "rnx" => await RnxApp.RunAsync(args, output, error, CancellationToken.None),
                _ => await RnsdApp.RunAsync(args, output, error, CancellationToken.None),
            };
            Assert.Equal(expected.GetProperty("exitCode").GetInt32(), code);
            if (args.FirstOrDefault() == "--version") Assert.Contains("1.5.7", output.ToString());
            else if (code != 0) Assert.NotEmpty(error.ToString());
            else Assert.Contains("Usage:", output.ToString());
        }
    }

    [Fact]
    public async Task InvalidOutputDirectoryAndExecutionPolicyFailBeforeStartingRuntime()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(3, await RncpApp.RunAsync(["-l", "-s", Path.Combine(Directory.GetCurrentDirectory(), "missing-" + Guid.NewGuid())],
            output, error, CancellationToken.None));
        Assert.Contains("Output directory not found", error.ToString());
        Assert.Equal(2, await RnxApp.RunAsync(["-l", "-n"], output, error, CancellationToken.None));
        Assert.Contains("--allow-execution", error.ToString());
        Assert.Equal(2, await RnxApp.RunAsync(["-w", "NaN", "bad", "dotnet"], output, error, CancellationToken.None));
        Assert.Equal(241, await RnxApp.RunAsync(["bad", "dotnet"], output, error, CancellationToken.None));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), CliSupport.ExpandHome("~"));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "file.bin"),
            CliSupport.ExpandHome("~/file.bin"));
        Assert.Equal("~nonexistent-parity7-user/file.bin", CliSupport.ExpandHome("~nonexistent-parity7-user/file.bin"));
    }

    [Fact]
    public void AllowedFileLinesAreExactAndMalformedHashesFailClosed()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "p7-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "allowed_identities");
        try
        {
            File.WriteAllText(file, "00000000000000000000000000000000\r\n 11111111111111111111111111111111 \nignored\n");
            Assert.Single(RncpApp.ReadAllowedFile(file));
            var lower = Path.Combine(directory, "lower");
            File.WriteAllText(lower, "11111111111111111111111111111111\n");
            Assert.Equal(new byte[16], Assert.Single(RncpApp.ReadAllowedFiles([file, lower])));
            File.WriteAllText(file, "");
            Assert.Empty(RncpApp.ReadAllowedFiles([file, lower]));
            File.Delete(file);
            Assert.Equal(Convert.FromHexString("11111111111111111111111111111111"), Assert.Single(RncpApp.ReadAllowedFiles([file, lower])));
            File.Delete(lower);
            File.WriteAllText(file, "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz\n");
            Assert.Throws<FormatException>(() => RncpApp.ReadAllowedFile(file));
            Assert.Equal(0, RncpApp.CheckOutputDirectory(directory, TextWriter.Null));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ParserHandlesAttachedValuesClustersEqualsAndTerminator()
    {
        Assert.Equal(["-v", "-q", "-w", "-1", "--stdout", "-1", "--", "-vv"],
            Rncp.TransferArguments.Expand(["-vqw-1", "--stdout=-1", "--", "-vv"], "vq", "w", new HashSet<string> { "--stdout" }, out _));
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Parity7Transfer.json")));
        var expected = fixture.RootElement.GetProperty("attachedValues");
        var args = Rncp.TransferArguments.Expand(["--stdin=-v", "--stdout=-1", "--stderr=0"], "", "",
            new HashSet<string> { "--stdin", "--stdout", "--stderr" }, out var attached);
        var index = 0;
        Assert.Equal(expected.GetProperty("stdin").GetString(), Rncp.TransferArguments.Next(args, ref index, attached));
        index++;
        Assert.Equal(expected.GetProperty("stdout").GetInt64().ToString(), Rncp.TransferArguments.Next(args, ref index, attached));
        index++;
        Assert.Equal(expected.GetProperty("stderr").GetInt64().ToString(), Rncp.TransferArguments.Next(args, ref index, attached));
        Assert.Equal(["--stdin", "-vv"], Rncp.TransferArguments.Expand(["--stdin", "-vv"], "v", "",
            new HashSet<string> { "--stdin" }, out _));
    }

    [Theory]
    [InlineData("rncp", "--save")]
    [InlineData("rncp", "-w")]
    [InlineData("rnx", "--stdin")]
    [InlineData("rnx", "--stdout")]
    public async Task OptionCannotConsumeFollowingOption(string tool, string option)
    {
        var error = new StringWriter();
        var code = tool == "rncp"
            ? await RncpApp.RunAsync([option, "--verbose"], TextWriter.Null, error, CancellationToken.None)
            : await RnxApp.RunAsync([option, "--verbose"], TextWriter.Null, error, CancellationToken.None);
        Assert.Equal(2, code);
        Assert.Contains("requires a value", error.ToString());
    }

    [Fact]
    public async Task AuthenticatedClientCancellationTerminatesCommandAndAllowsNextCommand()
    {
        var (server, client, directory) = ReticulumNodeTests.Pair();
        var marker = Path.Combine(directory, "owned-command.pid");
        try
        {
            await server.StartAsync();
            await client.StartAsync();
            using var identity = client.LoadOrCreateIdentity("rnx");
            using var listener = new RnxListener(server.Router, server.LoadOrCreateIdentity("rnx"),
                new RnxListenerOptions { AllowedIdentities = [identity.Hash] });
            await listener.AnnounceAsync();
            var remote = new RnxClient(client, identity, listener.DestinationHash);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var script = "[IO.File]::WriteAllText('" + marker.Replace("'", "''", StringComparison.Ordinal)
                + "',$PID);Start-Sleep -Seconds 60";
            var running = remote.ExecuteAsync(Command(script), TimeSpan.FromSeconds(20), cancellationToken: cancellation.Token);
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(marker) && deadline.Elapsed < TimeSpan.FromSeconds(15))
                await Task.Delay(50);
            Assert.True(File.Exists(marker), "Authenticated execution never started.");
            var pid = int.Parse(await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            var exited = false;
            try
            {
                using var owned = Process.GetProcessById(pid);
                exited = owned.HasExited || owned.WaitForExit(5000);
            }
            catch (ArgumentException) { exited = true; }
            Assert.True(exited, "Authenticated link cancellation left its command running.");
            var next = await remote.ExecuteAsync("dotnet --version", TimeSpan.FromSeconds(15));
            Assert.True(next.Ok, next.Error);
            await remote.CloseAsync();
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MalformedRemoteResultAndFetchWithoutResourceFailBoundedly()
    {
        var (server, client, directory) = ReticulumNodeTests.Pair();
        try
        {
            await server.StartAsync();
            await client.StartAsync();
            using var identity = server.LoadOrCreateIdentity("parity7-response");
            using var caller = client.LoadOrCreateIdentity("parity7-caller");
            var destination = Destination.Create(DestinationType.Single, RnxListener.AppName, [RnxListener.Aspect], identity);
            var local = new LocalDestination(identity, destination) { AcceptLinks = true };
            local.RegisterRequestHandler(RnxListener.CommandPath, _ => new object?[] { true, "not-a-code", Array.Empty<byte>(), Array.Empty<byte>(), 0L, 0L, 0.0, 1.0 }, RequestPolicy.All);
            server.Router.RegisterLocalDestination(local);
            await server.Router.AnnounceAsync(local);
            var remote = new RnxClient(client, caller, destination.Hash);
            var result = await remote.ExecuteAsync("authorized-test-no-spawn", TimeSpan.FromSeconds(10));
            Assert.Equal(247, result.ErrorCode);
            await remote.CloseAsync();

            var fetchDestination = Destination.Create(DestinationType.Single, RncpListener.AppName, [RncpListener.Aspect], identity);
            var fetchLocal = new LocalDestination(identity, fetchDestination) { AcceptLinks = true };
            fetchLocal.RegisterRequestHandler(RncpListener.FetchPath, _ => true, RequestPolicy.All);
            server.Router.RegisterLocalDestination(fetchLocal);
            await server.Router.AnnounceAsync(fetchLocal);
            var clock = Stopwatch.StartNew();
            var fetched = await RncpClient.FetchAsync(client, caller, fetchDestination.Hash, "missing.bin", directory, TimeSpan.FromSeconds(2));
            Assert.False(fetched.Success);
            Assert.Equal("The transfer failed", fetched.Message);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(12));
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ResultDownloadDeadlineOnlyBeginsWhenResourceArrivesAndLinkRemainsReusable()
    {
        var (server, client, directory) = ReticulumNodeTests.Pair();
        try
        {
            await server.StartAsync();
            await client.StartAsync();
            using var identity = server.LoadOrCreateIdentity("rnx-results");
            using var caller = client.LoadOrCreateIdentity("rnx-results-caller");
            var destination = Destination.Create(DestinationType.Single, RnxListener.AppName, [RnxListener.Aspect], identity);
            var local = new LocalDestination(identity, destination) { AcceptLinks = true };
            var calls = 0;
            local.RegisterRequestHandler(RnxListener.CommandPath, _ =>
            {
                var count = Interlocked.Increment(ref calls);
                if (count == 1) Thread.Sleep(200);
                return new object?[] { true, 0L, new byte[count == 2 ? 2 * 1024 * 1024 : 1], Array.Empty<byte>(), 1L, 0L, 0.0, 1.0 };
            }, RequestPolicy.All, autoCompress: false);
            server.Router.RegisterLocalDestination(local);
            await server.Router.AnnounceAsync(local);
            var remote = new RnxClient(client, caller, destination.Hash);
            var small = await remote.ExecuteAsync("authorized-test-no-spawn", TimeSpan.FromSeconds(10), resultTimeout: TimeSpan.Zero);
            Assert.True(small.Ok, small.Error);
            var large = await remote.ExecuteAsync("authorized-test-no-spawn", TimeSpan.FromSeconds(10), resultTimeout: TimeSpan.Zero);
            Assert.Equal(246, large.ErrorCode);
            var again = await remote.ExecuteAsync("authorized-test-no-spawn", TimeSpan.FromSeconds(10));
            Assert.True(again.Ok, again.Error);
            await remote.CloseAsync();
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ListenerConcurrencyIsBoundedAndDisposalKillsOwnedRunningCommands()
    {
        var (server, client, directory) = ReticulumNodeTests.Pair();
        RnxListener? listener = null;
        try
        {
            await server.StartAsync();
            await client.StartAsync();
            using var caller = client.LoadOrCreateIdentity("rnx-owner");
            listener = new RnxListener(server.Router, server.LoadOrCreateIdentity("rnx-owner"),
                new RnxListenerOptions { AllowedIdentities = [caller.Hash], MaxConcurrentCommands = 1 });
            await listener.AnnounceAsync();
            var firstMarker = Path.Combine(directory, "first.pid");
            var secondMarker = Path.Combine(directory, "second.pid");
            string SleepCommand(string marker) => Command("[IO.File]::WriteAllText('" +
                marker.Replace("'", "''", StringComparison.Ordinal) + "',$PID);Start-Sleep -Seconds 30");
            var firstClient = new RnxClient(client, caller, listener.DestinationHash);
            var secondClient = new RnxClient(client, caller, listener.DestinationHash);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var first = firstClient.ExecuteAsync(SleepCommand(firstMarker), TimeSpan.FromSeconds(20), cancellationToken: stop.Token);
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(firstMarker) && deadline.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(50);
            Assert.True(File.Exists(firstMarker));
            var second = secondClient.ExecuteAsync(SleepCommand(secondMarker), TimeSpan.FromSeconds(20), cancellationToken: stop.Token);
            await Task.Delay(1000);
            Assert.False(File.Exists(secondMarker), "Second process exceeded the configured concurrency bound.");
            var pid = int.Parse(await File.ReadAllTextAsync(firstMarker), System.Globalization.CultureInfo.InvariantCulture);
            listener.Dispose();
            var exited = false;
            try
            {
                using var process = Process.GetProcessById(pid);
                exited = process.HasExited || process.WaitForExit(5000);
            }
            catch (ArgumentException) { exited = true; }
            Assert.True(exited, "Listener disposal did not terminate its owned process.");
            stop.Cancel();
            foreach (var task in new[] { first, second })
            {
                try { await task; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            await firstClient.CloseAsync();
            await secondClient.CloseAsync();
        }
        finally
        {
            listener?.Dispose();
            await client.DisposeAsync();
            await server.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
