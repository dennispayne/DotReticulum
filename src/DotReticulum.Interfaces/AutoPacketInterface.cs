using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>IPv6 multicast discovery with independently routed, unicast UDP peer interfaces.</summary>
public sealed class AutoPacketInterface : ISpawningPacketInterface, IPacketInterfaceDiagnostics
{
    public const int FixedHardwareMtu = 1196;
    public const int MaximumFrameSize = FixedHardwareMtu + 64;
    public const int DefaultIfacSize = 16;
    private readonly AutoPacketInterfaceOptions _options;
    private readonly IAutoPacketNetwork _network;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _warmup;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<IPAddress, AutoPacketPeer> _peers = [];
    private readonly Queue<(byte[] Hash, DateTimeOffset Time)> _duplicates = new(48);
    private readonly Channel<ReadOnlyMemory<byte>> _parentFrames = Channel.CreateBounded<ReadOnlyMemory<byte>>(1);
    private Task? _jobs;
    private Task? _shutdown;
    private TaskCompletionSource? _startCompletion;
    private bool _started;
    private bool _online;
    private int _receiving;
    private long _receivedBytes;
    private long _sentBytes;
    private long _dropped;

    public AutoPacketInterface(AutoPacketInterfaceOptions? options = null)
        : this(options ?? new(), new AutoPacketNetwork(), TimeProvider.System, TimeSpan.FromSeconds(1.92)) { }

    internal AutoPacketInterface(AutoPacketInterfaceOptions options, IAutoPacketNetwork network, TimeProvider clock, TimeSpan warmup)
    {
        _options = options.ValidateAndCopy();
        _network = network;
        _clock = clock;
        _warmup = warmup;
    }

    public event Action<IPacketInterface>? ChildConnected;
    public bool SendsThroughChildren => true;
    public long Bitrate => _options.ConfiguredBitrate;
    public int HardwareMtu => FixedHardwareMtu;
    public bool SupportsMtuDiscovery => true;
    public IPAddress MulticastAddress => _options.GetMulticastAddress();
    public int PeerCount { get { lock (_gate) return _peers.Count; } }
    public bool IsOnline { get { lock (_gate) return _online; } }
    public TimeSpan PeeringTimeout => TimeSpan.FromSeconds(OperatingSystem.IsAndroid() ? 27.5 : 22);

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            if (_started) throw new InvalidOperationException("The AutoInterface has already been started.");
            _started = true;
            _startCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            foreach (var device in _network.GetDevices().DistinctBy(d => d.Name))
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!AutoPacketNetwork.IsAllowed(device, _options, OperatingSystem.IsMacOS(), OperatingSystem.IsAndroid())) continue;
                try
                {
                    var session = OpenSession(device);
                    lock (_gate) _sessions.Add(device.Name, session);
                    session.Run(this);
                }
                catch (Exception error) { Report(new IOException($"AutoInterface could not adopt device {device.Name}.", error)); }
            }
            if (_sessions.Count == 0) Report(new IOException("AutoInterface has no usable IPv6 link-local devices; no connectivity is available."));
            await Task.Delay(_warmup, _clock, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate) _online = _shutdown is null;
            _jobs = RunJobsAsync();
        }
        catch
        {
            _stop.Cancel();
            Session[] sessions;
            lock (_gate) { sessions = [.. _sessions.Values]; _sessions.Clear(); }
            foreach (var session in sessions) await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { _startCompletion!.TrySetResult(); }
    }

    private Session OpenSession(AutoPacketDevice device) => new(device, _network.Open(device, _options), _clock.GetUtcNow(), _stop.Token);

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        if (Interlocked.Exchange(ref _receiving, 1) != 0) throw new InvalidOperationException("ReceiveAsync supports a single enumerator.");
        return _parentFrames.Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>The parent is receive-only discovery infrastructure; the router sends through its children.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStarted();
        ValidateFrame(packet);
        return ValueTask.CompletedTask;
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            if (!_started || !_online) throw new InvalidOperationException("The AutoInterface has not completed startup.");
        }
    }

    internal void ProcessDiscovery(AutoPacketDevice device, IPAddress source, ReadOnlyMemory<byte> data)
    {
        source = AutoPacketNetwork.Unscope(source);
        if (source.AddressFamily != AddressFamily.InterNetworkV6 || !source.IsIPv6LinkLocal ||
            data.Length < 32 || !CryptographicOperations.FixedTimeEquals(data.Span[..32], _options.GetDiscoveryToken(source)))
        {
            Drop("Invalid AutoInterface discovery token or source address.");
            return;
        }
        AutoPacketPeer added;
        lock (_gate)
        {
            if (!_online || _shutdown is not null || !_sessions.ContainsKey(device.Name)) return;
            var now = _clock.GetUtcNow();
            var own = _sessions.Values.FirstOrDefault(s => s.Device.Address.Equals(source));
            if (own is not null)
            {
                own.LastEcho = now;
                own.EchoSeen = true;
                return;
            }
            if (_sessions.Values.Any(s => s.Device.LocalAddresses?.Contains(source) == true))
            {
                Report(new IOException($"AutoInterface received an echo for an unselected local address {source}."));
                return;
            }
            if (_peers.TryGetValue(source, out var peer)) { peer.LastHeard = now; return; }
            if (_peers.Count >= _options.MaximumPeers) { Drop("AutoInterface peer limit reached."); return; }
            added = new(this, source, device.Name, now, _options.ReceiveQueueCapacity);
            _peers.Add(source, added);
        }
        var observers = ChildConnected;
        if (observers is not null)
            foreach (Action<IPacketInterface> observer in observers.GetInvocationList())
                try { observer(added); }
                catch (Exception error) { Report(new IOException($"AutoInterface peer observer failed for {source}.", error)); }
    }

    internal void ProcessData(IPAddress source, ReadOnlyMemory<byte> data)
    {
        if (!IsValidFrame(data)) { Drop("AutoInterface discarded an empty or oversized data datagram."); return; }
        source = AutoPacketNetwork.Unscope(source);
        lock (_gate)
        {
            if (!_online || !_peers.TryGetValue(source, out var peer)) return;
            var now = _clock.GetUtcNow();
            var hash = SHA256.HashData(data.Span);
            if (_duplicates.Any(entry => now < entry.Time + TimeSpan.FromSeconds(.75) && hash.AsSpan().SequenceEqual(entry.Hash))) return;
            if (_duplicates.Count == 48) _duplicates.Dequeue();
            _duplicates.Enqueue((hash, now));
            peer.LastHeard = now;
            Interlocked.Add(ref _receivedBytes, data.Length);
            peer.Accept(data);
        }
    }

    private async Task ReceiveLoopAsync(Session session, bool discovery, bool multicast)
    {
        try
        {
            while (true)
            {
                AutoPacketDatagram datagram;
                try { datagram = await session.Socket.ReceiveAsync(discovery, multicast, session.Token).ConfigureAwait(false); }
                catch (SocketException error) when (error.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset)
                {
                    Drop($"AutoInterface discarded UDP input on {session.Device.Name}: {error.SocketErrorCode}.");
                    continue;
                }
                if (discovery) ProcessDiscovery(session.Device, datagram.Source, datagram.Data);
                else ProcessData(datagram.Source, datagram.Data);
            }
        }
        catch (Exception) when (session.Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            session.Failed = true;
            Report(new IOException($"AutoInterface receive failed on {session.Device.Name}; listener will be retried.", error));
        }
    }

    private async Task AnnounceLoopAsync(Session session)
    {
        try
        {
            while (true)
            {
                await AnnounceAsync(session, MulticastAddress, _options.DiscoveryPort, session.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1.6), _clock, session.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) when (session.Token.IsCancellationRequested) { }
        catch (Exception error) { Report(new IOException($"AutoInterface announce task failed on {session.Device.Name}.", error)); }
    }

    private async ValueTask AnnounceAsync(Session session, IPAddress target, int port, CancellationToken cancellationToken)
    {
        try
        {
            await session.Socket.SendAsync(_options.GetDiscoveryToken(session.Device.Address), target, port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { Report(new IOException($"AutoInterface discovery send failed on {session.Device.Name} to {target}.", error)); }
    }

    private async Task RunJobsAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), _clock, _stop.Token).ConfigureAwait(false);
                await RunMaintenanceAsync().ConfigureAwait(false);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Report(new IOException("AutoInterface maintenance failed.", error)); }
    }

    internal async ValueTask RunMaintenanceAsync()
    {
        var now = _clock.GetUtcNow();
        List<(Session Session, IPAddress Address)> reverse = [];
        List<AutoPacketPeer> expired = [];
        Session[] sessions;
        lock (_gate)
        {
            if (!_online || _shutdown is not null) return;
            foreach (var peer in _peers.Values.ToArray())
            {
                if (now > peer.LastHeard + PeeringTimeout)
                {
                    _peers.Remove(peer.Address);
                    peer.Close();
                    expired.Add(peer);
                }
                else if (now > peer.LastOutbound + TimeSpan.FromSeconds(5.2) && _sessions.TryGetValue(peer.DeviceName, out var session))
                {
                    peer.LastOutbound = now;
                    reverse.Add((session, peer.Address));
                }
            }
            sessions = [.. _sessions.Values];
            foreach (var session in sessions)
            {
                var timedOut = now > session.LastEcho + TimeSpan.FromSeconds(6.5);
                if (session.CarrierLost != timedOut)
                {
                    session.CarrierLost = timedOut;
                    Report(new IOException($"AutoInterface multicast carrier {(timedOut ? "lost" : "recovered")} on {session.Device.Name}."));
                }
                if (!session.EchoSeen && !session.EchoWarningLogged)
                {
                    session.EchoWarningLogged = true;
                    Report(new IOException($"AutoInterface has received no multicast echo on {session.Device.Name}; hardware or a firewall may block multicast."));
                }
            }
        }
        foreach (var peer in expired) await peer.DisposeAsync().ConfigureAwait(false);
        foreach (var (session, address) in reverse)
            await AnnounceAsync(session, address, _options.DiscoveryPort + 1, _stop.Token).ConfigureAwait(false);

        IReadOnlyList<AutoPacketDevice> devices;
        try { devices = _network.GetDevices(); }
        catch (Exception error) { Report(new IOException("AutoInterface address refresh failed.", error)); return; }
        foreach (var session in sessions)
        {
            var updated = devices.LastOrDefault(d => d.Name == session.Device.Name);
            if (updated is null || updated.Address.Equals(session.Device.Address) &&
                updated.Index == session.Device.Index && !session.Failed) continue;
            await session.DisposeAsync().ConfigureAwait(false);
            try
            {
                _stop.Token.ThrowIfCancellationRequested();
                var replacement = OpenSession(updated);
                lock (_gate) _sessions[updated.Name] = replacement;
                replacement.Run(this);
                Report(new IOException($"AutoInterface listener replaced on {updated.Name}: {session.Device.Address} -> {updated.Address}."));
            }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                session.Failed = true;
                Report(new IOException($"AutoInterface could not replace listener on {session.Device.Name}; retrying on the next peer job.", error));
            }
        }
    }

    internal async ValueTask SendPeerAsync(AutoPacketPeer peer, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        ValidateFrame(packet);
        Session session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            if (!_peers.TryGetValue(peer.Address, out var current) || !ReferenceEquals(current, peer))
                throw new IOException("The AutoInterface peer is no longer attached.");
            session = _sessions[peer.DeviceName];
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
            await session.Socket.SendAsync(packet, peer.Address, _options.DataPort, linked.Token).ConfigureAwait(false);
            Interlocked.Add(ref _sentBytes, packet.Length);
            peer.RecordSent(packet.Length);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Report(new IOException($"AutoInterface unicast send failed to {peer.Address} on {peer.DeviceName}.", error));
            throw;
        }
    }

    internal void RemovePeer(AutoPacketPeer peer)
    {
        lock (_gate)
        {
            if (_peers.TryGetValue(peer.Address, out var current) && ReferenceEquals(peer, current)) _peers.Remove(peer.Address);
            peer.Close();
        }
    }

    private static bool IsValidFrame(ReadOnlyMemory<byte> packet) => !packet.IsEmpty &&
        packet.Length <= FixedHardwareMtu + ((packet.Span[0] & 0x80) != 0 ? 64 : 0);

    internal static void ValidateFrame(ReadOnlyMemory<byte> packet)
    {
        if (!IsValidFrame(packet)) throw new ArgumentException("Packet exceeds the AutoInterface frame bound.", nameof(packet));
    }

    internal void Drop(string message)
    {
        Interlocked.Increment(ref _dropped);
        Report(new IOException(message));
    }

    private void Report(Exception error)
    {
        try
        {
            if (_options.Diagnostic is { } diagnostic) diagnostic(error);
            else System.Diagnostics.Trace.TraceError("AutoInterface: {0}", error);
        }
        catch (Exception observer) { System.Diagnostics.Trace.TraceError("AutoInterface diagnostic observer failed: {0}; original: {1}", observer, error); }
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["rxb"] = Interlocked.Read(ref _receivedBytes),
            ["txb"] = Interlocked.Read(ref _sentBytes),
            ["bitrate"] = Bitrate,
            ["hardware_mtu"] = HardwareMtu,
            ["peers"] = _peers.Count,
            ["devices"] = _sessions.Count,
            ["multicast_address"] = MulticastAddress.ToString(),
            ["discovery_port"] = _options.DiscoveryPort,
            ["data_port"] = _options.DataPort,
            ["dropped_frames"] = Interlocked.Read(ref _dropped),
            ["carrier_lost_devices"] = _sessions.Values.Count(s => s.CarrierLost),
            ["multicast_echo_devices"] = _sessions.Values.Count(s => s.EchoSeen),
            ["status"] = _online && _shutdown is null,
            ["dotreticulum_bitrate_kind"] = "configured_estimate",
        };
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is not null) return new(_shutdown);
            _online = false;
            _stop.Cancel();
            _shutdown = Task.Run(ShutdownAsync);
            return new(_shutdown);
        }
    }

    private async Task ShutdownAsync()
    {
        if (_startCompletion is not null) await _startCompletion.Task.ConfigureAwait(false);
        if (_jobs is not null) await _jobs.ConfigureAwait(false);
        Session[] sessions;
        AutoPacketPeer[] peers;
        lock (_gate)
        {
            _online = false;
            sessions = [.. _sessions.Values];
            _sessions.Clear();
            peers = [.. _peers.Values];
            foreach (var peer in peers) peer.Close();
            _peers.Clear();
            _duplicates.Clear();
        }
        foreach (var peer in peers) await peer.DisposeAsync().ConfigureAwait(false);
        foreach (var session in sessions) await session.DisposeAsync().ConfigureAwait(false);
        _parentFrames.Writer.TryComplete();
        _stop.Dispose();
    }

    private sealed class Session
    {
        public AutoPacketDevice Device { get; }
        public IAutoPacketDevice Socket { get; }
        public CancellationTokenSource Stop { get; }
        public CancellationToken Token { get; }
        public DateTimeOffset LastEcho { get; set; }
        public bool EchoSeen { get; set; }
        public bool EchoWarningLogged { get; set; }
        public bool CarrierLost { get; set; }
        public volatile bool Failed;
        private Task[] _tasks = [];
        private readonly object _disposeGate = new();
        private Task? _disposeTask;
        public Session(AutoPacketDevice device, IAutoPacketDevice socket, DateTimeOffset now, CancellationToken stop)
        {
            Device = device;
            Socket = socket;
            LastEcho = now;
            Stop = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Token = Stop.Token;
        }
        public void Run(AutoPacketInterface owner)
        {
            _tasks =
            [
                owner.ReceiveLoopAsync(this, true, true),
                owner.ReceiveLoopAsync(this, true, false),
                owner.ReceiveLoopAsync(this, false, false),
                owner.AnnounceLoopAsync(this),
            ];
        }
        public ValueTask DisposeAsync()
        {
            lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
        }
        private async Task DisposeCoreAsync()
        {
            Stop.Cancel();
            await Socket.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(_tasks).ConfigureAwait(false);
            Stop.Dispose();
        }
    }
}
