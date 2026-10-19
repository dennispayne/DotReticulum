using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DotReticulum.Applications.Rncp;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Tests;

public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_NATIVE_EXE")))
            Skip = "Set DOTRETICULUM_NATIVE_EXE to a published NativeAOT Applications executable.";
    }
}

public sealed class NativeResourceTests
{
    internal sealed class NativePeer : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _log = new();
        public string Output { get { lock (_log) return _log.ToString(); } }

        public NativePeer(params string[] args) : this(null, args) { }

        public NativePeer(IReadOnlyDictionary<string, string>? environment, params string[] args)
        {
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_NATIVE_EXE")!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in args) info.ArgumentList.Add(argument);
            if (environment is not null)
                foreach (var (key, value) in environment) info.Environment[key] = value;
            _process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch native peer.");
            _process.OutputDataReceived += (_, e) => Append(e.Data);
            _process.ErrorDataReceived += (_, e) => Append(e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        private void Append(string? line)
        {
            if (line is null) return;
            lock (_log)
            {
                _log.AppendLine(line);
                if (_log.Length > 65536) _log.Remove(0, _log.Length - 65536);
            }
        }

        public async Task WaitForSuccessAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken);
            Assert.True(_process.ExitCode == 0, Output);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
            _process.Dispose();
        }
    }

    [NativeFact]
    public async Task NativeListener_DecryptsDecompressesAndReturnsCompressedMultiMegabyteResource()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var (node, unused, directory) = ReticulumNodeTests.Pair(out var port);
        var save = Directory.CreateDirectory(Path.Combine(directory, "received")).FullName;
        var config = PyTool.WriteConfig(Path.Combine(directory, "native"), port);
        var peerIdentity = node.LoadOrCreateIdentity("native_resource_peer");
        NativePeer? listener = null;
        try
        {
            string destination;
            using (var print = new NativePeer("rncp", "--config", config, "-p"))
            {
                await print.WaitForSuccessAsync(timeout.Token);
                var match = Regex.Match(print.Output, @"Listening on\s*:\s*<([0-9a-f]{32})>");
                Assert.True(match.Success, print.Output);
                destination = match.Groups[1].Value;
            }

            await node.StartAsync(timeout.Token);
            listener = new NativePeer("rncp", "--config", config, "-l", "-a", peerIdentity.HexHash,
                "-s", save, "-F", "-j", save, "-b", "2", "-v");
            var connection = await ClientLinks.ConnectAsync(node, Convert.FromHexString(destination), TimeSpan.FromSeconds(30), timeout.Token);
            Assert.True(connection.Ok, connection.Error + "\n" + listener.Output);
            var link = connection.Link!;
            try
            {
                link.Identify(peerIdentity);
                var payload = new byte[2_200_017];
                for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 31);
                var sent = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
                var resource = link.SendResource(payload,
                    new Dictionary<object, object?> { ["name"] = "native-roundtrip.bin"u8.ToArray() },
                    autoCompress: true, concluded: r => sent.TrySetResult(r));
                Assert.True(resource.Compressed);
                Assert.Equal(ResourceStatus.Complete, (await sent.Task.WaitAsync(timeout.Token)).Status);

                var path = Path.Combine(save, "native-roundtrip.bin");
                while (!File.Exists(path) || new FileInfo(path).Length != payload.Length)
                    await Task.Delay(20, timeout.Token);
                Assert.Equal(payload, await File.ReadAllBytesAsync(path, timeout.Token));

                var returned = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
                link.ResourceStrategy = ResourceStrategy.AcceptAll;
                link.ResourceReceived = r => returned.TrySetResult(r);
                var fetch = link.Request(RncpListener.FetchPath, "native-roundtrip.bin", timeout: 30);
                Assert.NotNull(fetch);
                Assert.Equal(true, await fetch.ResponseTask.WaitAsync(timeout.Token));
                var received = await returned.Task.WaitAsync(timeout.Token);
                Assert.Equal(ResourceStatus.Complete, received.Status);
                Assert.True(received.Compressed, "Native peer did not send a compressed resource.");
                Assert.Equal(payload, received.Data);
                Assert.DoesNotContain("Resource compression failed", listener.Output);
            }
            finally { link.Teardown(); }
        }
        finally
        {
            listener?.Dispose();
            await node.DisposeAsync();
            await unused.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
