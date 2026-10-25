using System.Diagnostics;
using System.Collections.Concurrent;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnx;

public sealed class RnxListenerOptions
{
    public List<byte[]> AllowedIdentities { get; init; } = [];

    /// <summary>Accept commands from anyone (upstream <c>--noauth</c>). Dangerous: remote code execution.</summary>
    public bool AllowAll { get; init; }

    public int MaxOutputBytes { get; init; } = 16 * 1024 * 1024;

    public int MaxConcurrentCommands { get; init; } = 4;

    public TimeSpan MaxExecutionTime { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The <c>rnx.execute</c> destination, wire-compatible with upstream <c>rnx -l</c>. Request path <c>command</c>, request data
/// <c>[command bytes, timeout, stdout limit, stderr limit, stdin bytes]</c>; response
/// <c>[executed, return code, stdout, stderr, stdout length, stderr length, started, concluded]</c>. The command line is split like
/// Python's <c>shlex.split</c> and run without a shell.
/// </summary>
public sealed class RnxListener : IDisposable
{
    public const string AppName = "rnx";
    public const string Aspect = "execute";
    public const string CommandPath = "command";

    private readonly RnxListenerOptions _options;
    private readonly LocalDestination _local;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _links = new();
    private readonly SemaphoreSlim _commands;
    private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _disposed;

    public RnxListener(TransportRouter router, Identity identity, RnxListenerOptions options)
    {
        _options = options;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentCommands);
        if (options.MaxExecutionTime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        _commands = new SemaphoreSlim(options.MaxConcurrentCommands);
        var destination = Destination.Create(DestinationType.Single, AppName, [Aspect], identity);
        _local = new LocalDestination(identity, destination) { AcceptLinks = true, LinkEstablished = OnLinkEstablished };
        _local.RegisterRequestHandler(CommandPath, OnCommand, options.AllowAll ? RequestPolicy.All : RequestPolicy.List, options.AllowedIdentities);
        Router = router;
        router.RegisterLocalDestination(_local);
    }

    public TransportRouter Router { get; }

    public byte[] DestinationHash => _local.Destination.Hash;

    public string DestinationHashHex => Convert.ToHexStringLower(DestinationHash);

    public Action<string>? Log { get; set; }

    public ValueTask AnnounceAsync(CancellationToken cancellationToken = default) => Router.AnnounceAsync(_local, cancellationToken: cancellationToken);

    private void OnLinkEstablished(Link link)
    {
        var key = Convert.ToHexStringLower(link.LinkId);
        var cancellation = new CancellationTokenSource();
        _links[key] = cancellation;
        if (Volatile.Read(ref _disposed) != 0) cancellation.Cancel();
        Log?.Invoke("Command link " + Convert.ToHexStringLower(link.LinkId) + " established");
        link.Closed = l =>
        {
            if (_links.TryRemove(key, out var pending)) pending.Cancel();
            Log?.Invoke("Command link " + Convert.ToHexStringLower(l.LinkId) + " closed");
        };
        link.RemoteIdentified = (l, identity) =>
        {
            Log?.Invoke("Initiator of link " + Convert.ToHexStringLower(l.LinkId) + " identified as " + CliSupport.PrettyHex(identity.Hash));
            if (!_options.AllowAll && !_options.AllowedIdentities.Any(h => h.AsSpan().SequenceEqual(identity.Hash)))
            {
                Log?.Invoke("Identity " + CliSupport.PrettyHex(identity.Hash) + " not allowed, tearing down link");
                l.Teardown();
            }
        };
    }

    private object? OnCommand(RequestContext context)
    {
        if (context.Data is not object?[] { Length: >= 5 } data || data[0] is not byte[] commandBytes) return null;
        var command = System.Text.Encoding.UTF8.GetString(commandBytes);
        double? timeout;
        long? outLimit, errLimit;
        try
        {
            timeout = data[1] is null ? null : MsgPack.ToDouble(data[1]);
            outLimit = data[2] is null ? null : MsgPack.ToInt64(data[2]);
            errLimit = data[3] is null ? null : MsgPack.ToInt64(data[3]);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException) { return null; }
        if (timeout is { } value && !double.IsFinite(value)) return null;
        var stdin = data[4] as byte[];
        Log?.Invoke("Executing command [" + command + "] for " + (context.RemoteIdentity is { } id ? CliSupport.PrettyHex(id.Hash) : "unknown requestor"));
        var token = _links.TryGetValue(Convert.ToHexStringLower(context.LinkId), out var pending) ? pending.Token : _stop.Token;
        if (Volatile.Read(ref _disposed) != 0) return null;
        Interlocked.Increment(ref _active);
        try
        {
            _commands.Wait(token);
            try
            {
                var boundedTimeout = Math.Min(timeout ?? _options.MaxExecutionTime.TotalSeconds, _options.MaxExecutionTime.TotalSeconds);
                var result = Execute(command, boundedTimeout, outLimit, errLimit, stdin, token, _options.MaxOutputBytes);
                Log?.Invoke("Delivering result of command [" + command + "]");
                return result;
            }
            finally { _commands.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        finally
        {
            if (Interlocked.Decrement(ref _active) == 0 && Volatile.Read(ref _disposed) != 0) _idle.TrySetResult();
        }
    }

    /// <summary>Runs a command and builds the upstream result array (see the type documentation).</summary>
    public static object?[] Execute(string command, double? timeout, long? outLimit, long? errLimit, byte[]? stdin,
        CancellationToken cancellationToken = default, int maxOutputBytes = 16 * 1024 * 1024)
        => ExecuteAsync(command, timeout, outLimit, errLimit, stdin, cancellationToken, maxOutputBytes).GetAwaiter().GetResult();

    private static async Task<object?[]> ExecuteAsync(string command, double? timeout, long? outLimit, long? errLimit, byte[]? stdin,
        CancellationToken cancellationToken, int maxOutputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);
        var started = UnixNow();
        var result = new object?[] { false, null, null, null, null, null, started, null };
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout is { } invalid && !double.IsFinite(invalid)) return result;
        Process process;
        try
        {
            var parts = CliSupport.ShellSplit(command);
            if (parts.Count == 0) return result;
            var info = new ProcessStartInfo(parts[0])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in parts.Skip(1)) info.ArgumentList.Add(arg);
            process = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FormatException or ArgumentException or IOException)
        {
            return result;
        }

        using (process)
        {
            result[0] = true;
            var stdout = new BoundedOutput(maxOutputBytes, outLimit);
            var stderr = new BoundedOutput(maxOutputBytes, errLimit);
            using var drains = new CancellationTokenSource();
            var outTask = stdout.DrainAsync(process.StandardOutput.BaseStream, drains.Token);
            var errTask = stderr.DrainAsync(process.StandardError.BaseStream, drains.Token);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } seconds)
            {
                lifetime.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, uint.MaxValue / 1000.0 - 1)));
            }
            var input = WriteInputAsync(process, stdin, lifetime.Token);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                timedOut = true;
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await input.ConfigureAwait(false);
            var drainTask = Task.WhenAll(outTask, errTask);
            try { await drainTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                await drains.CancelAsync().ConfigureAwait(false);
                try { await drainTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (drains.IsCancellationRequested) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            result[1] = (long)process.ExitCode;
            result[2] = stdout.Result();
            result[3] = stderr.Result();
            result[4] = stdout.Length;
            result[5] = stderr.Length;
            if (!timedOut && timeout is not null) result[7] = UnixNow();
            return result;
        }
    }

    private static async Task WriteInputAsync(Process process, byte[]? stdin, CancellationToken token)
    {
        try
        {
            if (stdin is not null) await process.StandardInput.BaseStream.WriteAsync(stdin, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { process.StandardInput.Close(); }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private sealed class BoundedOutput(int capacity, long? limit)
    {
        private readonly MemoryStream _prefix = new();
        public long Length { get; private set; }

        public async Task DrainAsync(Stream stream, CancellationToken token)
        {
            var buffer = new byte[8192];
            var retained = limit is >= 0 ? Math.Min(limit.Value, capacity) : capacity;
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                var keep = (int)Math.Min(count, Math.Max(0, retained - _prefix.Length));
                if (keep != 0) _prefix.Write(buffer, 0, keep);
                Length += count;
            }
        }

        public byte[] Result()
        {
            var requested = limit is < 0 ? (limit.Value < -Length ? 0 : Length + limit.Value) : limit ?? Length;
            var bytes = _prefix.ToArray();
            return bytes[..(int)Math.Min(bytes.Length, Math.Max(0, requested))];
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        foreach (var pending in _links.Values) pending.Cancel();
        if (Volatile.Read(ref _active) == 0) _idle.TrySetResult();
        _idle.Task.Wait(TimeSpan.FromSeconds(10));
    }

    private static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}

/// <summary>Outcome of one remote execution; <see cref="Error"/> is set (with the upstream exit code) when no result arrived.</summary>
public sealed record RnxResult(bool Executed, long? ReturnCode, byte[] Stdout, byte[] Stderr, long StdoutLength, long StderrLength,
    double Started, double? Concluded, string? Error = null, int ErrorCode = 0)
{
    public bool Ok => Error is null;

    public static RnxResult Failure(string error, int code) => new(false, null, [], [], 0, 0, 0, null, error, code);
}

/// <summary>Client side of rnx. Keeps one link per destination so several commands can share it (interactive mode).</summary>
public sealed class RnxClient(ReticulumNode node, Identity identity, byte[] destinationHash, bool identify = true)
{
    private Link? _link;
    private bool _identified;

    public Task CloseAsync()
    {
        _link?.Teardown();
        _link = null;
        return Task.CompletedTask;
    }

    public async Task<RnxResult> ExecuteAsync(string command, TimeSpan connectTimeout, long? stdoutLimit = null, long? stderrLimit = null,
        byte[]? stdin = null, TimeSpan? resultTimeout = null, CancellationToken cancellationToken = default)
    {
        if (_link is null || _link.Status != LinkStatus.Active)
        {
            _identified = false;
            var connected = await ClientLinks.ConnectAsync(node, destinationHash, connectTimeout, cancellationToken).ConfigureAwait(false);
            if (connected.Link is not { } link) return RnxResult.Failure(connected.Error!, connected.Error == "Path not found" ? 242 : 243);
            _link = link;
        }

        if (identify && !_identified)
        {
            _link.Identify(identity);
            _identified = true;
        }

        object?[] request = [Encoding(command), connectTimeout.TotalSeconds, stdoutLimit, stderrLimit, stdin];
        var timeout = connectTimeout.TotalSeconds + ((_link.Rtt ?? 0) * 4) + 2.0;
        var receiving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedResource = 0;
        var receipt = _link.Request(RnxListener.CommandPath, request, timeout: timeout,
            progressCallback: progress =>
            {
                if (progress.Status == RequestStatus.Receiving)
                {
                    Interlocked.Exchange(ref receivedResource, 1);
                    receiving.TrySetResult();
                }
            }, cancellationToken: cancellationToken);
        if (receipt is null) return RnxResult.Failure("Could not request remote execution, link was closed", 244);

        object? response;
        try
        {
            var wait = receipt.ResponseTask.WaitAsync(cancellationToken);
            if (resultTimeout is { } limit && await Task.WhenAny(wait, receiving.Task).ConfigureAwait(false) == receiving.Task)
                response = await wait.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
            else response = await wait.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            receipt.Cancel();
            return Volatile.Read(ref receivedResource) != 0
                ? RnxResult.Failure("Receiving result failed", 246)
                : _link.Status == LinkStatus.Closed
                ? RnxResult.Failure("Could not request remote execution, link was closed", 244)
                : RnxResult.Failure("No result was received", 245);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _link.Teardown();
            throw;
        }

        if (response is not object?[] { Length: >= 8 } fields || fields[0] is not bool executed) return RnxResult.Failure(response is null ? "No response" : "Received invalid result", response is null ? 249 : 247);
        if (!executed) return RnxResult.Failure("Remote could not execute command", 248);
        try
        {
            if (fields[2] is not null and not byte[] || fields[3] is not null and not byte[]) return RnxResult.Failure("Received invalid result", 247);
            return new RnxResult(true, fields[1] is null ? null : MsgPack.ToInt64(fields[1]), fields[2] as byte[] ?? [], fields[3] as byte[] ?? [],
                fields[4] is null ? 0 : MsgPack.ToInt64(fields[4]), fields[5] is null ? 0 : MsgPack.ToInt64(fields[5]), MsgPack.ToDouble(fields[6]),
                fields[7] is null ? null : MsgPack.ToDouble(fields[7]));
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException or OverflowException)
        { return RnxResult.Failure("Received invalid result", 247); }
    }

    private static byte[] Encoding(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}
