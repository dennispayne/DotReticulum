namespace DotReticulum.Interfaces;

internal sealed class BackboneFrameDecoder(int maximumFrameLength)
{
    private readonly byte[] _buffer = new byte[checked(maximumFrameLength * 2)];
    private int _length;
    private bool _started;
    private bool _discard;

    public void Feed(ReadOnlySpan<byte> bytes, List<byte[]> frames)
    {
        foreach (var value in bytes)
        {
            if (value == 0x7e)
            {
                if (_started && !_discard && _length > 0)
                {
                    // Upstream performs these two replacements in this order, not a generic escape parser.
                    var first = Replace(_buffer.AsSpan(0, _length), 0x5e, 0x7e);
                    var frame = Replace(first, 0x5d, 0x7d);
                    if (frame.Length > 19 && frame.Length <= maximumFrameLength) frames.Add(frame);
                }
                _started = true;
                _length = 0;
                _discard = false;
            }
            else if (_started && !_discard)
            {
                if (_length == _buffer.Length) _discard = true;
                else _buffer[_length++] = value;
            }
        }
    }

    private static byte[] Replace(ReadOnlySpan<byte> source, byte escaped, byte replacement)
    {
        var result = new byte[source.Length];
        var count = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == 0x7d && i + 1 < source.Length && source[i + 1] == escaped)
            {
                result[count++] = replacement;
                i++;
            }
            else result[count++] = source[i];
        }
        return result.AsSpan(0, count).ToArray();
    }
}

internal sealed class PipeFrameDecoder(int maximumFrameLength)
{
    private readonly byte[] _buffer = new byte[maximumFrameLength];
    private int _length;
    private bool _started;
    private bool _escaped;
    private bool _discard;

    public void Feed(ReadOnlySpan<byte> bytes, List<byte[]> frames)
    {
        foreach (var value in bytes)
        {
            if (value == 0x7e)
            {
                if (_started)
                {
                    if (!_discard && !_escaped && _length > 0) frames.Add(_buffer.AsSpan(0, _length).ToArray());
                    _started = false;
                }
                else _started = true;
                _length = 0;
                _escaped = false;
                _discard = false;
                continue;
            }
            if (!_started || _discard) continue;
            byte decoded = value;
            if (_escaped)
            {
                _escaped = false;
                if (value is not (0x5d or 0x5e)) { _discard = true; continue; }
                decoded = (byte)(value ^ 0x20);
            }
            else if (value == 0x7d) { _escaped = true; continue; }
            if (_length == _buffer.Length) _discard = true;
            else _buffer[_length++] = decoded;
        }
    }
}
