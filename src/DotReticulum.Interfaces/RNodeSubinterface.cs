using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>A stable, already-started radio endpoint owned by an RNode device.</summary>
public sealed class RNodeSubinterface : IReconnectingPacketInterface, IPacketInterfaceDiagnostics, IPacketInterfaceChildPolicy
{
    private readonly RNodeDevice _device;
    private readonly Channel<ReadOnlyMemory<byte>> _received;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendSlots;
    private readonly SemaphoreSlim _sendOrder = new(1, 1);
    private readonly SemaphoreSlim _credit = new(0, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _statistics = [];
    private bool _connected;
    private bool _disposed;
    private int _reader;
    private long _receivedBytes;
    private long _sentBytes;
    private long _dropped;
    private long _observerErrors;
    private long _bitrate;
    private RNodeRadioOptions _options;

    internal RNodeSubinterface(RNodeDevice device, string name, int virtualPort, RNodeRadioOptions options, bool outgoing,
        RNodeDeviceOptions deviceOptions)
    {
        _device = device;
        Name = name;
        VirtualPort = virtualPort;
        _options = options;
        Outgoing = outgoing;
        _sendSlots = new(deviceOptions.SendQueueCapacity, deviceOptions.SendQueueCapacity);
        _received = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(deviceOptions.ReceiveQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    }

    public string Name { get; }
    public int VirtualPort { get; }
    public RNodeRadioOptions Options => Volatile.Read(ref _options);
    public bool Outgoing { get; }
    public bool AllowOutgoingSelection => Outgoing;
    public bool InheritParentOutgoing => false;
    public bool CountParentTraffic => true;
    public bool InheritParentAccessCode => false;
    public long Bitrate => Interlocked.Read(ref _bitrate);
    public int HardwareMtu => RNodeProtocol.HardwareMtu;
    public bool IsConnected { get { lock (_gate) return _connected && !_disposed && !_device.IsDisposed; } }
    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<Exception>? Faulted;

    public ValueTask StartAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("RNode children are started and owned by their parent device.");

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _reader, 1, 0) != 0)
            throw new InvalidOperationException("Only one receiver is supported.");
        try
        {
            await foreach (var packet in _received.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return packet;
        }
        finally { Interlocked.Exchange(ref _reader, 0); }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (packet.Length is < 1 or > RNodeProtocol.HardwareMtu) throw new ArgumentOutOfRangeException(nameof(packet));
        var flowControl = Options.FlowControl;
        if (!IsConnected) throw new IOException("The RNode radio is offline.");
        var session = _device.CurrentSessionToken;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token, _device.StopToken, session);
        await _sendSlots.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var ownedPacket = packet.ToArray();
            await _sendOrder.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (!IsConnected) throw new IOException("The RNode radio is offline.");
                using var connected = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, session);
                if (flowControl) await _credit.WaitAsync(connected.Token).ConfigureAwait(false);
                try { await _device.SendAsync(this, ownedPacket, connected.Token).ConfigureAwait(false); }
                catch
                {
                    if (flowControl && IsConnected && !session.IsCancellationRequested) Ready();
                    throw;
                }
                Interlocked.Add(ref _sentBytes, ownedPacket.Length);
            }
            finally { _sendOrder.Release(); }
        }
        finally { _sendSlots.Release(); }
    }

    internal void Incoming(byte[] packet)
    {
        if (!IsConnected) return;
        if (_received.Writer.TryWrite(packet)) Interlocked.Add(ref _receivedBytes, packet.Length);
        else Interlocked.Increment(ref _dropped);
    }

    internal void Ready()
    {
        try { _credit.Release(); }
        catch (SemaphoreFullException) { }
    }

    internal void SetConnected(bool connected, bool disposing = false)
    {
        lock (_gate)
        {
            if (_disposed || _connected == connected) return;
            _connected = connected;
            if (connected)
            {
                while (_credit.Wait(0)) { }
                Ready();
            }
        }
        if (connected) Notify(Connected);
        else if (!disposing) Notify(Disconnected);
    }

    internal void Report(Exception exception)
    {
        lock (_gate) _statistics["last_error"] = exception.Message;
        if (Faulted is not { } observers) return;
        foreach (Action<Exception> observer in observers.GetInvocationList())
        {
            try { observer(exception); }
            catch (Exception callbackError)
            {
                Interlocked.Increment(ref _observerErrors);
                _device.ReportObserver(callbackError);
            }
        }
    }

    internal void Statistic(string key, object? value)
    {
        lock (_gate) _statistics[key] = value;
    }

    internal void UpdateBitrate(int sf, int cr, long bandwidth)
    {
        Interlocked.Exchange(ref _bitrate, sf is >= 5 and <= 12 && cr is >= 5 and <= 8 && bandwidth > 0
            ? (long)(sf * (4.0 / cr) * bandwidth / Math.Pow(2, sf)) : 0);
    }

    internal void SetConfiguredBitrate(long bitrate) => Interlocked.Exchange(ref _bitrate, bitrate);
    internal void SetOptions(RNodeRadioOptions options) => Volatile.Write(ref _options, options);
    public ValueTask ConfigureRadioAsync(RNodeRadioOptions options, CancellationToken cancellationToken = default) =>
        _device.ConfigureRadioAsync(this, options, cancellationToken);
    public ValueTask SetRadioStateAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _device.RadioStateAsync(this, enabled, cancellationToken);

    private void Notify(Action? observers)
    {
        if (observers is null) return;
        foreach (Action observer in observers.GetInvocationList())
            try { observer(); }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _observerErrors);
                _device.ReportObserver(exception);
            }
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, object?>(_statistics)
            {
                ["status"] = _connected && !_disposed && !_device.IsDisposed, ["name"] = Name, ["virtual_port"] = VirtualPort,
                ["outgoing"] = Outgoing, ["bitrate"] = Bitrate, ["hardware_mtu"] = HardwareMtu,
                ["rxb"] = Interlocked.Read(ref _receivedBytes), ["txb"] = Interlocked.Read(ref _sentBytes),
                ["dropped_packets"] = Interlocked.Read(ref _dropped),
                ["observer_errors"] = Interlocked.Read(ref _observerErrors),
            };
            return result;
        }
    }

    internal void Complete(Exception? error) => _received.Writer.TryComplete(error);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _connected = false;
        }
        _received.Writer.TryComplete();
        _stop.Cancel();
        return ValueTask.CompletedTask;
    }
}
