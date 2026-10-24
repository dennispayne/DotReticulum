using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>One independent I2P stream. Initiators retain this interface identity across reconnection.</summary>
public sealed class I2pPacketPeer : IReconnectingPacketInterface, IPacketInterfaceDiagnostics, IPacketInterfaceChildPolicy
{
    private readonly I2pPacketInterface _parent;
    private readonly I2pPacketInterfaceOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly Channel<ReadOnlyMemory<byte>> _received;
    private readonly object _gate = new();
    private I2pSamConnection? _connection;
    private Task? _worker;
    private Task? _dispose;
    private int _receiving;
    private long _lastRead;
    private long _lastWrite;
    private long _rx;
    private long _tx;
    private int _reconnects;
    private bool _connected;
    private string _phase = "queued";
    private long _phaseStarted = Environment.TickCount64;
    private string? _lastFailurePhase;
    private string? _lastError;
    private int _sessionAttempts;
    private int _streamAttempts;
    private string? _sessionId;
    private string? _localBase32;

    internal I2pPacketPeer(I2pPacketInterface parent, I2pPacketInterfaceOptions options, string destination, bool initiator)
    {
        _parent = parent;
        _options = options;
        Destination = destination;
        IsInitiator = initiator;
        _received = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(options.ReceiveQueueCapacity)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public string Destination { get; }
    public bool IsInitiator { get; }
    public bool InheritParentOutgoing => !IsInitiator;
    public bool Outgoing => true;
    public bool CountParentTraffic => !IsInitiator;
    public long Bitrate => IsInitiator ? I2pPacketInterface.NominalBitrate : _parent.Bitrate;
    public int HardwareMtu => I2pPacketInterface.MaximumFrameLength;
    public bool SupportsMtuDiscovery => true;
    public bool IsConnected { get { lock (_gate) return _connected && _dispose is null; } }
    public event Action? Connected;
    public event Action? Disconnected;

    internal void Begin(I2pSamConnection? accepted = null)
    {
        lock (_gate)
        {
            if (_dispose is not null) { accepted?.DisposeAsync().GetAwaiter().GetResult(); return; }
            _worker = Task.Run(() => RunAsync(accepted));
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(I2pSamConnection? accepted)
    {
        string? privateDestination = null;
        I2pSamConnection? session = null;
        CancellationTokenSource? sessionLifetime = null;
        Task? monitor = null;
        string? sessionId = null;
        var attempts = 0;

        async Task CloseSessionAsync()
        {
            if (sessionLifetime is not null) await sessionLifetime.CancelAsync().ConfigureAwait(false);
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            if (monitor is not null) await JoinAsync(monitor, sessionLifetime!.Token).ConfigureAwait(false);
            sessionLifetime?.Dispose();
            session = null;
            monitor = null;
            sessionLifetime = null;
            sessionId = null;
            lock (_gate) _sessionId = null;
        }

        try
        {
            do
            {
                try
                {
                    if (accepted is not null)
                    {
                        await using (accepted.ConfigureAwait(false))
                            await RunConnectionAsync(accepted, _lifetime.Token).ConfigureAwait(false);
                        return;
                    }
                    using var setup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    setup.CancelAfter(_options.SetupTimeout);
                    if (session is null)
                    {
                        if (privateDestination is null)
                        {
                            SetPhase("destination_generate");
                            privateDestination = await _parent.GenerateDestinationAsync(setup.Token).ConfigureAwait(false);
                            lock (_gate) _localBase32 = I2pDestination.Base32FromPrivate(privateDestination) + ".b32.i2p";
                        }
                        SetPhase("session_open");
                        session = await I2pSamConnection.OpenAsync(_options, setup.Token).ConfigureAwait(false);
                        sessionId = "reticulum-" + Guid.NewGuid().ToString("N");
                        SetPhase("session_create");
                        Interlocked.Increment(ref _sessionAttempts);
                        await session.CommandAsync($"SESSION CREATE STYLE=STREAM ID={sessionId} DESTINATION={privateDestination} \n",
                            "SESSION", "STATUS", setup.Token).ConfigureAwait(false);
                        sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        monitor = session.WatchSessionAsync(sessionLifetime.Token);
                        lock (_gate) _sessionId = sessionId;
                    }
                    await WithSessionAsync(ConnectStreamAsync(sessionId!, setup, () => attempts = 0),
                        monitor!, setup).ConfigureAwait(false);
                }
                catch (Exception error) when (!_lifetime.IsCancellationRequested)
                {
                    var lostSession = monitor?.IsCompleted == true;
                    if (lostSession) SetPhase("session_control");
                    ReportFailure(error);
                    if (session is not null && (monitor is null || lostSession || error is I2pSamException { Result: "INVALID_ID" }))
                        await CloseSessionAsync().ConfigureAwait(false);
                }
                if (_lifetime.IsCancellationRequested || !IsInitiator) break;
                if (_options.MaxReconnectAttempts is { } maximum && attempts >= maximum) break;
                attempts++;
                Interlocked.Increment(ref _reconnects);
                SetPhase("reconnect_delay");
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                try
                {
                    var wait = Task.Delay(_options.ReconnectDelay, delay.Token);
                    if (monitor is null) await wait.ConfigureAwait(false);
                    else await WithSessionAsync(wait, monitor, delay).ConfigureAwait(false);
                }
                catch (Exception error) when (!_lifetime.IsCancellationRequested)
                {
                    SetPhase("session_control");
                    ReportFailure(error);
                    await CloseSessionAsync().ConfigureAwait(false);
                }
            } while (!_lifetime.IsCancellationRequested);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            await CloseSessionAsync().ConfigureAwait(false);
            lock (_gate) { _connected = false; _connection = null; }
            SetPhase("stopped");
            _received.Writer.TryComplete();
            _parent.PeerEnded(this);
        }
    }

    private async Task ConnectStreamAsync(string sessionId, CancellationTokenSource setup, Action connected)
    {
        var destination = Destination;
        if (destination.EndsWith(".i2p", StringComparison.Ordinal))
        {
            SetPhase("naming_lookup");
            await using var naming = await I2pSamConnection.OpenAsync(_options, setup.Token).ConfigureAwait(false);
            var reply = await naming.CommandAsync($"NAMING LOOKUP NAME={destination}\n", "NAMING", "REPLY", setup.Token).ConfigureAwait(false);
            if (!reply.TryGetValue("VALUE", out destination)) throw new InvalidDataException("SAM naming reply is missing VALUE.");
            I2pSamConnection.ValidateToken(destination);
        }
        SetPhase("stream_open");
        await using var stream = await I2pSamConnection.OpenAsync(_options, setup.Token).ConfigureAwait(false);
        SetPhase("stream_connect");
        Interlocked.Increment(ref _streamAttempts);
        await stream.CommandAsync($"STREAM CONNECT ID={sessionId} DESTINATION={destination} SILENT=false\n",
            "STREAM", "STATUS", setup.Token).ConfigureAwait(false);
        setup.CancelAfter(Timeout.InfiniteTimeSpan);
        connected();
        await RunConnectionAsync(stream, setup.Token).ConfigureAwait(false);
    }

    private static async Task WithSessionAsync(Task operation, Task monitor, CancellationTokenSource cancellation)
    {
        if (await Task.WhenAny(operation, monitor).ConfigureAwait(false) == monitor)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await JoinAsync(operation, cancellation.Token).ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
        }
        await operation.ConfigureAwait(false);
    }

    private void ReportFailure(Exception error)
    {
        Exception reported;
        lock (_gate)
        {
            _lastFailurePhase = _phase;
            reported = error is OperationCanceledException
                ? new TimeoutException($"I2P peer setup timed out during {_phase}; shared setup budget {_options.SetupTimeout.TotalSeconds:g} seconds.", error)
                : error;
            _lastError = reported.Message;
        }
        _parent.Report(reported);
    }

    private void SetPhase(string phase)
    {
        lock (_gate) { _phase = phase; _phaseStarted = Environment.TickCount64; }
    }

    internal static async Task JoinAsync(Task task, CancellationToken token)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) when (token.IsCancellationRequested) { }
    }

    private async Task RunConnectionAsync(I2pSamConnection connection, CancellationToken token)
    {
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_gate)
        {
            _connection = connection;
            _connected = true;
            _lastRead = _lastWrite = Environment.TickCount64;
            SetPhase("connected");
        }
        Notify(Connected);
        var watchdog = WatchdogAsync(connection, active);
        try
        {
            var buffer = new byte[4096];
            var decoder = new HdlcFrameDecoder(HardwareMtu, validatePackets: false);
            var frames = new List<byte[]>();
            while (true)
            {
                var count = await connection.Stream.ReadAsync(buffer, active.Token).ConfigureAwait(false);
                if (count == 0) break;
                Interlocked.Exchange(ref _lastRead, Environment.TickCount64);
                frames.Clear();
                decoder.Feed(buffer.AsSpan(0, count), frames);
                foreach (var frame in frames)
                {
                    Interlocked.Add(ref _rx, frame.Length);
                    if (!IsInitiator) _parent.RecordTraffic(false, frame.Length);
                    await _received.Writer.WriteAsync(frame, active.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && active.IsCancellationRequested)
        {
            throw new IOException("I2P stream watchdog stopped the connection.");
        }
        finally
        {
            await active.CancelAsync().ConfigureAwait(false);
            await JoinAsync(watchdog, active.Token).ConfigureAwait(false);
            lock (_gate) { _connected = false; _connection = null; }
            if (!_lifetime.IsCancellationRequested) Notify(Disconnected);
        }
    }

    private async Task WatchdogAsync(I2pSamConnection connection, CancellationTokenSource active)
    {
        var token = active.Token;
        var tick = TimeSpan.FromMilliseconds(Math.Min(1000, _options.KeepAliveInterval.TotalMilliseconds));
        try
        {
            while (true)
            {
                await Task.Delay(tick, token).ConfigureAwait(false);
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastRead) > _options.ReadTimeout.TotalMilliseconds)
                    throw new TimeoutException("I2P stream became unresponsive.");
                if (now - Interlocked.Read(ref _lastWrite) > _options.KeepAliveInterval.TotalMilliseconds)
                {
                    await _writes.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await connection.Stream.WriteAsync(new byte[] { 0x7e, 0x7e }, token).ConfigureAwait(false);
                    }
                    finally { _writes.Release(); }
                }
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            _parent.Report(error);
            await active.CancelAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _receiving, 1) != 0) throw new InvalidOperationException("ReceiveAsync supports a single enumerator.");
        return ReadAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var reader = _received.Reader.ReadAllAsync(linked.Token).GetAsyncEnumerator();
        await using (reader.ConfigureAwait(false))
        {
            while (true)
            {
                bool more;
                try { more = await reader.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { yield break; }
                if (!more) yield break;
                yield return reader.Current;
            }
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (packet.IsEmpty || packet.Length > HardwareMtu) throw new ArgumentException("I2P frame exceeds its raw MTU.", nameof(packet));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _writes.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            I2pSamConnection? connection;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_dispose is not null, this);
                connection = _connected ? _connection : null;
            }
            if (connection is null) return; // Upstream discards offline sends.
            var frame = HdlcPacketFraming.Encode(packet, HardwareMtu, validatePacket: false);
            try
            {
                await connection.Stream.WriteAsync(frame, linked.Token).ConfigureAwait(false);
                Interlocked.Add(ref _tx, frame.Length);
                if (!IsInitiator) _parent.RecordTraffic(true, frame.Length);
                Interlocked.Exchange(ref _lastWrite, Environment.TickCount64);
            }
            catch
            {
                // A cancelled partial frame must never be followed by another frame on this stream.
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _writes.Release(); }
    }

    private void Notify(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error) { _parent.Report(error); }
        }
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["peer"] = Destination, ["status"] = IsConnected, ["initiator"] = IsInitiator,
            ["rxb"] = Interlocked.Read(ref _rx), ["txb"] = Interlocked.Read(ref _tx),
            ["bitrate"] = Bitrate, ["hardware_mtu"] = HardwareMtu, ["i2p_tunneled"] = true,
            ["i2p_tunnel_state"] = !IsConnected ? "init" :
                Environment.TickCount64 - Interlocked.Read(ref _lastRead) > _options.StaleAfter.TotalMilliseconds ? "stale" : "active",
            ["reconnect_attempts"] = Volatile.Read(ref _reconnects),
            ["setup_phase"] = _phase, ["phase_elapsed_ms"] = Environment.TickCount64 - _phaseStarted,
            ["setup_timeout_ms"] = _options.SetupTimeout.TotalMilliseconds,
            ["session_attempts"] = Volatile.Read(ref _sessionAttempts),
            ["session_id"] = _sessionId, ["local_i2p_b32"] = _localBase32,
            ["stream_connect_attempts"] = Volatile.Read(ref _streamAttempts),
            ["last_failure_phase"] = _lastFailurePhase, ["last_error"] = _lastError,
        };
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_dispose ??= Task.Run(DisposeCoreAsync));
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        I2pSamConnection? connection;
        Task? worker;
        lock (_gate) { connection = _connection; worker = _worker; }
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        if (worker is not null) await JoinAsync(worker, _lifetime.Token).ConfigureAwait(false);
        _received.Writer.TryComplete();
    }
}
