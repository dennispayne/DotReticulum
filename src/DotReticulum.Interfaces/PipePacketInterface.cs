using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

public sealed record PipePacketInterfaceOptions
{
    public long? ConfiguredBitrate { get; init; }
    public TimeSpan RespawnDelay { get; init; } = TimeSpan.FromSeconds(5);
    public int? MaximumRespawnAttempts { get; init; }
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(12);
    public int ReceiveQueueFrames { get; init; } = 256;
}

/// <summary>Runs an explicitly named executable, exchanging bounded HDLC frames over its binary stdin/stdout.</summary>
public sealed class PipePacketInterface : IReconnectingPacketInterface, IPacketInterfaceDiagnostics
{
    private readonly string _executable;
    private readonly string[] _arguments;
    private readonly PipePacketInterfaceOptions _options;
    private readonly Func<ProcessStartInfo, Process> _startProcess = StartOwnedProcess;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<ReadOnlyMemory<byte>> _received;
    private Process? _process;
    private Task? _worker;
    private Task? _disposeTask;
    private bool _started;
    private int _receiving;
    private int _respawns;
    private long _rx;
    private long _tx;
    private long _spawnFailures;
    private string? _lastError;
    private int? _lastExitCode;

    public PipePacketInterface(string executable, IEnumerable<string>? arguments = null, PipePacketInterfaceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _executable = executable;
        _arguments = arguments?.ToArray() ?? [];
        if (_arguments.Any(argument => argument is null)) throw new ArgumentException("Arguments cannot contain null.", nameof(arguments));
        _options = options ?? new();
        if (_options.RespawnDelay <= TimeSpan.Zero || _options.WriteTimeout <= TimeSpan.Zero ||
            _options.ReceiveQueueFrames < 1 || _options.MaximumRespawnAttempts is < 0 || _options.ConfiguredBitrate is < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        _received = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(_options.ReceiveQueueFrames)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    }

    internal PipePacketInterface(string executable, IEnumerable<string> arguments, PipePacketInterfaceOptions options,
        Func<ProcessStartInfo, Process> startProcess) : this(executable, arguments, options)
    {
        _startProcess = startProcess;
    }

    public long Bitrate => _options.ConfiguredBitrate ?? 1_000_000;
    public int HardwareMtu => 1064;
    public bool SupportsMtuDiscovery => false;
    public bool IsSharedMedium => true;
    public bool IsConnected
    {
        get { lock (_gate) return _disposeTask is null && _process is { HasExited: false }; }
    }
    public event Action? Connected;
    public event Action? Disconnected;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started) throw new InvalidOperationException("The pipe interface has already started.");
            _process = Spawn();
            _started = true;
            _worker = Task.Run(RunAsync);
        }
        Notify(Connected);
        return ValueTask.CompletedTask;
    }

    private Process Spawn()
    {
        var info = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        foreach (var argument in _arguments) info.ArgumentList.Add(argument);
        try { return _startProcess(info); }
        catch
        {
            Interlocked.Increment(ref _spawnFailures);
            throw;
        }
    }

    private static Process StartOwnedProcess(ProcessStartInfo info)
    {
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException("The pipe executable did not start.");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private async Task RunAsync()
    {
        Exception? terminal = null;
        try
        {
            var attempts = 0;
            while (!_lifetime.IsCancellationRequested)
            {
                Process? process;
                lock (_gate) process = _process;
                if (process is null)
                {
                    if (_options.MaximumRespawnAttempts is { } maximum && attempts >= maximum)
                        throw new IOException("Maximum pipe respawn attempts reached.");
                    await Task.Delay(_options.RespawnDelay, _lifetime.Token).ConfigureAwait(false);
                    attempts++;
                    Interlocked.Increment(ref _respawns);
                    try
                    {
                        lock (_gate)
                        {
                            if (_disposeTask is not null) break;
                            _process = Spawn();
                        }
                        Notify(Connected);
                    }
                    catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                    { Report(error); }
                    continue;
                }
                attempts = 0;
                try
                {
                    await ReadProcessAsync(process, _lifetime.Token).ConfigureAwait(false);
                    if (!_lifetime.IsCancellationRequested)
                        Report(new IOException(process.HasExited
                            ? $"Pipe child exited with status {process.ExitCode}."
                            : "Pipe child closed its output before exiting."));
                }
                catch (Exception error) when (!_lifetime.IsCancellationRequested && error is IOException or ObjectDisposedException)
                { Report(error); }
                finally
                {
                    lock (_gate) if (ReferenceEquals(_process, process)) _process = null;
                    await StopProcessAsync(process).ConfigureAwait(false);
                    if (!_lifetime.IsCancellationRequested) Notify(Disconnected);
                }
            }
        }
        catch (Exception error) when (_lifetime.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or IOException) { }
        catch (Exception error) { terminal = error; Report(error); }
        finally { _received.Writer.TryComplete(terminal); }
    }

    private async Task ReadProcessAsync(Process process, CancellationToken token)
    {
        var decoder = new PipeFrameDecoder(HardwareMtu);
        var buffer = new byte[32768];
        var frames = new List<byte[]>();
        while (true)
        {
            var count = await process.StandardOutput.BaseStream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return;
            decoder.Feed(buffer.AsSpan(0, count), frames);
            foreach (var frame in frames)
            {
                Interlocked.Add(ref _rx, frame.Length);
                await _received.Writer.WriteAsync(frame, token).ConfigureAwait(false);
            }
            frames.Clear();
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (packet.IsEmpty || packet.Length > HardwareMtu) throw new ArgumentException("Invalid pipe frame length.", nameof(packet));
        lock (_gate) EnsureStarted();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(_options.WriteTimeout);
        await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        Process? process = null;
        var beganWrite = false;
        try
        {
            lock (_gate)
            {
                EnsureStarted();
                process = _process ?? throw new IOException("The pipe child is offline.");
            }
            var frame = HdlcPacketFraming.Encode(packet, HardwareMtu, validatePacket: false);
            beganWrite = true;
            await process.StandardInput.BaseStream.WriteAsync(frame, linked.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
            Interlocked.Add(ref _tx, frame.Length);
        }
        catch (Exception error) when (beganWrite)
        {
            Report(error);
            // A canceled/failed stream write may have emitted a prefix. Never append a later frame to that prefix.
            if (process is not null) KillOwnedProcess(process);
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        lock (_gate) EnsureStarted();
        if (Interlocked.Exchange(ref _receiving, 1) != 0) throw new InvalidOperationException("Only one receive enumerator is supported.");
        return ReadQueuedAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadQueuedAsync([EnumeratorCancellation] CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await using var reader = _received.Reader.ReadAllAsync(linked.Token).GetAsyncEnumerator();
        while (true)
        {
            bool more;
            try { more = await reader.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !token.IsCancellationRequested) { yield break; }
            if (!more) yield break;
            yield return reader.Current;
        }
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        if (!_started) throw new InvalidOperationException("The pipe interface has not started.");
    }

    private static void KillOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }

    private async Task StopProcessAsync(Process process)
    {
        try
        {
            KillOwnedProcess(process);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            lock (_gate) _lastExitCode = process.ExitCode;
            await _writeGate.WaitAsync().ConfigureAwait(false);
            _writeGate.Release();
        }
        finally { process.Dispose(); }
    }

    private void Report(Exception error)
    {
        var message = error.GetType().Name + ": " + error.Message;
        lock (_gate) _lastError = message[..Math.Min(message.Length, 4096)];
        Trace.TraceError("Pipe interface: {0}", error);
    }

    private void Notify(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error) { Report(error); }
        }
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["rxb"] = Interlocked.Read(ref _rx), ["txb"] = Interlocked.Read(ref _tx),
            ["status"] = IsConnected, ["pid"] = _process?.Id, ["last_exit_code"] = _lastExitCode,
            ["last_error"] = _lastError, ["respawn_attempts"] = Volatile.Read(ref _respawns),
            ["spawn_failures"] = Interlocked.Read(ref _spawnFailures), ["receive_queue_frames"] = _received.Reader.Count,
            ["bitrate"] = Bitrate, ["hardware_mtu"] = HardwareMtu, ["shared_medium"] = true,
        };
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task? worker;
        lock (_gate)
        {
            if (_process is not null) KillOwnedProcess(_process);
            worker = _worker;
        }
        if (worker is not null) await worker.ConfigureAwait(false);
        _received.Writer.TryComplete();
    }
}
