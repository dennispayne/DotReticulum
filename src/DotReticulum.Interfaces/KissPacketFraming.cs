namespace DotReticulum.Interfaces;

/// <summary>Port-zero transmit and bounded, port-agnostic receive framing matching pinned KISSInterface.</summary>
public static class KissPacketFraming
{
    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length > 580) throw new ArgumentOutOfRangeException(nameof(data));
        var output = new byte[data.Length * 2 + 3];
        var count = 0;
        output[count++] = 0xc0;
        output[count++] = 0;
        foreach (var value in data)
        {
            if (value is 0xc0 or 0xdb)
            {
                output[count++] = 0xdb;
                output[count++] = value == 0xc0 ? (byte)0xdc : (byte)0xdd;
            }
            else output[count++] = value;
        }
        output[count++] = 0xc0;
        return output.AsSpan(0, count).ToArray();
    }

    public static byte[] Configuration(KissOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return [
            0xc0, 1, Clamp(options.PreambleMilliseconds / 10), 0xc0,
            0xc0, 4, Clamp(options.TxTailMilliseconds / 10), 0xc0,
            0xc0, 2, Clamp(options.Persistence), 0xc0,
            0xc0, 3, Clamp(options.SlotTimeMilliseconds / 10), 0xc0,
            0xc0, 15, 1, 0xc0,
        ];
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}

/// <summary>Preserves upstream permissive escape, delimiter and oversized-prefix receive behavior.</summary>
public sealed class KissFrameDecoder
{
    private readonly byte[] _buffer;
    private bool _inFrame;
    private bool _escape;
    private int _command = 0xfe;
    private int _count;

    public KissFrameDecoder(int maximumFrameLength = 564)
    {
        if (maximumFrameLength is < 1 or > 580) throw new ArgumentOutOfRangeException(nameof(maximumFrameLength));
        _buffer = new byte[maximumFrameLength];
    }

    public bool HasPartialData => _count > 0;

    public int Feed(ReadOnlySpan<byte> input, ICollection<byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var ready = 0;
        foreach (var value in input)
        {
            if (_inFrame && value == 0xc0 && _command == 0)
            {
                _inFrame = false;
                if (_count > 0) frames.Add(_buffer.AsSpan(0, _count).ToArray());
            }
            else if (value == 0xc0)
            {
                _inFrame = true;
                _command = 0xfe;
                _count = 0;
            }
            else if (_inFrame && _count < _buffer.Length)
            {
                if (_count == 0 && _command == 0xfe) _command = value & 0x0f;
                else if (_command == 0)
                {
                    if (value == 0xdb) _escape = true;
                    else
                    {
                        _buffer[_count++] = _escape ? value switch { 0xdc => (byte)0xc0, 0xdd => (byte)0xdb, _ => value } : value;
                        _escape = false;
                    }
                }
                else if (_command == 15) ready++;
            }
        }
        return ready;
    }

    public void ExpirePartial()
    {
        if (!HasPartialData) return;
        _count = 0;
        _inFrame = false;
        _command = 0xfe;
        _escape = false;
    }
}
