using System.Collections.Concurrent;
using System.Text;
using DotReticulum.Applications.Rnsh;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public sealed class RnshNativeTests
{
    [NativeFact]
    public async Task PublishedInitiatorRunsInsideActualTerminalAndCompletesRemoteSession()
    {
        var diagnostics = new ConcurrentQueue<string>();
        var (server, unused, directory) = ReticulumNodeTests.Pair(out var port,
            (_, message) => diagnostics.Enqueue(message));
        await unused.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var sessions = new List<Task>();
        try
        {
            using var identity = server.LoadOrCreateIdentity("rnsh-native");
            var destination = Destination.Create(DestinationType.Single, "rnsh", [], identity);
            var local = new LocalDestination(identity, destination) { AcceptLinks = true };
            local.LinkEstablished = link =>
            {
                var session = new RnshSession(link);
                sessions.Add(Task.Run(async () =>
                {
                    await using (session) await session.ServeAsync(new RnshListenerOptions
                    {
                        AllowAll = true,
                        Log = diagnostics.Enqueue,
                    }, timeout.Token);
                }));
            };
            server.Router.RegisterLocalDestination(local);
            await server.StartAsync(timeout.Token);
            var config = PyTool.WriteConfig(Path.Combine(directory, "network"), port);
            var state = Path.Combine(directory, "shell");
            var executable = Environment.GetEnvironmentVariable("DOTRETICULUM_NATIVE_EXE")!;
            var remoteCommand = OperatingSystem.IsWindows()
                ? new[] { "cmd.exe", "/c", "echo", "native-terminal-flow" }
                : new[] { "/bin/sh", "-c", "printf native-terminal-flow" };
            var argv = new[] { executable, "rnsh", "--rnsconfig", config, "--config", state, "-m", "-w", "20",
                Convert.ToHexStringLower(destination.Hash), "--" }.Concat(remoteCommand).ToArray();
            await using var terminal = RnshProcess.Start(argv, new RnshExecute
            {
                Window = new RnshWindow { Rows = 31, Columns = 97, HorizontalPixels = 0, VerticalPixels = 0 },
            }, null);
            using var output = new MemoryStream();
            var pump = terminal.Stdout.CopyToAsync(output, timeout.Token);
            await Task.Delay(500, timeout.Token);
            await server.Router.AnnounceAsync(local, cancellationToken: timeout.Token);
            var exitCode = await terminal.WaitAsync(timeout.Token);
            await pump;
            var text = Encoding.UTF8.GetString(output.ToArray());
            Assert.True(exitCode == 0,
                $"Native terminal exited {exitCode}:{Environment.NewLine}{text}{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
            Assert.Contains("native-terminal-flow", text);
            await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
        }
        finally
        {
            await timeout.CancelAsync();
            await server.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }
}
