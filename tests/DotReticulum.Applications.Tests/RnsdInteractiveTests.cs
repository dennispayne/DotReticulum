using DotReticulum.Applications.Rnsd;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Unit")]
public sealed class RnsdInteractiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "TestResults", "rnsd-interactive-" + Guid.NewGuid().ToString("N"));
    public RnsdInteractiveTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "config"), "[reticulum]\nshare_instance=no\n[logging]\nloglevel=4\n[interfaces]\n");
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("help\nstatus\ninterfaces\npersist\nquit\n")]
    [InlineData("help\r\nstatus\rinterfaces\npersist\r\nexit")]
    public async Task NativeCommandsRunAndPersistWithoutCodeExecution(string commands)
    {
        using var input = new StringReader(commands);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await RnsdApp.RunAsync(["--config", _directory, "--interactive"], output, error, CancellationToken.None, input);
        Assert.Equal(0, result);
        Assert.Empty(error.ToString());
        Assert.Contains("Commands: help", output.ToString());
        Assert.Contains("Status:", output.ToString());
        Assert.Contains("Interfaces:", output.ToString());
        Assert.Contains("State persisted.", output.ToString());
        Assert.Contains("Shutting down", output.ToString());
        Assert.True(File.Exists(Path.Combine(_directory, "storage", "transport_identity")));
        Assert.True(File.Exists(Path.Combine(_directory, "storage", "runtime-state-v1.msgpack")));
    }

    [Fact]
    public async Task EofGracefullyPersistsWithAndWithoutAnUnterminatedLastCommand()
    {
        foreach (var commands in new[] { "", "status" })
        {
            using var input = new StringReader(commands);
            using var output = new StringWriter();
            var result = await RnsdApp.RunAsync(["--config", _directory, "-i"], output, TextWriter.Null, CancellationToken.None, input);
            Assert.Equal(0, result);
            Assert.Contains("Shutting down", output.ToString());
        }
        Assert.True(File.Exists(Path.Combine(_directory, "storage", "runtime-state-v1.msgpack")));
    }

    [Fact]
    public async Task DiscoveryExecutionAuthorizationFlagIsExplicitlyAcceptedWithoutRunningUnconfiguredCommands()
    {
        using var input = new StringReader("quit\n");
        using var error = new StringWriter();
        Assert.Equal(0, await RnsdApp.RunAsync(["--config", _directory, "--allow-discovery-commands", "-i"], TextWriter.Null, error, CancellationToken.None, input));
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task OverlongLineIsDiscardedAndNextCommandRemainsUsable()
    {
        using var input = new StringReader(new string('x', RnsdInteractiveShell.MaximumLineLength + 1) + "\nstatus\nquit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await RnsdApp.RunAsync(["--config", _directory, "-i"], output, error, CancellationToken.None, input));
        Assert.Contains("exceeds 4096 characters; discarded the entire line", error.ToString());
        Assert.Contains("Status:", output.ToString());
    }

    [Fact]
    public async Task ExactLineBoundAndUnknownCommandsDoNotExecuteArbitraryCode()
    {
        var marker = Path.Combine(_directory, "must-not-exist");
        using var input = new StringReader(new string(' ', RnsdInteractiveShell.MaximumLineLength) + $"\nFile.WriteAllText(\"{marker}\", \"unsafe\")\nstatus\nexit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await RnsdApp.RunAsync(["--config", _directory, "-i"], output, error, CancellationToken.None, input));
        Assert.DoesNotContain("exceeds", error.ToString());
        Assert.Contains("Arbitrary code execution is not supported", error.ToString());
        Assert.Contains("Status:", output.ToString());
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task CancellationDoesNotWaitForMoreInputAndStillPersists()
    {
        using var input = new BlockingReader();
        using var stop = new CancellationTokenSource();
        var run = RnsdApp.RunAsync(["--config", _directory, "-i"], TextWriter.Null, TextWriter.Null, stop.Token, input);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(File.Exists(Path.Combine(_directory, "storage", "runtime-state-v1.msgpack")));
    }

    private sealed class BlockingReader : TextReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
