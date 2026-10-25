using System.Diagnostics;
using System.Text.Json;
using DotReticulum.Applications.Rnsh;
using DotReticulum.Core;
using DotReticulum.Transport;
using Xunit.Abstractions;

namespace DotReticulum.Applications.Tests;

public sealed class RnshLiveTests(ITestOutputHelper log)
{
    [LiveFact]
    public async Task ActualPinnedPythonListenerAndInitiatorExchangeBinaryStdinOutputAndRecoverNewSessions()
    {
        var (server, unused, dir) = ReticulumNodeTests.Pair(out var port);
        await unused.DisposeAsync();
        await using var bridge = new Parity4TransportLiveTests.ImpairedBridge(port, bitrate: 1_000_000, latencyMilliseconds: 20);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        Process? python = null;
        Task<string>? stderr = null;
        Task peerPump = Task.CompletedTask;
        var events = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        var peerOutput = new System.Text.StringBuilder();
        JsonElement? latestSessions = null;
        try
        {
            using var identity = server.LoadOrCreateIdentity("rnsh");
            var destination = Destination.Create(DestinationType.Single, "rnsh", [], identity);
            var sessions = new List<Task>();
            var local = new LocalDestination(identity, destination) { AcceptLinks = true };
            local.LinkEstablished = link =>
            {
                var session = new RnshSession(link);
                sessions.Add(Task.Run(async () =>
                {
                    await using (session) await session.ServeAsync(new RnshListenerOptions { AllowAll = true }, timeout.Token);
                }));
            };
            server.Router.RegisterLocalDestination(local);
            await server.StartAsync(timeout.Token);
            bridge.Start();
            var harness = FindHarness();
            var config = Path.Combine(dir, "python-rnsh");
            var wslPython = Environment.GetEnvironmentVariable("DOTRETICULUM_RNSH_WSL_PYTHON");
            var upstream = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM") ?? throw new InvalidOperationException("Missing upstream.");
            var start = new ProcessStartInfo(wslPython is null ? Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python" : "wsl.exe")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            static string LinuxPath(string path) => "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
            if (wslPython is not null)
            {
                var linuxUpstream = Environment.GetEnvironmentVariable("DOTRETICULUM_RNSH_WSL_UPSTREAM")
                    ?? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(wslPython)))!.Replace('\\', '/') + "/.reference";
                foreach (var arg in new[] { "-d", "Ubuntu", "--exec", "env", "DOTRETICULUM_UPSTREAM=" + linuxUpstream,
                    wslPython, "-u", LinuxPath(harness), "--config", LinuxPath(config), "--port", bridge.Port.ToString() }) start.ArgumentList.Add(arg);
            }
            else
            {
                foreach (var arg in new[] { "-u", harness, "--config", config, "--port", bridge.Port.ToString() }) start.ArgumentList.Add(arg);
                start.Environment["DOTRETICULUM_UPSTREAM"] = upstream;
                start.Environment["PYTHONPATH"] = upstream;
            }
            python = Process.Start(start)!;
            stderr = python.StandardError.ReadToEndAsync();
            peerPump = Task.Run(async () =>
            {
                try
                {
                    while (await python.StandardOutput.ReadLineAsync() is { } line)
                    {
                        lock (peerOutput) peerOutput.AppendLine(line);
                        if (!line.StartsWith("@@")) continue;
                        var item = JsonDocument.Parse(line[2..]).RootElement.Clone();
                        if (item.GetProperty("event").GetString() == "sessions") latestSessions = item;
                        else await events.Writer.WriteAsync(item);
                    }
                    events.Writer.TryComplete();
                }
                catch (Exception ex) { events.Writer.TryComplete(ex); }
            });
            async Task<JsonElement> Event(string name)
            {
                while (true)
                {
                    var item = await events.Reader.ReadAsync(timeout.Token);
                    if (item.GetProperty("event").GetString() == name) return item;
                    if (item.GetProperty("event").GetString() == "error") throw new IOException(item.ToString());
                }
            }
            var ready = await Event("ready");
            Assert.Equal("1.5.7", ready.GetProperty("version").GetString());
            var remote = Convert.FromHexString(ready.GetProperty("dest").GetString()!);
            var input = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var connected = await ClientLinks.ConnectAsync(server, remote, TimeSpan.FromSeconds(25), timeout.Token);
                Assert.NotNull(connected.Link);
                await using var session = new RnshSession(connected.Link!);
                connected.Link!.Identify(identity);
                if (attempt == 0)
                {
                    bridge.DropNextChannel(fromPython: false);
                    bridge.DropNextChannel(fromPython: true);
                }
                using var output = new MemoryStream();
                using var error = new MemoryStream();
                var code = await session.InitiateAsync(new RnshExecute
                {
                    Command = ["/usr/bin/python3", "-c", "import os; d=b''\nwhile len(d)<256: d+=os.read(0,256-len(d))\nos.write(1,d); os.write(2,b'err')"],
                    PipeStdin = true, PipeStdout = true, PipeStderr = true,
                }, new HeldInput(input), output, error, TimeSpan.FromSeconds(15), timeout.Token);
                Assert.Equal(0, code);
                Assert.Equal(input, output.ToArray());
                Assert.Equal("err"u8.ToArray(), error.ToArray());
            }
            Assert.True(bridge.Dropped >= 2, "Expected both directions to execute Channel retry under loss.");
            {
                var connected = await ClientLinks.ConnectAsync(server, remote, TimeSpan.FromSeconds(25), timeout.Token);
                Assert.NotNull(connected.Link);
                await using var session = new RnshSession(connected.Link!);
                connected.Link!.Identify(identity);
                using var eofDeadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                eofDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InitiateAsync(new RnshExecute
                {
                    Command = ["/usr/bin/python3", "-c", "import os; os.read(0,1)"],
                    PipeStdin = true, PipeStdout = true, PipeStderr = true,
                }, Stream.Null, Stream.Null, Stream.Null, TimeSpan.FromSeconds(5), eofDeadline.Token));
                Assert.NotNull(latestSessions);
                var stalled = latestSessions.Value.GetProperty("sessions").EnumerateArray().Last();
                Assert.Equal("LSSTATE_RUNNING", stalled.GetProperty("state").GetString());
                Assert.Equal(JsonValueKind.Null, stalled.GetProperty("return_code").ValueKind);
                log.WriteLine("Unmodified pinned EOF defect: RUNNING without exit callback; bounded cancellation then fresh link.");
            }
            {
                var connected = await ClientLinks.ConnectAsync(server, remote, TimeSpan.FromSeconds(25), timeout.Token);
                Assert.NotNull(connected.Link);
                await using var session = new RnshSession(connected.Link!);
                connected.Link!.Identify(identity);
                using var output = new MemoryStream();
                var started = Environment.TickCount64;
                RnshWindow Size() => Environment.TickCount64 - started < 600
                    ? new RnshWindow { Rows = 31, Columns = 97, HorizontalPixels = 0, VerticalPixels = 0 }
                    : new RnshWindow { Rows = 45, Columns = 120, HorizontalPixels = 0, VerticalPixels = 0 };
                var code = await session.InitiateAsync(new RnshExecute
                {
                    Command = ["/bin/sh", "-c", "stty size; sleep 3; stty size"],
                    Window = Size(), Terminal = "xterm",
                }, new WaitingInput(), output, Stream.Null, TimeSpan.FromSeconds(15), timeout.Token, window: Size);
                Assert.Equal(0, code);
                var text = System.Text.Encoding.UTF8.GetString(output.ToArray());
                Assert.Contains("31 97", text);
                Assert.Contains("45 120", text);
            }
            await server.Router.AnnounceAsync(local, cancellationToken: timeout.Token);
            await python.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                op = "execute", destination = Convert.ToHexStringLower(destination.Hash),
                argv = new[] { "dotreticulum-rnsh-missing-executable" }, input = "",
            }));
            await python.StandardInput.FlushAsync(timeout.Token);
            var failed = await Event("error");
            Assert.Contains(OperatingSystem.IsWindows() ? "CreateProcess" : "No such file or directory",
                failed.GetProperty("message").GetString()!);
            var cancellationMarker = Path.Combine(dir, "cancelled-child-marker");
            var cancellationCommand = OperatingSystem.IsWindows()
                ? new[] { "powershell.exe", "-NoProfile", "-Command", $"Start-Sleep -Seconds 4; [IO.File]::WriteAllText('{cancellationMarker.Replace("'", "''")}', 'orphan')" }
                : new[] { "/bin/sh", "-c", $"sleep 4; printf orphan > '{cancellationMarker.Replace("'", "'\\''")}'" };
            await python.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                op = "execute", destination = Convert.ToHexStringLower(destination.Hash), argv = cancellationCommand, cancel = true,
            }));
            await python.StandardInput.FlushAsync(timeout.Token);
            await Event("cancelled");
            var argv = OperatingSystem.IsWindows()
                ? new[] { "powershell.exe", "-NoProfile", "-Command", "$s=[Console]::OpenStandardInput();$o=[Console]::OpenStandardOutput();$s.CopyTo($o);[Console]::Error.Write('err');exit 7" }
                : new[] { "/bin/sh", "-c", "cat; printf err >&2; exit 7" };
            await python.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { op = "execute", destination = Convert.ToHexStringLower(destination.Hash), argv, input = Convert.ToHexStringLower(input) }));
            await python.StandardInput.FlushAsync(timeout.Token);
            var result = await Event("result");
            Assert.Equal(input, Convert.FromHexString(result.GetProperty("stdout").GetString()!));
            Assert.Equal("err"u8.ToArray(), Convert.FromHexString(result.GetProperty("stderr").GetString()!));
            Assert.Equal(OperatingSystem.IsWindows() ? 7 : 0, result.GetProperty("code").GetInt32());
            await Task.Delay(4500, timeout.Token);
            Assert.False(File.Exists(cancellationMarker), "Cancelled rnsh session left a child running.");
            await python.StandardInput.WriteLineAsync("{\"op\":\"quit\"}");
            await python.StandardInput.FlushAsync(timeout.Token);
            await python.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, python.ExitCode);
            await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
        }
        finally
        {
            await timeout.CancelAsync();
            if (python is not null)
            {
                if (!python.HasExited) { python.Kill(true); await python.WaitForExitAsync(); }
                python.Dispose();
            }
            log.WriteLine(peerOutput.ToString());
            await peerPump.WaitAsync(TimeSpan.FromSeconds(5));
            if (stderr is not null)
            {
                try { log.WriteLine(await stderr); }
                catch (OperationCanceledException) { }
            }
            await server.DisposeAsync();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static string FindHarness()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Interop", "live_rnsh_node.py");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("live_rnsh_node.py");
    }

    private sealed class WaitingInput : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HeldInput(byte[] data) : Stream
    {
        private int _offset;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => data.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < data.Length)
            {
                var count = Math.Min(buffer.Length, data.Length - _offset);
                data.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
