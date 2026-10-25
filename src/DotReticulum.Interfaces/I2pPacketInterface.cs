using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>
/// Native I2P SAM 3.1 bearer. Owns sessions and exposes independent peer interfaces;
/// it does not launch I2P, proxy through Python, or broadcast parent sends.
/// </summary>
public sealed class I2pPacketInterface : ISpawningPacketInterface, IPacketInterfaceDiagnostics
{
    public const int MaximumFrameLength = 1064;
    public const long NominalBitrate = 256000;
    public const int DefaultIfacSize = 16;
    private readonly I2pPacketInterfaceOptions _options;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<I2pPacketPeer> _peers = [];
    private readonly Channel<ReadOnlyMemory<byte>> _empty = Channel.CreateBounded<ReadOnlyMemory<byte>>(1);
    private Task? _server;
    private Task? _dispose;
    private bool _started;
    private bool _online;
    private string? _base32;
    private string? _lastError;
    private long _rejected;
    private long _rx;
    private long _tx;
    private int _receiving;

    public I2pPacketInterface(I2pPacketInterfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Snapshot();
    }

    public event Action<IPacketInterface>? ChildConnected;
    /// <summary>Observed failures; private SAM replies and keys are never included.</summary>
    public event Action<Exception>? Diagnostic;
    public bool SendsThroughChildren => true;
    public long Bitrate => _options.ConfiguredBitrate is >= 5 ? _options.ConfiguredBitrate.Value : NominalBitrate;
    public int HardwareMtu => MaximumFrameLength;
    public bool SupportsMtuDiscovery => true;
    public string? Base32Address { get { lock (_gate) return _base32 is null ? null : _base32 + ".b32.i2p"; } }
    public IReadOnlyList<I2pPacketPeer> Peers { get { lock (_gate) return _peers.ToArray(); } }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        I2pPacketPeer[] peers;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            if (_started) throw new InvalidOperationException("The I2P interface has already been started.");
            _started = true;
            if (_options.Connectable) _server = Task.Run(ServerLoopAsync);
            peers = _options.Peers.Select(address => new I2pPacketPeer(this, _options, address, initiator: true)).ToArray();
            foreach (var peer in peers) _peers.Add(peer);
        }
        foreach (var peer in peers) { Publish(peer); peer.Begin(); }
        return ValueTask.CompletedTask;
    }

    private void Publish(I2pPacketPeer peer)
    {
        if (ChildConnected is not { } handlers) return;
        foreach (Action<IPacketInterface> handler in handlers.GetInvocationList())
        {
            try { handler(peer); }
            catch (Exception error) { Report(error); }
        }
    }

    internal void PeerEnded(I2pPacketPeer peer)
    {
        lock (_gate) _peers.Remove(peer);
    }

    internal void RecordTraffic(bool sent, int length)
    {
        if (sent) Interlocked.Add(ref _tx, length);
        else Interlocked.Add(ref _rx, length);
    }

    internal void Report(Exception error)
    {
        lock (_gate) _lastError = error.Message;
        Trace.TraceError("I2P interface {0}: {1}", _options.Name, error.Message);
        if (Diagnostic is not { } handlers) return;
        foreach (Action<Exception> handler in handlers.GetInvocationList())
        {
            try { handler(error); }
            catch (Exception observerError) { Trace.TraceError("I2P diagnostic observer failed: {0}", observerError.Message); }
        }
    }

    internal async Task<string> GenerateDestinationAsync(CancellationToken token)
    {
        await using var connection = await I2pSamConnection.OpenAsync(_options, token).ConfigureAwait(false);
        var reply = await connection.CommandAsync("DEST GENERATE SIGNATURE_TYPE=7\n", "DEST", "REPLY", token, requireResult: false).ConfigureAwait(false);
        if (!reply.TryGetValue("PRIV", out var destination)) throw new InvalidDataException("SAM destination reply is missing PRIV.");
        _ = I2pDestination.Base32FromPrivate(destination);
        return destination;
    }

    private async Task<string> LoadDestinationAsync(CancellationToken token)
    {
        var directory = Path.Combine(_options.StoragePath, "i2p");
        Directory.CreateDirectory(directory);
        var legacy = Path.Combine(directory, I2pDestination.KeyFileName(_options.Name, [], legacy: true));
        var path = File.Exists(legacy) ? legacy :
            Path.Combine(directory, I2pDestination.KeyFileName(_options.Name, _options.TransportIdentityHash.Span));
        if (File.Exists(path)) return await ReadDestinationAsync(path, token).ConfigureAwait(false);
        var destination = await GenerateDestinationAsync(token).ConfigureAwait(false);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            var settings = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(staging, settings))
            {
                using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync(destination.AsMemory(), token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (!AtomicFilePublication.TryPublishNew(staging, path))
                return await ReadDestinationAsync(path, token).ConfigureAwait(false);
            return destination;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static async Task<string> ReadDestinationAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length > I2pSamConnection.MaximumLineLength) throw new InvalidDataException("I2P key file exceeds its bound.");
        using var reader = new StreamReader(file);
        // A bounded stream read prevents a concurrently enlarged file from allocating without limit.
        var buffer = new char[I2pSamConnection.MaximumLineLength + 1];
        try
        {
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > I2pSamConnection.MaximumLineLength) throw new InvalidDataException("I2P key file exceeds its bound.");
            var destination = new string(buffer, 0, count);
            _ = I2pDestination.Base32FromPrivate(destination);
            return destination;
        }
        finally { Array.Clear(buffer); }
    }

    private async Task ServerLoopAsync()
    {
        var attempts = 0;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    using var setup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    setup.CancelAfter(_options.SetupTimeout);
                    var destination = await LoadDestinationAsync(setup.Token).ConfigureAwait(false);
                    var base32 = I2pDestination.Base32FromPrivate(destination);
                    await using var session = await I2pSamConnection.OpenAsync(_options, setup.Token).ConfigureAwait(false);
                    var id = "reticulum-" + Guid.NewGuid().ToString("N");
                    await session.CommandAsync($"SESSION CREATE STYLE=STREAM ID={id} DESTINATION={destination} \n",
                        "SESSION", "STATUS", setup.Token).ConfigureAwait(false);
                    attempts = 0;
                    lock (_gate) { _online = true; _base32 = base32; }
                    using var active = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    var monitor = session.WatchSessionAsync(active.Token);
                    var accept = AcceptLoopAsync(id, active.Token);
                    var first = await Task.WhenAny(monitor, accept).ConfigureAwait(false);
                    await active.CancelAsync().ConfigureAwait(false);
                    await I2pPacketPeer.JoinAsync(monitor, active.Token).ConfigureAwait(false);
                    await I2pPacketPeer.JoinAsync(accept, active.Token).ConfigureAwait(false);
                    if (first.IsFaulted) await first.ConfigureAwait(false);
                }
                catch (Exception error) when (!_lifetime.IsCancellationRequested)
                {
                    Report(error is OperationCanceledException ? new TimeoutException("I2P endpoint setup timed out.") : error);
                }
                finally
                {
                    I2pPacketPeer[] incoming;
                    lock (_gate) { _online = false; incoming = _peers.Where(p => !p.IsInitiator).ToArray(); }
                    foreach (var peer in incoming) await peer.DisposeAsync().ConfigureAwait(false);
                }
                if (_options.MaxReconnectAttempts is { } maximum && attempts >= maximum) break;
                attempts++;
                await Task.Delay(_options.ReconnectDelay, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task AcceptLoopAsync(string sessionId, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            I2pSamConnection? connection = null;
            try
            {
                using (var setup = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    setup.CancelAfter(_options.SetupTimeout);
                    connection = await I2pSamConnection.OpenAsync(_options, setup.Token).ConfigureAwait(false);
                    await connection.CommandAsync($"STREAM ACCEPT ID={sessionId} SILENT=false\n",
                        "STREAM", "STATUS", setup.Token).ConfigureAwait(false);
                }
                var destination = await connection.ReadLineAsync(token).ConfigureAwait(false);
                var peerAddress = I2pDestination.Base32FromSamPublic(destination) + ".b32.i2p";
                I2pPacketPeer? child = null;
                lock (_gate)
                {
                    if (!_lifetime.IsCancellationRequested && _dispose is null && _peers.Count(p => !p.IsInitiator) < _options.MaxIncomingPeers)
                    {
                        var peer = new I2pPacketPeer(this, _options, peerAddress, initiator: false);
                        _peers.Add(peer);
                        peer.Begin(connection);
                        connection = null;
                        child = peer;
                    }
                    if (child is not null) Publish(child);
                    else Interlocked.Increment(ref _rejected);
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                Report(error is OperationCanceledException ? new TimeoutException("I2P accept setup timed out.") : error);
                await Task.Delay(_options.ReconnectDelay, token).ConfigureAwait(false);
            }
            finally { if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false); }
        }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            if (!_started) throw new InvalidOperationException("The I2P interface has not been started.");
        }
        if (Interlocked.Exchange(ref _receiving, 1) != 0) throw new InvalidOperationException("ReceiveAsync supports a single enumerator.");
        return ReceiveEmptyAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveEmptyAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var packet in _empty.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return packet;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            if (!_started) throw new InvalidOperationException("The I2P interface has not been started.");
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["status"] = _online, ["clients"] = _peers.Count(p => !p.IsInitiator),
            ["outgoing_peers"] = _peers.Count(p => p.IsInitiator), ["bitrate"] = Bitrate,
            ["hardware_mtu"] = HardwareMtu, ["i2p_tunneled"] = true,
            ["dotreticulum_bitrate_kind"] = _options.ConfiguredBitrate is >= 5 ? "configured" : "configured_estimate",
            ["i2p_b32"] = _base32, ["connectable"] = _options.Connectable,
            ["last_error"] = _lastError, ["rejected_clients"] = Interlocked.Read(ref _rejected),
            ["rxb"] = Interlocked.Read(ref _rx), ["txb"] = Interlocked.Read(ref _tx),
        };
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_dispose ??= Task.Run(DisposeCoreAsync));
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        I2pPacketPeer[] peers;
        Task? server;
        lock (_gate) { peers = _peers.ToArray(); server = _server; }
        _empty.Writer.TryComplete();
        foreach (var peer in peers) await peer.DisposeAsync().ConfigureAwait(false);
        if (server is not null) await I2pPacketPeer.JoinAsync(server, _lifetime.Token).ConfigureAwait(false);
        lock (_gate) { _online = false; _peers.Clear(); }
    }
}
