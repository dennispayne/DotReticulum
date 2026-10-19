using System.Diagnostics;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnx;

public sealed class RnxListenerOptions
{
    public List<byte[]> AllowedIdentities { get; init; } = [];

    /// <summary>Accept commands from anyone (upstream <c>--noauth</c>). Dangerous: remote code execution.</summary>
    public bool AllowAll { get; init; }
}

/// <summary>
/// The <c>rnx.execute</c> destination, wire-compatible with upstream <c>rnx -l</c>. Request path <c>command</c>, request data
/// <c>[command bytes, timeout, stdout limit, stderr limit, stdin bytes]</c>; response
/// <c>[executed, return code, stdout, stderr, stdout length, stderr length, started, concluded]</c>. The command line is split like
/// Python's <c>shlex.split</c> and run without a shell.
/// </summary>
public sealed class RnxListener
{
    public const string AppName = "rnx";
    public const string Aspect = "execute";
    public const string CommandPath = "command";

    private readonly RnxListenerOptions _options;
    private readonly LocalDestination _local;

    public RnxListener(TransportRouter router, Identity identity, RnxListenerOptions options)
    {
        _options = options;
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
        Log?.Invoke("Command link " + Convert.ToHexStringLower(link.LinkId) + " established");
        link.Closed = l => Log?.Invoke("Command link " + Convert.ToHexStringLower(l.LinkId) + " closed");
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
        double? timeout = data[1] is null ? null : MsgPack.ToDouble(data[1]);
        long? outLimit = data[2] is null ? null : MsgPack.ToInt64(data[2]);
        long? errLimit = data[3] is null ? null : MsgPack.ToInt64(data[3]);
        var stdin = data[4] as byte[];
        Log?.Invoke("Executing command [" + command + "] for " + (context.RemoteIdentity is { } id ? CliSupport.PrettyHex(id.Hash) : "unknown requestor"));
        var result = Execute(command, timeout, outLimit, errLimit, stdin);
        Log?.Invoke("Delivering result of command [" + command + "]");
        return result;
    }

    /// <summary>Runs a command and builds the upstream result array (see the type documentation).</summary>
    public static object?[] Execute(string command, double? timeout, long? outLimit, long? errLimit, byte[]? stdin)
    {
        var started = UnixNow();
        var result = new object?[] { false, null, null, null, null, null, started, null };
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
            using var stdout = new MemoryStream();
            using var stderr = new MemoryStream();
            var outTask = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            var errTask = process.StandardError.BaseStream.CopyToAsync(stderr);
            try
            {
                if (stdin is not null) process.StandardInput.BaseStream.Write(stdin);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            var timedOut = false;
            if (timeout is { } seconds)
            {
                if (!process.WaitForExit(TimeSpan.FromSeconds(Math.Max(seconds, 0))))
                {
                    timedOut = true;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
            }
            else
            {
                process.WaitForExit();
            }

            Task.WaitAll(outTask, errTask);
            var outBytes = stdout.ToArray();
            var errBytes = stderr.ToArray();
            result[1] = (long)process.ExitCode;
            result[2] = Limit(outBytes, outLimit);
            result[3] = Limit(errBytes, errLimit);
            result[4] = (long)outBytes.Length;
            result[5] = (long)errBytes.Length;
            if (!timedOut) result[7] = UnixNow();
            return result;
        }
    }

    private static byte[] Limit(byte[] data, long? limit) => limit is { } l && data.Length > l ? data[..(int)Math.Max(l, 0)] : data;

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
        var receipt = _link.Request(RnxListener.CommandPath, request, timeout: timeout);
        if (receipt is null) return RnxResult.Failure("Could not request remote execution, link was closed", 244);

        object? response;
        try
        {
            var wait = receipt.ResponseTask.WaitAsync(cancellationToken);
            response = resultTimeout is { } limit ? await wait.WaitAsync(limit + TimeSpan.FromSeconds(timeout)).ConfigureAwait(false) : await wait.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return _link.Status == LinkStatus.Closed
                ? RnxResult.Failure("Could not request remote execution, link was closed", 244)
                : RnxResult.Failure("No result was received", 245);
        }

        if (response is not object?[] { Length: >= 8 } fields || fields[0] is not bool executed) return RnxResult.Failure(response is null ? "No response" : "Received invalid result", response is null ? 249 : 247);
        if (!executed) return RnxResult.Failure("Remote could not execute command", 248);
        return new RnxResult(true, fields[1] is null ? null : MsgPack.ToInt64(fields[1]), fields[2] as byte[] ?? [], fields[3] as byte[] ?? [],
            fields[4] is null ? 0 : MsgPack.ToInt64(fields[4]), fields[5] is null ? 0 : MsgPack.ToInt64(fields[5]), MsgPack.ToDouble(fields[6]),
            fields[7] is null ? null : MsgPack.ToDouble(fields[7]));
    }

    private static byte[] Encoding(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}
