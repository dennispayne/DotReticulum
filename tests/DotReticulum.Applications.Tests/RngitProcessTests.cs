using System.Text;
using System.Diagnostics;
using DotReticulum.Applications.Rngit;

namespace DotReticulum.Applications.Tests;

public sealed class RngitProcessTests : IDisposable
{
    private readonly PresentationProcessFixture fixture = new();

    [Fact]
    public async Task BinaryInputOutputAndArgumentsAreNotShellInterpreted()
    {
        var input = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var result = await fixture.RunAsync("echo", input: input);
        Assert.Equal(0, result.Code);
        Assert.Equal(input, result.Output);
        var literal = "a space; $(danger) & | ' \" π";
        result = await fixture.RunAsync("argument", extra: [literal]);
        Assert.Equal(0, result.Code);
        Assert.Equal(literal, Encoding.UTF8.GetString(result.Output));
    }

    [Fact]
    public async Task SimultaneousStdoutAndStderrDoNotDeadlock()
    {
        var result = await fixture.RunAsync("both", maxOutputBytes: 512 * 1024);
        Assert.Equal(7, result.Code);
        Assert.Equal(128 * 1024, result.Output.Length);
        Assert.Equal(128 * 1024, result.Error.Length);
    }

    [Theory]
    [InlineData("flood")]
    [InlineData("error-flood")]
    public async Task OutputBoundsAreEnforcedForBothPipes(string mode)
    {
        var result = await fixture.RunAsync(mode, maxOutputBytes: 1024);
        Assert.Equal(RngitProcess.OutputLimitExceeded, result.Code);
        Assert.True(result.Output.Length <= 1024);
        Assert.Contains("byte combined stdout/stderr limit", result.Error);
        Assert.True(result.Error.Length <= 1200);
    }

    [Fact]
    public async Task TimeoutKillsOwnedProcessAndReturnsDiagnostic()
    {
        var result = await fixture.RunAsync("sleep", timeout: TimeSpan.FromMilliseconds(700));
        Assert.Equal(RngitProcess.TimedOut, result.Code);
        Assert.Contains("timed out", result.Error);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsOrdinaryFailure()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync("sleep", stop: stop.Token));
    }

    [Fact]
    public Task TimeoutKillsAnOwnedDescendantTree() => VerifyDescendantAsync("child");

    [WindowsPresentationFact]
    public Task ExitedParentWithInheritedChildPipesRetainsBoundedOwnedTree() => VerifyDescendantAsync("orphan-pipes");

    private async Task VerifyDescendantAsync(string mode)
    {
        fixture.BackendMode(mode);
        var running = RngitProcess.RunAsync(fixture.Backend, [], timeout: TimeSpan.FromSeconds(2));
        var pidPath = Path.Combine(fixture.Directory, "rngit-child-pid");
        for (var attempt = 0; attempt < 100 && !File.Exists(pidPath); attempt++) await Task.Delay(20);
        Assert.True(File.Exists(pidPath), "Owned child did not start within the bounded startup wait.");
        var pid = int.Parse(File.ReadAllText(pidPath), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(RngitProcess.TimedOut, (await running).Code);
        try
        {
            using var child = Process.GetProcessById(pid);
            for (var attempt = 0; attempt < 100 && !HasTerminated(child); attempt++) await Task.Delay(20);
            Assert.True(HasTerminated(child), "The owned descendant survived its parent's timeout.");
        }

        catch (ArgumentException) { /* Reaped processes no longer have an OS process entry. */ }
    }

    private static bool HasTerminated(Process child)
    {
        if (child.HasExited) return true;
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            // Orphaned zombies may remain until PID 1 reaps them, but cannot execute or hold pipes.
            var status = File.ReadAllText($"/proc/{child.Id}/stat");
            return status[(status.LastIndexOf(')') + 2)..].StartsWith("Z ", StringComparison.Ordinal)
                || status[(status.LastIndexOf(')') + 2)..].StartsWith("X ", StringComparison.Ordinal);
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    [Fact]
    public async Task SuccessfulExitBeforeStdinConsumptionIsNotSilentSuccess()
    {
        fixture.BackendMode("early");
        var result = await RngitProcess.RunAsync(fixture.Backend, [], stdin: new byte[16 * 1024 * 1024]);
        Assert.True(result.Code == RngitProcess.InputWriteFailed, $"Expected stdin failure, got {result.Code}: {result.Error}");
        Assert.Contains("did not accept all stdin", result.Error);
    }

    [Fact]
    public async Task StartFailureAndEmptyInputTerminate()
    {
        var result = await RngitProcess.RunAsync(Path.Combine(fixture.Directory, "missing-executable"), []);
        Assert.Equal(RngitProcess.StartFailed, result.Code);
        Assert.NotEmpty(result.Error);
        result = await fixture.RunAsync("echo");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task EnvironmentAndWorkingDirectoryAreExplicit()
    {
        var result = await RngitProcess.RunAsync(fixture.PowerShell, fixture.Arguments("environment"),
            fixture.Directory, environment: new Dictionary<string, string?> { ["RNGIT_TEST_VALUE"] = "π value" });
        Assert.Equal(0, result.Code);
        var text = Encoding.UTF8.GetString(result.Output);
        Assert.StartsWith("π value\n", text);
        Assert.EndsWith(fixture.Directory, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidLimitsAndPrecancelAreRejectedBeforeStart()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RngitProcess.RunAsync("missing", [], maxOutputBytes: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RngitProcess.RunAsync("missing", [], timeout: TimeSpan.Zero));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RngitProcess.RunAsync("missing", [], stop: new CancellationToken(true)));
    }

    public void Dispose() => fixture.Dispose();
}

public sealed class WindowsPresentationFactAttribute : FactAttribute
{
    public WindowsPresentationFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Exited-root descendant ownership uses Windows jobs; other platforms only verify live-root process-tree termination and bounded pipe draining.";
    }
}

internal sealed class PresentationProcessFixture : IDisposable
{
    public string Directory { get; }
    public string PowerShell { get; }
    public string Script { get; }
    public string Backend { get; }

    public PresentationProcessFixture()
    {
        var root = RepositoryRoot();
        Directory = Path.Combine(root, "TestResults", "presentation-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        var backendOutput = Path.Combine(root, "tests", "Fixtures", "RngitPresentationBackend", "bin", "Release", "net10.0");
        var isolatedBackend = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..",
            "RngitPresentationBackend", new DirectoryInfo(AppContext.BaseDirectory).Name));
        if (System.IO.Directory.Exists(isolatedBackend)) backendOutput = isolatedBackend;
        if (!System.IO.Directory.Exists(backendOutput))
            throw new InvalidOperationException("First build tests\\Fixtures\\RngitPresentationBackend\\RngitPresentationBackend.csproj -c Release.");
        foreach (var file in System.IO.Directory.GetFiles(backendOutput)) File.Copy(file, Path.Combine(Directory, Path.GetFileName(file)));
        Backend = Path.Combine(Directory, OperatingSystem.IsWindows() ? "RngitPresentationBackend.exe" : "RngitPresentationBackend");
        PowerShell = RngitMedia.FindExecutable("pwsh") ?? throw new InvalidOperationException("PowerShell 7 is required for owned process tests.");
        Script = Path.Combine(Directory, "fixture.ps1");
        File.WriteAllText(Script, """
            param([string] $Mode, [string] $Value)
            $ErrorActionPreference = 'Stop'
            $out = [Console]::OpenStandardOutput()
            $err = [Console]::OpenStandardError()
            $utf8 = [Text.UTF8Encoding]::new($false)
            switch ($Mode) {
                'echo' { [Console]::OpenStandardInput().CopyTo($out) }
                'argument' { $b = $utf8.GetBytes($Value); $out.Write($b); $out.Flush() }
                'environment' { $b = $utf8.GetBytes("$env:RNGIT_TEST_VALUE`n$((Get-Location).Path)"); $out.Write($b) }
                'both' {
                    $b = [byte[]]::new(131072); [Array]::Fill[byte]($b, [byte]65)
                    $out.Write($b); $out.Flush(); $err.Write($b); $err.Flush(); exit 7
                }
                'flood' {
                    $b = [byte[]]::new(8192)
                    for ($i=0; $i -lt 1024; $i++) { $out.Write($b); $out.Flush() }
                }
                'error-flood' {
                    $b = $utf8.GetBytes(('E' * 8192))
                    for ($i=0; $i -lt 1024; $i++) { $err.Write($b); $err.Flush() }
                }
                'sleep' { Start-Sleep -Seconds 60 }
                'editor' { [IO.File]::WriteAllText($Value, "edited π`n", $utf8) }
                'editor-fail' { exit 9 }
                'editor-large' { [IO.File]::WriteAllText($Value, ('x' * 4096), $utf8) }
                'editor-link' { Remove-Item -LiteralPath $Value; New-Item -ItemType SymbolicLink -Path $Value -Target (Join-Path $PSScriptRoot 'external.txt') | Out-Null }
            }
            """, new UTF8Encoding(false));
    }

    internal static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source)! })
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "DotReticulum.slnx"))) return current.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    public string[] Arguments(string mode, IReadOnlyList<string>? extra = null) =>
        new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Script, mode }.Concat(extra ?? []).ToArray();

    public Task<RngitProcessResult> RunAsync(string mode, byte[]? input = null, IReadOnlyList<string>? extra = null,
        TimeSpan? timeout = null, int maxOutputBytes = 4 * 1024 * 1024, CancellationToken stop = default) =>
        RngitProcess.RunAsync(PowerShell, Arguments(mode, extra), Directory, input,
            timeout ?? TimeSpan.FromSeconds(15), maxOutputBytes, stop);

    public RngitEditor Editor(string mode, bool authorized = true) => new(PowerShell, Arguments(mode), authorized);
    public void BackendMode(string mode, byte[]? output = null)
    {
        File.WriteAllText(Path.Combine(Directory, "rngit-backend-mode"), mode);
        if (output is not null) File.WriteAllText(Path.Combine(Directory, "rngit-backend-output"), Convert.ToBase64String(output));
    }
    public void Dispose()
    {
        // A terminated descendant can briefly retain its executable on Windows; bound the wait, then surface the failure.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
                return;
            }
            catch (Exception e) when (attempt < 50 && e is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
