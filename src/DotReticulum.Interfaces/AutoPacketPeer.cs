using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

internal sealed class AutoPacketPeer : IPacketInterface, IPacketInterfaceDiagnostics
{
    private readonly AutoPacketInterface _owner;
    private readonly Channel<ReadOnlyMemory<byte>> _frames;
    private readonly object _sendGate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _sendTask;
    private int _receiving;
    private int _closed;
    private int _disposed;
    private long _received;
    private long _sent;
    internal IPAddress Address { get; }
    internal string DeviceName { get; }
    internal DateTimeOffset LastHeard { get; set; }
    internal DateTimeOffset LastOutbound { get; set; }
    public long Bitrate => _owner.Bitrate;
    public int HardwareMtu => AutoPacketInterface.FixedHardwareMtu;
    public bool SupportsMtuDiscovery => true;

    internal AutoPacketPeer(AutoPacketInterface owner, IPAddress address, string device, DateTimeOffset now, int capacity)
    {
        _owner = owner;
        Address = address;
        DeviceName = device;
        LastHeard = LastOutbound = now;
        _frames = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _receiving, 1) != 0) throw new InvalidOperationException("ReceiveAsync supports a single enumerator.");
        return ReceiveCoreAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return frame;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        AutoPacketInterface.ValidateFrame(packet);
        // Reject concurrent work rather than retain an unbounded queue of caller-owned packet buffers.
        lock (_sendGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            if (_sendTask is { IsCompleted: false }) throw new IOException("AutoInterface peer already has an in-flight datagram.");
            _sendTask = SendCoreAsync(packet, cancellationToken);
            return new(_sendTask);
        }
    }

    private async Task SendCoreAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _owner.SendPeerAsync(this, packet, linked.Token).ConfigureAwait(false);
    }

    internal void Accept(ReadOnlyMemory<byte> data)
    {
        Interlocked.Add(ref _received, data.Length);
        if (!_frames.Writer.TryWrite(data.ToArray())) _owner.Drop($"AutoInterface receive queue full for {Address} on {DeviceName}.");
    }

    internal void RecordSent(int length) => Interlocked.Add(ref _sent, length);

    internal void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _stop.Cancel();
            _frames.Writer.TryComplete();
            while (_frames.Reader.TryRead(out _)) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _owner.RemovePeer(this);
        Task? send;
        lock (_sendGate) send = _sendTask;
        if (send is not null)
        {
            // SendPeerAsync reports non-cancellation errors; disposal only joins the already-observed operation.
            try { await send.ConfigureAwait(false); }
            catch (Exception) when (send.IsCanceled || send.IsFaulted) { }
        }
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _stop.Dispose();
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics() => new Dictionary<string, object?>
    {
        ["peer"] = Address.ToString(),
        ["device"] = DeviceName,
        ["rxb"] = Interlocked.Read(ref _received),
        ["txb"] = Interlocked.Read(ref _sent),
        ["bitrate"] = Bitrate,
        ["hardware_mtu"] = HardwareMtu,
        ["status"] = Volatile.Read(ref _closed) == 0,
        ["dotreticulum_bitrate_kind"] = "configured_estimate",
    };

    public override string ToString() => $"AutoInterfacePeer[{DeviceName}/{Address}]";
}
