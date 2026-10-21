using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Tests.Interop;

namespace DotReticulum.Applications.Tests;

public sealed class BlackholeLiveTests
{
    [LiveFact]
    public async Task ActualPythonPublisherAndUpdaterExchangeWithNativeEndpoints()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "Blackhole-live-" + Guid.NewGuid().ToString("N"));
        var port = ReticulumNodeTests.FreePort();
        var config = RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
            [interfaces]
              [[Server]]
                enabled = yes
                type = TCPServerInterface
                listen_ip = 127.0.0.1
                listen_port = {port}
            """);
        var native = ReticulumNode.Create(config, Path.Combine(directory, "native"));
        Process? python = null;
        var log = new StringBuilder();
        Task errorPump = Task.CompletedTask;
        try
        {
            await native.StartAsync();
            using var identity = Identity.Generate();
            var nativeStore = new BlackholeStore(Path.Combine(directory, "native-blackhole"), identity.Hash);
            nativeStore.BlackholeIdentity(Enumerable.Repeat((byte)0x88, 16).ToArray(), reason: "native publication");
            var service = new BlackholeService(nativeStore, new BlackholeOptions { Publish = true }, (_, _) => throw new NotSupportedException());
            var publication = service.RegisterPublisher(identity, native.Router.RegisterLocalDestination)!;
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { FindAdapter(), "--config", Path.Combine(directory, "python"), "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--source-identity", identity.HexHash })
                start.ArgumentList.Add(argument);
            start.Environment["PYTHONPATH"] = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")
                ?? throw new InvalidOperationException("DOTRETICULUM_UPSTREAM is required for live reference tests.");
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            python = Process.Start(start) ?? throw new InvalidOperationException("Python process did not start.");
            errorPump = Task.Run(async () =>
            {
                while (await python.StandardError.ReadLineAsync() is { } line) lock (log) log.AppendLine(line);
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            async Task<JsonElement> Event(string expected)
            {
                while (await python.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                {
                    lock (log) log.AppendLine(line);
                    if (!line.StartsWith("@@", StringComparison.Ordinal)) continue;
                    using var json = JsonDocument.Parse(line[2..]);
                    if (json.RootElement.GetProperty("event").GetString() == expected) return json.RootElement.Clone();
                }

                throw new InvalidOperationException($"Python exited before {expected}: {log}");
            }

            var ready = await Event("ready");
            var remoteHash = Convert.FromHexString(ready.GetProperty("identity").GetString()!);
            await python.StandardInput.WriteLineAsync("announce");
            await python.StandardInput.FlushAsync();
            await Event("announced");
            var received = new BlackholeStore(Path.Combine(directory, "received"), identity.Hash, [remoteHash]);
            var updater = new BlackholeService(received, new BlackholeOptions { Sources = [remoteHash] }, BlackholeService.NativeFetcher(native));
            var updated = await updater.UpdateSourceAsync(remoteHash, deadline.Token);
            Assert.True(updated.Succeeded, updated.Error + "\n" + log);
            Assert.True(received.IsBlackholed(Enumerable.Repeat((byte)0x77, 16).ToArray()));
            Assert.Equal("python publication", Assert.Single(received.Snapshot()).Reason);
            await native.Router.AnnounceAsync(publication, cancellationToken: deadline.Token);
            await python.StandardInput.WriteLineAsync($"fetch {identity.HexHash}");
            await python.StandardInput.FlushAsync();
            var fetched = await Event("updated");
            var response = Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(Convert.FromHexString(fetched.GetProperty("packed").GetString()!)));
            var entry = Assert.IsType<Dictionary<object, object?>>(response[new string('8', 32)]);
            Assert.Equal("native publication", entry["reason"]);
            Assert.Equal(identity.Hash, Assert.IsType<byte[]>(entry["source"]));
        }
        finally
        {
            if (python is not null)
            {
                if (!python.HasExited)
                {
                    try { await python.StandardInput.WriteLineAsync("quit"); await python.StandardInput.FlushAsync(); }
                    catch (IOException) { }
                    try { await python.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException) { python.Kill(entireProcessTree: true); await python.WaitForExitAsync(); }
                }

                await errorPump.WaitAsync(TimeSpan.FromSeconds(5));
                python.Dispose();
            }

            await native.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindAdapter()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "tests", "DotReticulum.Applications.Tests", "BlackholeReference.py");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("BlackholeReference.py was not found.");
    }
}
