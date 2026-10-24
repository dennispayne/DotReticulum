using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Core;
using DotReticulum.Interfaces;

namespace DotReticulum.Interfaces.Tests;

internal sealed class Parity6WeaveClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
}

internal sealed class Parity6WeaveFixture : IDisposable
{
    private readonly JsonDocument _json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity6-weave.json")));
    public byte[] Bytes(string name) => Convert.FromHexString(_json.RootElement.GetProperty(name).GetString()!);
    public byte[] Write(int index) => Convert.FromHexString(_json.RootElement.GetProperty("writes")[index].GetString()!);
    public Identity HostIdentity() => Identity.FromPrivateKey(Bytes("host_test_private_key"));
    public void Dispose() => _json.Dispose();
}

/// <summary>Owned byte stream only: does not enumerate/open any serial or Bluetooth device.</summary>
internal sealed class Parity6WeaveSimulator(Parity6WeaveFixture fixture) : Stream
{
    private readonly Channel<byte[]> _incoming = Channel.CreateBounded<byte[]>(128);
    private readonly Channel<byte[]> _writes = Channel.CreateBounded<byte[]>(128);
    private byte[]? _current;
    private int _offset, _disposed;
    public bool AutoHandshake { get; set; } = true;
    public bool FailWrites { get; set; }
    public TaskCompletionSource? BlockCommands { get; set; }
    public ChannelReader<byte[]> Writes => _writes.Reader;

    public void Inject(byte[] bytes)
    {
        if (!_incoming.Writer.TryWrite(bytes)) throw new InvalidOperationException("Simulator input overflow.");
    }

    public void Unplug() => _incoming.Writer.TryComplete();

    public static byte[] Log(byte[] host, ushort evt, byte[]? data = null)
    {
        var payload = new byte[9 + (data?.Length ?? 0)];
        // Two reserved bytes, timestamp, severity, big-endian event.
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2), 1234);
        payload[6] = 5;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(7), evt);
        data?.CopyTo(payload, 9);
        return WeaveProtocol.Frame(host, WeaveProtocol.Log, payload);
    }
    public void Endpoint(byte[] endpoint, byte[] data)
    {
        Inject(WeaveProtocol.Frame(fixture.Bytes("host_switch_id"), WeaveProtocol.EndpointPacket, [.. data, .. endpoint]));
    }
    public void Alive(byte[] endpoint) => Inject(Log(fixture.Bytes("host_switch_id"), 0x3102, endpoint));
    public void Via(byte[] endpoint, byte[] via) => Inject(Log(fixture.Bytes("host_switch_id"), 0x3104, [.. endpoint, .. via]));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_current is null)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (!_incoming.Reader.TryRead(out _current)) return 0;
            _offset = 0;
        }
        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        if (_offset == _current.Length) _current = null;
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (FailWrites) throw new IOException("Simulated serial write failure.");
        var raw = new List<byte[]>();
        new WeaveProtocol.Decoder().Feed(buffer.Span, raw.Add);
        if (raw.Count == 1 && raw[0][4] == WeaveProtocol.Command && BlockCommands is not null)
            await BlockCommands.Task.WaitAsync(cancellationToken);
        await _writes.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        if (!AutoHandshake || raw.Count != 1) return;
        if (raw[0][4] == WeaveProtocol.Discover) Inject(WeaveProtocol.Encode(fixture.Bytes("discovery_response")));
        if (raw[0][4] == WeaveProtocol.Connect)
        {
            Inject(Log(fixture.Bytes("host_switch_id"), 0x3002));
            Inject(Log(fixture.Bytes("host_switch_id"), 0x3003, fixture.Bytes("endpoint")));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        { _incoming.Writer.TryComplete(); _writes.Writer.TryComplete(); }
        base.Dispose(disposing);
    }
    public override bool CanRead => _disposed == 0;
    public override bool CanWrite => _disposed == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
