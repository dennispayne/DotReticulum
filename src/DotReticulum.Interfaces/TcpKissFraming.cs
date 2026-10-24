namespace DotReticulum.Interfaces;

/// <summary>TCP KISS framing has the TCP MTU, not the much smaller radio KISS MTU.</summary>
internal sealed class TcpKissFrameDecoder(int maximumFrameLength)
{
    private readonly byte[] _buffer = new byte[maximumFrameLength];
    private bool _inFrame;
    private bool _escape;
    private bool _discard;
    private int _command = -1;
    private int _length;

    public static byte[] Encode(ReadOnlySpan<byte> packet)
    {
        var bytes = new byte[checked(packet.Length * 2 + 3)];
        var count = 0;
        bytes[count++] = 0xc0;
        bytes[count++] = 0;
        foreach (var value in packet)
        {
            if (value is 0xc0 or 0xdb)
            {
                bytes[count++] = 0xdb;
                bytes[count++] = value == 0xc0 ? (byte)0xdc : (byte)0xdd;
            }
            else bytes[count++] = value;
        }
        bytes[count++] = 0xc0;
        return bytes.AsSpan(0, count).ToArray();
    }

    public void Feed(ReadOnlySpan<byte> data, List<byte[]> frames)
    {
        foreach (var value in data)
        {
            if (value == 0xc0)
            {
                if (_inFrame && _command == 0)
                {
                    if (!_discard && !_escape && _length > 2)
                        frames.Add(_buffer.AsSpan(0, _length).ToArray());
                    _inFrame = false;
                }
                else _inFrame = true;
                _command = -1;
                _length = 0;
                _escape = false;
                _discard = false;
                continue;
            }
            if (!_inFrame || _discard) continue;
            if (_command == -1)
            {
                _command = value & 0x0f;
                continue;
            }
            if (_command != 0) continue;
            var decoded = value;
            if (_escape)
            {
                _escape = false;
                if (value is not (0xdc or 0xdd)) { _discard = true; continue; }
                decoded = value == 0xdc ? (byte)0xc0 : (byte)0xdb;
            }
            else if (value == 0xdb) { _escape = true; continue; }
            if (_length == _buffer.Length) { _discard = true; continue; }
            _buffer[_length++] = decoded;
        }
    }
}
