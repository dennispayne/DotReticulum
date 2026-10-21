using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DotReticulum.Applications.Rncp;
using DotReticulum.Applications.Rnx;

namespace DotReticulum.Applications.Tests;

/// <summary>An upstream Python utility (<c>python -m RNS.Utilities.rncp</c> / <c>rnx</c>) run against a .NET TCP server node.</summary>
internal sealed class PyTool : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    private PyTool(Process process)
    {
        _process = process;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public string Output { get { lock (_output) return _output.ToString(); } }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public static string PythonExe => Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python";

    /// <summary>Writes a Python RNS config dir whose only interface is a TCP client to the .NET server on <paramref name="port"/>.</summary>
    public static string WriteConfig(string dir, int port)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config"), $"""
            [reticulum]
              enable_transport = False
              share_instance = No
            [logging]
              loglevel = 4
            [interfaces]
              [[To .NET]]
                type = TCPClientInterface
                enabled = yes
                target_host = 127.0.0.1
                target_port = {port}
            """);
        return dir;
    }

    public static PyTool Start(string module, params string[] args) => StartIn(null, module, args);

    /// <summary>Like <see cref="Start"/> with a working directory (upstream rncp's -s/-j checks compare against "/" and fail on Windows paths, so tests save into the cwd).</summary>
    public static PyTool StartIn(string? workingDirectory, string module, params string[] args)
    {
        var psi = new ProcessStartInfo(PythonExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
        };
        foreach (var a in new[] { "-u", FindShim(), module }.Concat(args)) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONPATH"] = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")
            ?? throw new InvalidOperationException("DOTRETICULUM_UPSTREAM must point at the pinned upstream checkout.");
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        return new PyTool(Process.Start(psi) ?? throw new InvalidOperationException("Could not start python."));
    }

    /// <summary>Runs <c>module -p</c> and returns the printed identity and destination hashes.</summary>
    public static (string Identity, string Destination) PrintIdentity(string module, string configDir)
    {
        using var tool = Start(module, "--config", configDir, "-p");
        Assert.True(tool.WaitForExit(TimeSpan.FromSeconds(60)), tool.Output);
        var identity = Regex.Match(tool.Output, @"Identity\s*:\s*<([0-9a-f]{32})>");
        var destination = Regex.Match(tool.Output, @"Listening on\s*:\s*<([0-9a-f]{32})>");
        Assert.True(identity.Success && destination.Success, tool.Output);
        return (identity.Groups[1].Value, destination.Groups[1].Value);
    }

    private static string FindShim()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Interop", "rncp_win_shim.py");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("tests/Interop/rncp_win_shim.py not found.");
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        if (!_process.WaitForExit(timeout)) return false;
        _process.WaitForExit();
        return true;
    }

    public void Dispose()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        _process.Dispose();
    }
}

// LIVE: .NET rncp / rnx <-> real upstream Python rncp / rnx. Opt-in: DOTRETICULUM_LIVE=1 (+ DOTRETICULUM_PYTHON, DOTRETICULUM_UPSTREAM):
//   dotnet test tests/DotReticulum.Applications.Tests -c Release --filter "FullyQualifiedName~LiveRncpRnx"
public sealed class LiveRncpRnxTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private static byte[] RandomFile(string path, int size)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(path, data);
        return data;
    }

    private static async Task<byte[]> WaitForFileAsync(string path, int size, PyTool? tool = null)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var data = File.ReadAllBytes(path);
                if (data.Length == size) return data;
            }
            catch (IOException)
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("File did not arrive: " + path + "\n" + string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(path)!).Select(f => Path.GetFileName(f) + "=" + new FileInfo(f).Length)) + "\n" + tool?.Output);
    }

    [LiveFact]
    public async Task DotNetRncp_SendsAndFetches_FromPythonListener()
    {
        var (server, _, dir) = ReticulumNodeTests.Pair(out var port);
        try
        {
            var identity = server.LoadOrCreateIdentity("rncp");
            var cfg = PyTool.WriteConfig(Path.Combine(dir, "py"), port);
            var (_, pyDest) = PyTool.PrintIdentity("rncp", cfg);
            var save = Directory.CreateDirectory(Path.Combine(dir, "pysave")).FullName;
            var jail = Directory.CreateDirectory(Path.Combine(dir, "pyfiles")).FullName;
            var fetchable = RandomFile(Path.Combine(jail, "fetchme.bin"), 3_000_037);
            await server.StartAsync();

            using var listener = PyTool.StartIn(save, "rncp", "--config", cfg, "-l", "-a", identity.HexHash, "-F", "-b", "3");
            var destination = Convert.FromHexString(pyDest);

            var upload = Path.Combine(dir, "upload.bin");
            var data = RandomFile(upload, 5_000_011);
            var sent = await RncpClient.SendAsync(server, identity, destination, upload, Patience, true, null, CancellationToken.None);
            Assert.True(sent.Success, sent.Message + "\n" + listener.Output);
            Assert.Equal(data, await WaitForFileAsync(Path.Combine(save, "upload.bin"), data.Length, listener));

            var fetchDir = Directory.CreateDirectory(Path.Combine(dir, "fetched")).FullName;
            var fetched = await RncpClient.FetchAsync(server, identity, destination, Path.Combine(jail, "fetchme.bin"), fetchDir, Patience, false, null, CancellationToken.None);
            Assert.True(fetched.Success, fetched.Message + "\n" + listener.Output);
            Assert.Equal(fetchable, File.ReadAllBytes(Path.Combine(fetchDir, "fetchme.bin")));
        }
        finally
        {
            await server.DisposeAsync();
            Cleanup(dir);
        }
    }

    [LiveFact]
    public async Task PythonRncp_SendsAndFetches_FromDotNetListener()
    {
        var (server, _, dir) = ReticulumNodeTests.Pair(out var port);
        try
        {
            var cfg = PyTool.WriteConfig(Path.Combine(dir, "py"), port);
            var (pyIdentity, _) = PyTool.PrintIdentity("rncp", cfg);
            var identity = server.LoadOrCreateIdentity("rncp");
            var save = Directory.CreateDirectory(Path.Combine(dir, "netsave")).FullName;
            var jail = Directory.CreateDirectory(Path.Combine(dir, "netjail")).FullName;
            var fetchable = RandomFile(Path.Combine(jail, "fetchme.bin"), 2_500_003);
            var listener = new RncpListener(server.Router, identity, new RncpListenerOptions
            {
                AllowedIdentities = [Convert.FromHexString(pyIdentity)],
                AllowFetch = true,
                FetchJail = jail,
                SaveDirectory = save,
            });
            await server.StartAsync();

            var upload = Path.Combine(dir, "pyupload.bin");
            var data = RandomFile(upload, 4_000_019);
            using (var send = PyTool.Start("rncp", "--config", cfg, upload, listener.DestinationHashHex, "-w", "60"))
            {
                Assert.True(send.WaitForExit(Patience), send.Output);
                Assert.Equal(0, send.ExitCode);
            }

            Assert.Equal(data, await WaitForFileAsync(Path.Combine(save, "pyupload.bin"), data.Length));

            var fetchDir = Directory.CreateDirectory(Path.Combine(dir, "pyfetched")).FullName;
            using (var fetch = PyTool.StartIn(fetchDir, "rncp", "--config", cfg, "-f", "fetchme.bin", listener.DestinationHashHex, "-w", "60"))
            {
                Assert.True(fetch.WaitForExit(Patience), fetch.Output);
                Assert.Equal(fetchable, await WaitForFileAsync(Path.Combine(fetchDir, "fetchme.bin"), fetchable.Length, fetch));
            }
        }
        finally
        {
            await server.DisposeAsync();
            Cleanup(dir);
        }
    }

    [LiveFact]
    public async Task DotNetRnx_RunsCommand_OnPythonListener()
    {
        var (server, _, dir) = ReticulumNodeTests.Pair(out var port);
        try
        {
            var identity = server.LoadOrCreateIdentity("rnx");
            var cfg = PyTool.WriteConfig(Path.Combine(dir, "py"), port);
            var (_, pyDest) = PyTool.PrintIdentity("rnx", cfg);
            await server.StartAsync();
            using var listener = PyTool.Start("rnx", "--config", cfg, "-l", "-a", identity.HexHash);

            var client = new RnxClient(server, identity, Convert.FromHexString(pyDest));
            var python = PyTool.PythonExe.Replace('\\', '/');
            var result = await client.ExecuteAsync($"\"{python}\" -c \"print(12345*3)\"", TimeSpan.FromSeconds(60), resultTimeout: TimeSpan.FromSeconds(30));
            Assert.True(result.Ok, result.Error + "\n" + listener.Output);
            Assert.Equal(0L, result.ReturnCode);
            Assert.Equal("37035", Encoding.UTF8.GetString(result.Stdout).Trim());

            var withInput = await client.ExecuteAsync($"\"{python}\" -c \"import sys; sys.stdout.write(sys.stdin.read().upper())\"", TimeSpan.FromSeconds(30), stdin: "hello rnx"u8.ToArray());
            Assert.True(withInput.Ok, withInput.Error);
            Assert.Equal("HELLO RNX", Encoding.UTF8.GetString(withInput.Stdout));
            await client.CloseAsync();
        }
        finally
        {
            await server.DisposeAsync();
            Cleanup(dir);
        }
    }

    [LiveFact]
    public async Task PythonRnx_RunsCommand_OnDotNetListener()
    {
        var (server, _, dir) = ReticulumNodeTests.Pair(out var port);
        try
        {
            var cfg = PyTool.WriteConfig(Path.Combine(dir, "py"), port);
            var (pyIdentity, _) = PyTool.PrintIdentity("rnx", cfg);
            var identity = server.LoadOrCreateIdentity("rnx");
            var listener = new RnxListener(server.Router, identity, new RnxListenerOptions { AllowedIdentities = [Convert.FromHexString(pyIdentity)] });
            await server.StartAsync();

            using var client = PyTool.Start("rnx", "--config", cfg, listener.DestinationHashHex, "dotnet --version", "-w", "60", "-m");
            Assert.True(client.WaitForExit(Patience), client.Output);
            Assert.Equal(0, client.ExitCode);
            Assert.Matches(@"(?m)^\d+\.\d+\.\d+", client.Output);
        }
        finally
        {
            await server.DisposeAsync();
            Cleanup(dir);
        }
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }
}
