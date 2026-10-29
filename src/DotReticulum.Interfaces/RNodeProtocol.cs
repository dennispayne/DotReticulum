using System.Buffers.Binary;

namespace DotReticulum.Interfaces;

/// <summary>The RNode command protocol, distinct from standard KISS port/command nibble encoding.</summary>
public static class RNodeProtocol
{
    public const int HardwareMtu = 508;

    public static byte[] Frame(byte command, ReadOnlySpan<byte> payload)
    {
        var bytes = new List<byte>(payload.Length * 2 + 3) { 0xc0, command };
        foreach (var value in payload)
        {
            if (value == 0xc0) bytes.AddRange([0xdb, 0xdc]);
            else if (value == 0xdb) bytes.AddRange([0xdb, 0xdd]);
            else bytes.Add(value);
        }
        bytes.Add(0xc0);
        return bytes.ToArray();
    }

    public static byte[] Detect(bool multi = false) => multi
        ? [0xc0, 8, 0x73, 0xc0, 0x50, 0, 0xc0, 0x48, 0, 0xc0, 0x49, 0, 0xc0, 0x71, 0, 0xc0]
        : [0xc0, 8, 0x73, 0xc0, 0x50, 0, 0xc0, 0x48, 0, 0xc0, 0x49, 0, 0xc0];

    public static byte[] SelectedFrame(int virtualPort, byte command, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(virtualPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(virtualPort, 10);
        return [.. Frame(0x1f, [(byte)virtualPort]), .. Frame(command, payload)];
    }

    public static IReadOnlyList<byte[]> Configuration(RNodeRadioOptions options, int? virtualPort = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(virtualPort.HasValue);
        var frames = new List<byte[]>();
        void Add(byte command, byte[] payload) => frames.Add(virtualPort is { } port
            ? SelectedFrame(port, command, payload) : Frame(command, payload));
        var frequency = new byte[4];
        var bandwidth = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(frequency, (uint)options.Frequency);
        BinaryPrimitives.WriteUInt32BigEndian(bandwidth, (uint)options.Bandwidth);
        Add(1, frequency);
        Add(2, bandwidth);
        Add(3, [unchecked((byte)options.TxPower)]);
        Add(4, [(byte)options.SpreadingFactor]);
        Add(5, [(byte)options.CodingRate]);
        foreach (var (command, value) in new[] { ((byte)0x0b, options.AirtimeLimitShort), ((byte)0x0c, options.AirtimeLimitLong) })
        {
            if (value is not { } percentage) continue;
            var data = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)(percentage * 100));
            Add(command, data);
        }
        Add(6, [1]);
        return frames;
    }
}

public sealed record RNodeFrame(byte Command, byte[] Payload);

/// <summary>Bounded, strict incremental decoder. Idle expiry is driven by the owning asynchronous reader.</summary>
public sealed class RNodeFrameDecoder
{
    private readonly byte[] _buffer = new byte[1024];
    private int _length;
    private int _command = -1;
    private bool _framing;
    private bool _escape;
    private bool _invalid;
    public long DroppedFrames { get; private set; }
    public bool HasPartialFrame => _command >= 0 || _length > 0 || _escape;

    public void Expire()
    {
        if (HasPartialFrame) DroppedFrames++;
        _framing = _escape = _invalid = false;
        _command = -1;
        _length = 0;
    }

    public IReadOnlyList<RNodeFrame> Feed(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<RNodeFrame>();
        foreach (var value in bytes)
        {
            if (value == 0xc0)
            {
                if (_framing && _command >= 0)
                {
                    if (!_invalid && !_escape && (_length > 0 || _command == 0x6d) &&
                        (_command != 0 || _length <= RNodeProtocol.HardwareMtu))
                        frames.Add(new((byte)_command, _buffer.AsSpan(0, _length).ToArray()));
                    else DroppedFrames++;
                }
                _framing = true;
                _escape = _invalid = false;
                _command = -1;
                _length = 0;
            }
            else if (!_framing || _invalid) continue;
            else if (_command < 0) _command = value;
            else if (_escape)
            {
                _escape = false;
                if (value is not (0xdc or 0xdd)) { _invalid = true; continue; }
                Append(value == 0xdc ? (byte)0xc0 : (byte)0xdb);
            }
            else if (value == 0xdb) _escape = true;
            else Append(value);
        }
        return frames;
    }

    private void Append(byte value)
    {
        if (_length == _buffer.Length) _invalid = true;
        else _buffer[_length++] = value;
    }
}
