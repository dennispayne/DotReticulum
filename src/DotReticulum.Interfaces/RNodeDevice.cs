using System.Buffers.Binary;
using System.IO.Ports;
using System.Text;

namespace DotReticulum.Interfaces;

internal sealed class RNodeDevice
{
    internal sealed record Connection(Stream Stream, IDisposable? Owner = null, bool Tcp = false);
    private sealed class Echo
    {
        public long? Frequency;
        public long? Bandwidth;
        public int? Power;
        public int? Sf;
        public int? Cr;
        public int? State;
        public TaskCompletionSource Received { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset()
        {
            Frequency = Bandwidth = null;
            Power = Sf = Cr = State = null;
            Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class Session
    {
        public Session(Connection connection, CancellationToken parentToken)
        {
            Connection = connection;
            Stop = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            Token = Stop.Token;
        }
        public Connection Connection { get; }
        public CancellationTokenSource Stop { get; }
        public CancellationToken Token { get; }
        public Task Reader { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Detected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<int, Echo> Echoes { get; } = [];
        public Dictionary<int, byte> Types { get; } = [];
        public int Selected;
        public int? Major;
        public int? Minor;
        public int? Platform;
        public int? Mcu;
        public bool DetectResponse;
        public bool Online;
        public long LastWrite;
        public bool Closing;
        public object Gate { get; } = new();
        public void Cancel()
        {
            lock (Gate)
                try { Stop.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private readonly RNodeDeviceOptions _options;
    private readonly Func<CancellationToken, ValueTask<Connection>> _open;
    private readonly Stream? _provided;
    private readonly bool _canReconnect;
    private readonly bool _multi;
    private readonly SemaphoreSlim _wire = new(1, 1);
    private readonly SemaphoreSlim _framebufferRead = new(1, 1);
    private readonly SemaphoreSlim _displayRead = new(1, 1);
    private readonly SemaphoreSlim _configure = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Session? _session;
    private Task? _start;
    private Task? _worker;
    private Task? _dispose;
    private volatile bool _disposed;
    private long _reconnects;
    private long _malformed;
    private long _observerErrors;
    private int _lastHardwareError;
    private string? _lastError;
    private DateTimeOffset? _firstTransmit;
    private TaskCompletionSource<byte[]>? _framebufferResponse;
    private TaskCompletionSource<byte[]>? _displayResponse;
    private byte[]? _framebuffer;
    private byte[]? _display;
    private DateTimeOffset? _framebufferRequested;
    private DateTimeOffset? _displayRequested;
    private double? _framebufferLatency;
    private double? _displayLatency;

    internal RNodeDevice(Func<CancellationToken, ValueTask<Connection>> open, RNodeDeviceOptions options,
        bool multi, bool canReconnect, Stream? provided = null)
    {
        if (OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException("The pinned desktop RNode interface excludes Android; its platform-specific adapter is separate.");
        _open = open;
        _options = options;
        _multi = multi;
        _canReconnect = canReconnect && options.Reconnect;
        _provided = provided;
        Radios = multi
            ? ((RNodeMultiOptions)options).ValidateMulti().Select(c =>
                new RNodeSubinterface(this, c.Name, c.VirtualPort, c, c.Outgoing, options)).ToArray()
            : [new RNodeSubinterface(this, "radio", 0, ((RNodeOptions)options).Radio, true, options)];
        if (!multi) ((RNodeOptions)options).ValidateSingle();
    }

    internal IReadOnlyList<RNodeSubinterface> Radios { get; }
    internal bool IsDisposed => _disposed;
    internal long ParentBitrate => _options.ConfiguredBitrate ?? 0;
    internal bool DisplayAvailable { get { lock (_gate) return _session?.Platform is 0x80 or 0x70; } }
    internal ReadOnlyMemory<byte> LastFramebuffer { get { lock (_gate) return _framebuffer?.ToArray() ?? []; } }
    internal ReadOnlyMemory<byte> LastDisplay { get { lock (_gate) return _display?.ToArray() ?? []; } }
    internal CancellationToken StopToken => _stop.Token;
    internal CancellationToken CurrentSessionToken
    {
        get { lock (_gate) return _session?.Token ?? new CancellationToken(canceled: true); }
    }
    internal event Action? Connected;
    internal event Action? Disconnected;
    internal event Action<Exception>? Faulted;

    internal static Func<CancellationToken, ValueTask<Connection>> StreamFactory(Func<CancellationToken, ValueTask<Stream>> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        return async token => new(await open(token).ConfigureAwait(false));
    }

    internal static Func<CancellationToken, ValueTask<Connection>> SerialFactory(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (portName.Contains("://", StringComparison.Ordinal))
            throw new NotSupportedException("Use an explicit duplex stream factory for network adapters. BLE is unsupported.");
        return token =>
        {
            token.ThrowIfCancellationRequested();
            var port = NativeSerialPort.Create(portName, 115200, Parity.None, 8, StopBits.One, 100, 1000);
            try
            {
                port.Open();
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new Connection(port.BaseStream, port));
            }
            catch { port.Dispose(); throw; }
        };
    }

    internal ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_start is not null) throw new InvalidOperationException("The interface has already been started.");
            _start = StartCoreAsync(cancellationToken);
            return new(_start);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        Session? session = null;
        try
        {
            session = await OpenAsync(linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                _session = session;
                session.Online = true;
                _worker = RunAsync(session);
            }
            if (!_multi && _options.ConfiguredBitrate is { } configuredBitrate) Radios[0].SetConfiguredBitrate(configuredBitrate);
            foreach (var radio in Radios) radio.SetConnected(true);
            Notify(Connected);
        }
        catch (Exception exception)
        {
            if (session is not null) await CloseAsync(session).ConfigureAwait(false);
            Report(exception);
            foreach (var radio in Radios) radio.Complete(exception);
            throw;
        }
    }

    private async Task<Session> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await _open(cancellationToken).ConfigureAwait(false);
        var session = new Session(connection, _stop.Token);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!connection.Stream.CanRead || !connection.Stream.CanWrite)
                throw new IOException("RNode requires a duplex stream.");
            foreach (var radio in Radios) session.Echoes.Add(radio.VirtualPort, new());
            await Task.Delay(_options.DeviceSettleDelay, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            session.Reader = ReadAsync(session);
            await WriteAsync(session, RNodeProtocol.Detect(_multi), cancellationToken).ConfigureAwait(false);
            await ResponseAsync(session, session.Detected.Task, cancellationToken).ConfigureAwait(false);
            foreach (var radio in Radios)
            {
                if (_multi)
                {
                    var type = session.Types[radio.VirtualPort];
                    var high = type is 0x20 or 0x21;
                    if (high ? radio.Options.Frequency is < 2_200_000_000 or > 2_600_000_000
                        : radio.Options.Frequency is < 137_000_000 or > 1_000_000_000)
                        throw new ArgumentOutOfRangeException(nameof(radio.Options.Frequency), "Frequency is outside the detected radio family's range.");
                    radio.Statistic("radio_type", high ? "SX128X" : type is 0x10 or 0x11 ? "SX126X" : "SX127X");
                }
                foreach (var frame in RNodeProtocol.Configuration(radio.Options, _multi ? radio.VirtualPort : null))
                    await WriteAsync(session, frame, cancellationToken).ConfigureAwait(false);
                var echo = session.Echoes[radio.VirtualPort];
                await ResponseAsync(session, echo.Received.Task, cancellationToken).ConfigureAwait(false);
                if (Math.Abs(radio.Options.Frequency - echo.Frequency!.Value) > 100 ||
                    radio.Options.Bandwidth != echo.Bandwidth || radio.Options.TxPower != echo.Power ||
                    radio.Options.SpreadingFactor != echo.Sf || echo.State != 1)
                    throw new IOException($"Radio {radio.VirtualPort} configuration echo does not match the requested parameters.");
            }
            return session;
        }
        catch
        {
            await CloseAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ResponseAsync(Session session, Task response, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(response, session.Reader).WaitAsync(
            session.Connection.Tcp ? ((RNodeOptions)_options).Tcp.DetectionTimeout : _options.ResponseTimeout,
            _options.TimeProvider, cancellationToken).ConfigureAwait(false);
        if (completed == session.Reader)
        {
            await session.Reader.ConfigureAwait(false);
            throw new EndOfStreamException("RNode disconnected during configuration.");
        }
        await response.ConfigureAwait(false);
    }

    private async Task RunAsync(Session session)
    {
        await Task.Yield();
        while (!_stop.IsCancellationRequested)
        {
            using var beaconStop = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            var beacon = BeaconAsync(session, beaconStop.Token);
            try { await session.Reader.ConfigureAwait(false); }
            catch (Exception exception) when (!_stop.IsCancellationRequested) { Report(exception); }
            finally
            {
                beaconStop.Cancel();
                try { await beacon.ConfigureAwait(false); }
                catch (OperationCanceledException) when (beaconStop.IsCancellationRequested) { }
                catch (Exception exception) { Report(exception); }
            }
            if (_stop.IsCancellationRequested) break;
            session.Online = false;
            session.Cancel();
            foreach (var radio in Radios) radio.SetConnected(false);
            Notify(Disconnected);
            await CloseAsync(session).ConfigureAwait(false);
            lock (_gate) if (ReferenceEquals(_session, session)) _session = null;
            if (!_canReconnect)
            {
                foreach (var radio in Radios) radio.Complete(new IOException(_lastError ?? "RNode disconnected."));
                return;
            }
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_options.ReconnectDelay, _options.TimeProvider, _stop.Token).ConfigureAwait(false);
                    var reopened = await OpenAsync(_stop.Token).ConfigureAwait(false);
                    if (_stop.IsCancellationRequested)
                    {
                        await CloseAsync(reopened).ConfigureAwait(false);
                        return;
                    }
                    session = reopened;
                    lock (_gate)
                    {
                        _stop.Token.ThrowIfCancellationRequested();
                        _session = session;
                        session.Online = true;
                    }
                    Interlocked.Increment(ref _reconnects);
                    lock (_gate) _firstTransmit = null;
                    foreach (var radio in Radios) radio.SetConnected(true);
                    Notify(Connected);
                    break;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception exception) { Report(exception); }
            }
        }
    }

    private async Task BeaconAsync(Session session, CancellationToken cancellationToken)
    {
        var interval = _options.IdInterval;
        var payload = _options.IdCallsign is { } callsign ? Encoding.UTF8.GetBytes(callsign) : [];
        if ((!interval.HasValue || payload.Length == 0) && !session.Connection.Tcp) return;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            if (session.Connection.Tcp && session.Online &&
                _options.TimeProvider.GetElapsedTime(Interlocked.Read(ref session.LastWrite)) > ((RNodeOptions)_options).Tcp.ActivityKeepalive)
                await WriteAsync(session, RNodeProtocol.Detect(), cancellationToken).ConfigureAwait(false);
            DateTimeOffset? first;
            lock (_gate) first = _firstTransmit;
            if (interval is not { } beaconInterval || payload.Length == 0 ||
                first is not { } sentAt || _options.TimeProvider.GetUtcNow() <= sentAt + beaconInterval || !session.Online) continue;
            await Task.WhenAll(Radios.Where(r => r.IsConnected)
                .Select(r => BeaconRadioAsync(r, payload, cancellationToken))).ConfigureAwait(false);
        }
    }

    private static async Task BeaconRadioAsync(RNodeSubinterface radio, byte[] payload, CancellationToken cancellationToken)
    {
        try { await radio.SendAsync(payload, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !radio.IsConnected) { }
        catch (IOException) when (!cancellationToken.IsCancellationRequested && !radio.IsConnected) { }
    }

    private async Task ReadAsync(Session session)
    {
        var decoder = new RNodeFrameDecoder();
        var buffer = new byte[2048];
        var token = session.Token;
        Task<int>? pending = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                pending ??= session.Connection.Stream.ReadAsync(buffer, token).AsTask();
                if (decoder.HasPartialFrame)
                {
                    using var idleStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var idle = Task.Delay(session.Connection.Tcp ? ((RNodeOptions)_options).Tcp.PartialFrameTimeout
                        : _options.PartialFrameTimeout, _options.TimeProvider, idleStop.Token);
                    if (await Task.WhenAny(pending, idle).ConfigureAwait(false) != pending)
                    {
                        token.ThrowIfCancellationRequested();
                        decoder.Expire();
                        Interlocked.Increment(ref _malformed);
                        continue;
                    }
                    idleStop.Cancel();
                }
                var count = await pending.ConfigureAwait(false);
                pending = null;
                if (count == 0) throw new EndOfStreamException("RNode stream closed.");
                var droppedBefore = decoder.DroppedFrames;
                foreach (var frame in decoder.Feed(buffer.AsSpan(0, count))) Process(session, frame);
                Interlocked.Add(ref _malformed, decoder.DroppedFrames - droppedBefore);
            }
        }
        finally
        {
            if (pending is not null)
            {
                session.Cancel();
                try { await pending.ConfigureAwait(false); }
                catch (Exception) when (session.Token.IsCancellationRequested) { }
            }
        }
    }

    private void Process(Session session, RNodeFrame frame)
    {
        var p = frame.Payload;
        var command = frame.Command;
        if (command == 0x08 && p.Length == 1) session.DetectResponse = p[0] == 0x46;
        else if (command == 0x50 && p.Length == 2)
        {
            session.Major = p[0];
            session.Minor = p[1];
            if (p[0] < 1 || p[0] == 1 && p[1] < (_multi ? 74 : 52))
                throw new IOException($"Unsupported RNode firmware {p[0]}.{p[1]}.");
        }
        else if (command == 0x48 && p.Length == 1) session.Platform = p[0];
        else if (command == 0x49 && p.Length == 1) session.Mcu = p[0];
        else if (_multi && command == 0x71 && p.Length % 2 == 0)
        {
            for (var i = 0; i < p.Length; i += 2)
                if (p[i] <= 10) session.Types[p[i]] = p[i + 1];
        }
        else if (_multi && command == 0x1f && p.Length == 1)
        {
            session.Selected = p[0];
            return;
        }
        else if (command == 0x55 && p.Length == 1 && p[0] == 0xf8 && session.Platform == 0x80 && session.Online)
            throw new IOException("RNode ESP32 reset while online.");
        else if (command == 0x90 && p.Length == 1)
        {
            Volatile.Write(ref _lastHardwareError, p[0]);
            var error = new IOException($"RNode hardware error 0x{p[0]:x2}.");
            if (!_multi && p[0] is 5 or 6) Report(error);
            else throw error;
        }
        else if (command == 0x0f && p.Length == 1)
        {
            foreach (var endpoint in Radios) endpoint.Ready();
            return;
        }
        else if (!_multi && command == 0x42 && p.Length == 512)
        {
            lock (_gate)
            {
                _framebuffer = p;
                _framebufferLatency = _framebufferRequested is { } sent ? (_options.TimeProvider.GetUtcNow() - sent).TotalSeconds : null;
                _framebufferResponse?.TrySetResult(p);
            }
            return;
        }
        else if (!_multi && command == 0x66 && p.Length == 1024)
        {
            lock (_gate)
            {
                _display = p;
                _displayLatency = _displayRequested is { } sent ? (_options.TimeProvider.GetUtcNow() - sent).TotalSeconds : null;
                _displayResponse?.TrySetResult(p);
            }
            return;
        }
        else
        {
            var radio = Radios.FirstOrDefault(r => r.VirtualPort == (_multi ? session.Selected : 0));
            if (radio is null) { Interlocked.Increment(ref _malformed); return; }
            if (command == 0)
            {
                radio.Incoming(p);
                return;
            }
            lock (session.Gate)
            {
                var echo = session.Echoes[radio.VirtualPort];
                switch (command)
                {
                    case 1 when p.Length == 4: echo.Frequency = BinaryPrimitives.ReadUInt32BigEndian(p); radio.Statistic("frequency", echo.Frequency); break;
                    case 2 when p.Length == 4: echo.Bandwidth = BinaryPrimitives.ReadUInt32BigEndian(p); radio.Statistic("bandwidth", echo.Bandwidth); break;
                    case 3 when p.Length == 1: echo.Power = _multi ? unchecked((sbyte)p[0]) : p[0]; radio.Statistic("txpower", echo.Power); break;
                    case 4 when p.Length == 1: echo.Sf = p[0]; radio.Statistic("spreadingfactor", echo.Sf); break;
                    case 5 when p.Length == 1: echo.Cr = p[0]; radio.Statistic("codingrate", echo.Cr); break;
                    case 6 when p.Length == 1: echo.State = p[0]; radio.Statistic("radio_state", echo.State); break;
                    case 7 when p.Length == 1: radio.Statistic("radio_lock", p[0]); break;
                    default: Telemetry(radio, echo, command, p); break;
                }
                if ((command is 1 or 2 && p.Length == 4) || (command is 4 or 5 && p.Length == 1))
                    radio.UpdateBitrate(echo.Sf ?? 0, echo.Cr ?? 0, echo.Bandwidth ?? 0);
                if (echo.Frequency.HasValue && echo.Bandwidth.HasValue && echo.Power.HasValue &&
                    echo.Sf.HasValue && echo.Cr.HasValue && echo.State.HasValue) echo.Received.TrySetResult();
            }
        }
        if (session.DetectResponse && session.Major.HasValue && session.Platform.HasValue && session.Mcu.HasValue &&
            (!_multi || Radios.All(r => session.Types.ContainsKey(r.VirtualPort))))
            session.Detected.TrySetResult();
    }

    private void Telemetry(RNodeSubinterface radio, Echo echo, byte command, byte[] p)
    {
        static int U16(byte[] data, int index) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index, 2));
        switch (command)
        {
            case 0x21 when p.Length == 4: radio.Statistic("radio_rx", BinaryPrimitives.ReadUInt32BigEndian(p)); break;
            case 0x22 when p.Length == 4: radio.Statistic("radio_tx", BinaryPrimitives.ReadUInt32BigEndian(p)); break;
            case 0x23 when p.Length == 1: radio.Statistic("rssi", p[0] - 157); break;
            case 0x24 when p.Length == 1:
                var snr = unchecked((sbyte)p[0]) * 0.25;
                radio.Statistic("snr", snr);
                if (echo.Sf is { } sf)
                {
                    var minimum = -9 - (sf - 7) * 2;
                    radio.Statistic("quality", Math.Clamp(Math.Round((snr - minimum) / (6 - minimum) * 100, 1), 0, 100));
                }
                break;
            case 0x0b when p.Length == 2: radio.Statistic("airtime_limit_short", U16(p, 0) / 100.0); break;
            case 0x0c when p.Length == 2: radio.Statistic("airtime_limit_long", U16(p, 0) / 100.0); break;
            case 0x25 when p.Length == (_multi ? 8 : 11):
                radio.Statistic("airtime_short", U16(p, 0) / 100.0);
                radio.Statistic("airtime_long", U16(p, 2) / 100.0);
                radio.Statistic("channel_load_short", U16(p, 4) / 100.0);
                radio.Statistic("channel_load_long", U16(p, 6) / 100.0);
                if (!_multi)
                {
                    radio.Statistic("current_rssi", p[8] - 157);
                    radio.Statistic("noise_floor", p[9] - 157);
                    radio.Statistic("interference", p[10] == 255 ? null : p[10] - 157);
                }
                break;
            case 0x26 when p.Length == (_multi ? 10 : 12):
                radio.Statistic("symbol_time_ms", U16(p, 0) / 1000.0);
                radio.Statistic("symbol_rate", U16(p, 2));
                radio.Statistic("preamble_symbols", U16(p, 4));
                radio.Statistic("preamble_time_ms", U16(p, 6));
                radio.Statistic("csma_slot_time_ms", U16(p, 8));
                if (!_multi) radio.Statistic("csma_difs_ms", U16(p, 10));
                break;
            case 0x28 when !_multi && p.Length == 3:
                radio.Statistic("csma_cw_band", p[0]);
                radio.Statistic("csma_cw_min", p[1]);
                radio.Statistic("csma_cw_max", p[2]);
                break;
            case 0x27 when !_multi && p.Length == 2:
                radio.Statistic("battery_state", p[0]);
                radio.Statistic("battery_percent", Math.Min(100, (int)p[1]));
                break;
            case 0x29 when !_multi && p.Length == 1:
                var temperature = p[0] - 120;
                radio.Statistic("temperature", temperature is >= -30 and <= 90 ? temperature : null);
                break;
            case 0x40 when p.Length == 1: radio.Statistic("random", p[0]); break;
        }
    }

    internal async ValueTask SendAsync(RNodeSubinterface radio, byte[] packet, CancellationToken cancellationToken)
    {
        Session session;
        lock (_gate)
            session = _session is { Online: true } current ? current : throw new IOException("RNode is offline.");
        try
        {
            await WriteAsync(session, _multi ? RNodeProtocol.SelectedFrame(radio.VirtualPort, 0, packet)
                : RNodeProtocol.Frame(0, packet), cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_options is { IdCallsign: { } callsign } &&
                    packet.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(callsign)))
                    _firstTransmit = null;
                else _firstTransmit ??= _options.TimeProvider.GetUtcNow();
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Report(exception);
            session.Cancel();
            session.Connection.Owner?.Dispose();
            await session.Connection.Stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask ControlAsync(byte command, byte[] payload, bool requireDisplay, CancellationToken cancellationToken)
    {
        Session session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session = _session is { Online: true } current ? current : throw new IOException("RNode is offline.");
        }
        if (requireDisplay && session.Platform is not (0x80 or 0x70)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token, _stop.Token);
        await WriteAsync(session, RNodeProtocol.Frame(command, payload), linked.Token).ConfigureAwait(false);
    }

    internal async ValueTask RefreshDeviceInformationAsync(CancellationToken cancellationToken)
    {
        var session = GetOnlineSession();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token, _stop.Token);
        await WriteAsync(session, RNodeProtocol.Detect(_multi), linked.Token).ConfigureAwait(false);
    }

    private Session GetOnlineSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _session is { Online: true } session ? session : throw new IOException("RNode is offline.");
        }
    }

    internal async ValueTask RadioStateAsync(RNodeSubinterface radio, bool enabled, CancellationToken cancellationToken)
    {
        var session = GetOnlineSession();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token, _stop.Token);
        var payload = new byte[] { enabled ? (byte)1 : (byte)0 };
        await WriteAsync(session, _multi ? RNodeProtocol.SelectedFrame(radio.VirtualPort, 6, payload)
            : RNodeProtocol.Frame(6, payload), linked.Token).ConfigureAwait(false);
    }

    internal async ValueTask ConfigureRadioAsync(RNodeSubinterface radio, RNodeRadioOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(_multi);
        if (options.FlowControl != radio.Options.FlowControl)
            throw new ArgumentException("Flow-control policy is fixed for the endpoint lifetime.", nameof(options));
        var session = GetOnlineSession();
        if (_multi)
        {
            var high = session.Types[radio.VirtualPort] is 0x20 or 0x21;
            if (high ? options.Frequency is < 2_200_000_000 or > 2_600_000_000
                : options.Frequency is < 137_000_000 or > 1_000_000_000)
                throw new ArgumentOutOfRangeException(nameof(options.Frequency));
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token, _stop.Token);
        await _configure.WaitAsync(linked.Token).ConfigureAwait(false);
        var previous = radio.Options;
        var echo = session.Echoes[radio.VirtualPort];
        try
        {
            lock (session.Gate) echo.Reset();
            radio.SetOptions(options);
            foreach (var frame in RNodeProtocol.Configuration(options, _multi ? radio.VirtualPort : null))
                await WriteAsync(session, frame, linked.Token).ConfigureAwait(false);
            await ResponseAsync(session, echo.Received.Task, linked.Token).ConfigureAwait(false);
            if (Math.Abs(options.Frequency - echo.Frequency!.Value) > 100 || options.Bandwidth != echo.Bandwidth ||
                options.TxPower != echo.Power || options.SpreadingFactor != echo.Sf || echo.State != 1)
                throw new IOException("RNode live configuration echo did not match requested parameters.");
        }
        catch
        {
            radio.SetOptions(previous);
            session.Cancel();
            await session.Connection.Stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { _configure.Release(); }
    }

    internal ValueTask ExternalFramebufferAsync(bool enabled, CancellationToken cancellationToken) =>
        ControlAsync(0x41, [enabled ? (byte)1 : (byte)0], requireDisplay: true, cancellationToken);

    internal ValueTask WriteFramebufferLineAsync(int line, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (line is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(line));
        if (data.Length > 1023) throw new ArgumentOutOfRangeException(nameof(data));
        return ControlAsync(0x43, [(byte)line, .. data.Span], requireDisplay: true, cancellationToken);
    }

    internal async ValueTask DisplayImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        if (image.Length > 2048) throw new ArgumentOutOfRangeException(nameof(image));
        for (var line = 0; line < image.Length / 8; line++)
            await WriteFramebufferLineAsync(line, image.Slice(line * 8, 8), cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ReadOnlyMemory<byte>> ReadDisplayAsync(bool framebuffer, CancellationToken cancellationToken)
    {
        if (_multi) throw new NotSupportedException("The pinned multi-radio interface has no display read API.");
        var order = framebuffer ? _framebufferRead : _displayRead;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await order.WaitAsync(linked.Token).ConfigureAwait(false);
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Session session;
            lock (_gate)
            {
                session = _session is { Online: true } current ? current : throw new IOException("RNode is offline.");
                if (framebuffer)
                {
                    _framebufferResponse = response;
                    _framebufferRequested = _options.TimeProvider.GetUtcNow();
                }
                else
                {
                    _displayResponse = response;
                    _displayRequested = _options.TimeProvider.GetUtcNow();
                }
            }
            await ControlAsync(framebuffer ? (byte)0x42 : (byte)0x66, [1], requireDisplay: false, linked.Token).ConfigureAwait(false);
            await ResponseAsync(session, response.Task, linked.Token).ConfigureAwait(false);
            return await response.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (framebuffer && ReferenceEquals(_framebufferResponse, response)) _framebufferResponse = null;
                if (!framebuffer && ReferenceEquals(_displayResponse, response)) _displayResponse = null;
            }
            order.Release();
        }
    }

    internal async ValueTask HardResetAsync(CancellationToken cancellationToken)
    {
        await ControlAsync(0x55, [0xf8], requireDisplay: false, cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(2250), _options.TimeProvider, linked.Token).ConfigureAwait(false);
    }

    internal async Task DisplayDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await Task.Delay(delay, _options.TimeProvider, linked.Token).ConfigureAwait(false);
    }

    private async Task WriteAsync(Session session, byte[] frame, CancellationToken cancellationToken)
    {
        await _wire.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await session.Connection.Stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await session.Connection.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref session.LastWrite, _options.TimeProvider.GetTimestamp());
            }
            catch when (session.Online)
            {
                session.Cancel();
                session.Connection.Owner?.Dispose();
                await session.Connection.Stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _wire.Release(); }
    }

    private async Task CloseAsync(Session session)
    {
        lock (session.Gate)
        {
            if (session.Closing) return;
            session.Closing = true;
        }
        session.Online = false;
        session.Cancel();
        try { session.Connection.Owner?.Dispose(); }
        catch (Exception exception) { Report(exception); }
        try { await session.Connection.Stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { Report(exception); }
        try { await session.Reader.ConfigureAwait(false); }
        catch (Exception) when (session.Token.IsCancellationRequested) { }
        lock (session.Gate) session.Stop.Dispose();
    }

    internal void ReportObserver(Exception exception)
    {
        Interlocked.Increment(ref _observerErrors);
        lock (_gate) _lastError = $"RNode observer failed: {exception.Message}";
    }

    private void Report(Exception exception)
    {
        lock (_gate) _lastError = exception.Message;
        foreach (var radio in Radios) radio.Report(exception);
        if (Faulted is not { } observers) return;
        foreach (Action<Exception> observer in observers.GetInvocationList())
            try { observer(exception); }
            catch (Exception error) { ReportObserver(error); }
    }

    private void Notify(Action? observers)
    {
        if (observers is null) return;
        foreach (Action observer in observers.GetInvocationList())
            try { observer(); }
            catch (Exception error) { ReportObserver(error); }
    }

    internal IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate)
            return new Dictionary<string, object?>
            {
                ["status"] = _session is { Online: true } && !_disposed,
                ["firmware"] = _session is { } s ? $"{s.Major}.{s.Minor}" : null,
                ["platform"] = _session?.Platform, ["mcu"] = _session?.Mcu,
                ["reconnections"] = Interlocked.Read(ref _reconnects), ["last_error"] = _lastError,
                ["malformed_frames"] = Interlocked.Read(ref _malformed),
                ["observer_errors"] = Interlocked.Read(ref _observerErrors),
                ["hardware_error"] = Volatile.Read(ref _lastHardwareError),
                ["children"] = Radios.Count, ["hardware_mtu"] = RNodeProtocol.HardwareMtu,
                ["bitrate"] = _multi ? ParentBitrate : Radios[0].Bitrate,
                ["display_available"] = _session?.Platform is 0x80 or 0x70,
                ["framebuffer_latency"] = _framebufferLatency, ["display_latency"] = _displayLatency,
            };
    }

    internal ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true;
            _dispose = DisposeCoreAsync();
            return new(_dispose);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        Session? session;
        lock (_gate) session = _session;
        if (session is { Online: true })
        {
            using var timeout = new CancellationTokenSource(_options.ShutdownTimeout, _options.TimeProvider);
            try
            {
                if (session.Platform is 0x80 or 0x70)
                    await WriteAsync(session, RNodeProtocol.Frame(0x41, [0]), timeout.Token).ConfigureAwait(false);
                foreach (var radio in Radios)
                    await WriteAsync(session, _multi ? RNodeProtocol.SelectedFrame(radio.VirtualPort, 6, [0])
                        : RNodeProtocol.Frame(6, [0]), timeout.Token).ConfigureAwait(false);
                await WriteAsync(session, RNodeProtocol.Frame(0x0a, [0xff]), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) { Report(exception); }
        }
        _stop.Cancel();
        if (session is not null) await CloseAsync(session).ConfigureAwait(false);
        if (_start is not null)
            try { await _start.ConfigureAwait(false); } catch (Exception) when (_stop.IsCancellationRequested) { }
        if (_worker is not null)
            try { await _worker.ConfigureAwait(false); } catch (Exception) when (_stop.IsCancellationRequested) { }
        lock (_gate) session = _session;
        if (session is not null) await CloseAsync(session).ConfigureAwait(false);
        else if (_provided is not null) await _provided.DisposeAsync().ConfigureAwait(false);
        foreach (var radio in Radios)
        {
            radio.SetConnected(false, disposing: true);
            await radio.DisposeAsync().ConfigureAwait(false);
        }
    }
}
