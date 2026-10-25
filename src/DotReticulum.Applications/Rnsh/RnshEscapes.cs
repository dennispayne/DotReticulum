using System.Text;

namespace DotReticulum.Applications.Rnsh;

/// <summary>Interactive-only escapes from pinned initiator.py; binary pipes bypass this filter.</summary>
public sealed class RnshEscapes
{
    private bool _afterNewline = true;
    private bool _escape;
    private bool _lineMode;
    private readonly List<byte> _line = [];
    public bool Terminate { get; private set; }
    public byte[] Process(ReadOnlySpan<byte> input, out byte[] localOutput)
    {
        var remote = new List<byte>();
        var local = new List<byte>();
        foreach (var value in input)
        {
            if (_escape)
            {
                _escape = false;
                switch (value)
                {
                    case (byte)'.': Terminate = true; break;
                    case (byte)'?':
                        local.AddRange(Encoding.UTF8.GetBytes("\r\nSupported rnsh escape sequences:\r\n  ~~  Send ~\r\n  ~.  Terminate session\r\n  ~L  Toggle line-interactive mode\r\n  ~?  Display this reference\r\n"));
                        break;
                    case (byte)'L':
                        _lineMode = !_lineMode;
                        local.AddRange(Encoding.UTF8.GetBytes("\r\nLine-interactive mode " + (_lineMode ? "enabled" : "disabled") + "\r\n"));
                        break;
                    default:
                        if (value != (byte)'~') Append((byte)'~');
                        Append(value);
                        break;
                }
            }
            else if (_afterNewline && value == (byte)'~') { _escape = true; _afterNewline = false; }
            else
            {
                if (_lineMode && value is 8 or 127)
                {
                    if (_line.Count > 0) { _line.RemoveAt(_line.Count - 1); local.AddRange("\b \b"u8.ToArray()); }
                }
                else Append(value);
                _afterNewline = value == 13;
            }
        }
        localOutput = local.ToArray();
        return remote.ToArray();
        void Append(byte value)
        {
            if (!_lineMode) remote.Add(value);
            else
            {
                _line.Add(value);
                if (value is 13 or 1 or 3 or 4 or 5 or 12 or 17 or 19 or 21 or 25 or 9 or 26 or 27)
                {
                    remote.AddRange(_line);
                    _line.Clear();
                }
                else local.Add(value);
            }
        }
    }
}
