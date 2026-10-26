using System.Diagnostics;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rnsh;

/// <summary>Owns the child and every redirected endpoint through bounded teardown.</summary>
public sealed class RnshProcess : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly UnixRnshTerminal? _terminal;
    private readonly WindowsRnshTerminal? _windows;
    private bool _inputClosed;
    private long _exitCode = long.MinValue;
    private RnshProcess(Process process)
    {
        _process = process;
        Stdin = process.StandardInput.BaseStream;
        Stdout = process.StandardOutput.BaseStream;
        Stderr = process.StandardError.BaseStream;
    }
    private RnshProcess(UnixRnshTerminal terminal)
    {
        _terminal = terminal;
        Stdin = terminal.Input;
        Stdout = terminal.Output;
        Stderr = terminal.Error;
    }
    private RnshProcess(WindowsRnshTerminal terminal)
    {
        _windows = terminal;
        Stdin = terminal.Input; Stdout = terminal.Output; Stderr = terminal.Error;
    }
    public Stream Stdin { get; }
    public Stream Stdout { get; }
    public Stream Stderr { get; }

    public static RnshProcess Start(string[] argv, RnshExecute command, Identity? remote)
    {
        if (argv.Length == 0 || argv.Any(v => v.Contains('\0'))) throw new ArgumentException("Invalid command argv.");
        var identity = remote is null ? "" : "<" + remote.HexHash + ">";
        if (OperatingSystem.IsWindows()) return new RnshProcess(WindowsRnshTerminal.Start(argv, command, identity));
        if (OperatingSystem.IsLinux() || !command.PipeStdin || !command.PipeStdout || !command.PipeStderr)
            return new RnshProcess(UnixRnshTerminal.Start(argv, command, identity));
        var start = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in argv.Skip(1)) start.ArgumentList.Add(arg);
        start.Environment["TERM"] = command.Terminal ?? Environment.GetEnvironmentVariable("TERM") ?? "xterm";
        start.Environment["RNS_REMOTE_IDENTITY"] = identity;
        return new RnshProcess(Process.Start(start) ?? throw new IOException("Unable to start process"));
    }

    public void Resize(RnshWindow size) { _terminal?.Resize(size); _windows?.Resize(size); }
    public void CloseInput()
    {
        if (_inputClosed) return;
        _inputClosed = true;
        if (_windows is not null) _windows.CloseInput();
        else if (_terminal is not null) _terminal.CloseInput();
        else _process!.StandardInput.Close();
    }
    public async Task<int> WaitAsync(CancellationToken token)
    {
        if (_terminal is not null) return await _terminal.WaitAsync(token).ConfigureAwait(false);
        if (_windows is not null) return await _windows.WaitAsync(token).ConfigureAwait(false);
        await _process!.WaitForExitAsync(token).ConfigureAwait(false);
        try { return _process.ExitCode; }
        catch (InvalidOperationException) when (Volatile.Read(ref _exitCode) != long.MinValue) { return (int)Volatile.Read(ref _exitCode); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_terminal is not null) { await _terminal.DisposeAsync().ConfigureAwait(false); return; }
        if (_windows is not null) { await _windows.DisposeAsync().ConfigureAwait(false); return; }
        try
        {
            if (!_process!.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            // A pending WaitAsync may resume after disposal, when Process.ExitCode is no longer readable.
            Volatile.Write(ref _exitCode, _process.ExitCode);
        }
        catch (InvalidOperationException) { }
        finally { _process!.Dispose(); }
    }
}
