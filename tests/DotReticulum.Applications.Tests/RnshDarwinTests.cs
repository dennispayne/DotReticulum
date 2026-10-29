using System.Runtime.InteropServices;
using DotReticulum.Applications.Rnsh;

namespace DotReticulum.Applications.Tests;

public sealed class RnshDarwinFactAttribute : FactAttribute
{
    public RnshDarwinFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires the actual Darwin PTY, session and termios ABI.";
    }
}

[Trait("Category", "Integration")]
public sealed class RnshDarwinTests
{
    [RnshDarwinFact]
    public async Task ExitedChildTerminalOutputRemainsReadableBeforeTheFirstRead()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var stdoutPipe in new[] { false, true })
        {
            await using var child = RnshProcess.Start(["/bin/sh", "-c",
                "printf stdout-marker; printf stderr-marker >&2"], new RnshExecute
                {
                    PipeStdin = true, PipeStdout = stdoutPipe, PipeStderr = false,
                }, null);
            Assert.Equal(0, await child.WaitAsync(timeout.Token));
            using var output = new StreamReader(child.Stdout, leaveOpen: true);
            using var error = new StreamReader(child.Stderr, leaveOpen: true);
            var stdout = await output.ReadToEndAsync(timeout.Token);
            var stderr = await error.ReadToEndAsync(timeout.Token);
            Assert.Contains("stdout-marker", stdout);
            Assert.Contains("stderr-marker", stdoutPipe ? stderr : stdout);
            if (stdoutPipe) Assert.DoesNotContain("stderr-marker", stdout);
        }
    }

    [RnshDarwinFact]
    public async Task ControllingTerminalResizesAndExecKeepsArgumentsLiteral()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        const string literal = "spaces ' \" $(printf must-not-run); *";
        const string script = "printf 'initial:'; stty -f /dev/tty size; " +
            "IFS= read -r line; printf 'resized:'; stty -f /dev/tty size; printf 'argument:%s\\n' \"$1\"";
        await using var child = RnshProcess.Start(["/bin/sh", "-c", script, "rnsh-test", literal], new RnshExecute
        {
            Window = new RnshWindow { Rows = 31, Columns = 97, HorizontalPixels = 0, VerticalPixels = 0 },
        }, null);
        using var reader = new StreamReader(child.Stdout, leaveOpen: true);
        Assert.Equal("initial:31 97", await reader.ReadLineAsync(timeout.Token));
        child.Resize(new RnshWindow { Rows = 42, Columns = 113, HorizontalPixels = 800, VerticalPixels = 600 });
        await child.Stdin.WriteAsync("continue\n"u8.ToArray(), timeout.Token);
        await child.Stdin.FlushAsync(timeout.Token);
        var remainder = await reader.ReadToEndAsync(timeout.Token);
        Assert.Equal(0, await child.WaitAsync(timeout.Token));
        Assert.Contains("resized:42 113", remainder);
        Assert.Contains("argument:" + literal, remainder);
    }

    [RnshDarwinFact]
    public async Task TerminalInterruptReachesForegroundChildAndNextSessionStillStarts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var child = RnshProcess.Start(["/bin/sh", "-c",
            "trap 'printf interrupt-received; exit 0' INT; printf 'ready\\n'; while :; do read line; done"],
            new RnshExecute(), null))
        {
            using var reader = new StreamReader(child.Stdout, leaveOpen: true);
            Assert.Equal("ready", await reader.ReadLineAsync(timeout.Token));
            await child.Stdin.WriteAsync(new byte[] { 3 }, timeout.Token);
            await child.Stdin.FlushAsync(timeout.Token);
            Assert.Contains("interrupt-received", await reader.ReadToEndAsync(timeout.Token));
            Assert.Equal(0, await child.WaitAsync(timeout.Token));
        }
        await using var next = RnshProcess.Start(["/bin/sh", "-c", "printf recovered"], new RnshExecute(), null);
        using var nextReader = new StreamReader(next.Stdout, leaveOpen: true);
        Assert.Equal("recovered", await nextReader.ReadToEndAsync(timeout.Token));
        Assert.Equal(0, await next.WaitAsync(timeout.Token));
    }

    [RnshDarwinFact]
    public async Task CancelledWaitAndDisposalTerminateOwnedTerminalProcessGroup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var child = RnshProcess.Start(["/bin/sh", "-c", "sleep 120 & printf '%s\\n' \"$!\"; wait"],
            new RnshExecute(), null);
        using var reader = new StreamReader(child.Stdout, leaveOpen: true);
        var descendant = int.Parse((await reader.ReadLineAsync(timeout.Token))!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(descendant > 0);
        Assert.Equal(0, kill(descendant, 0));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child.WaitAsync(cancelled.Token));
        var pending = child.WaitAsync(timeout.Token);
        await child.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8), timeout.Token);
        await pending;
        using var reaped = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (kill(descendant, 0) == 0) await Task.Delay(20, reaped.Token);
        Assert.Equal(3, Marshal.GetLastPInvokeError()); // Darwin ESRCH, not an unrelated permission failure.
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
