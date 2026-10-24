using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

public sealed class WeavePeer : IPacketInterface, IPacketInterfaceDiagnostics
{
    private readonly WeavePacketInterface _parent;
    private readonly byte[] _endpoint;
    private byte[]? _via;
    private readonly Channel<ReadOnlyMemory<byte>> _incoming;
    private int _receiving, _closed;
    private long _received, _sent;

    internal WeavePeer(WeavePacketInterface parent, byte[] endpoint, int capacity, DateTimeOffset now)
    {
        _parent = parent; _endpoint = endpoint.ToArray(); LastHeard = now;
        _incoming = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }
    public byte[] EndpointId => _endpoint.ToArray();
    public byte[]? ViaSwitchId => _via?.ToArray();
    internal void SetVia(byte[] via) => _via = via;
    internal DateTimeOffset LastHeard { get; set; }
    public bool IsConnected => _closed == 0 && _parent.IsConnected;
    public long Bitrate => _parent.Bitrate;
    public int HardwareMtu => _parent.HardwareMtu;
    public bool SupportsMtuDiscovery => true;
    public ValueTask StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Weave peers are already started.");
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _receiving, 1) != 0) throw new InvalidOperationException("Only one receive enumeration is supported.");
        return _incoming.Reader.ReadAllAsync(cancellationToken);
    }
    internal bool Enqueue(byte[] data)
    {
        if (_closed != 0 || !_incoming.Writer.TryWrite(data)) return false;
        Interlocked.Add(ref _received, data.Length);
        return true;
    }
    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed != 0, this);
        await _parent.DeliverAsync(this, _endpoint, packet, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _sent, packet.Length);
    }
    internal void Close() { Interlocked.Exchange(ref _closed, 1); _incoming.Writer.TryComplete(); }
    public ValueTask DisposeAsync() { Close(); _parent.Remove(this, _endpoint); return ValueTask.CompletedTask; }
    public IReadOnlyDictionary<string, object?> GetDiagnostics() => new Dictionary<string, object?>
    {
        ["status"] = IsConnected, ["rxb"] = Interlocked.Read(ref _received), ["txb"] = Interlocked.Read(ref _sent),
        ["endpoint_id"] = Convert.ToHexStringLower(_endpoint), ["via_switch_id"] = _via is null ? null : Convert.ToHexStringLower(_via),
        ["bitrate"] = Bitrate, ["hardware_mtu"] = HardwareMtu,
        ["dotreticulum_bitrate_kind"] = "configured_estimate",
    };
}
