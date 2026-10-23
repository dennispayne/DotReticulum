using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>
/// Simplified HDLC framing as used by upstream's TCPInterface and SerialInterface
/// (RNS/Interfaces/TCPInterface.py class HDLC, SerialInterface.py): <c>FLAG | escape(packet) | FLAG</c> with
/// FLAG = 0x7E, ESC = 0x7D, escaped byte = byte XOR 0x20. There is no CRC and no address/control field.
/// This is not KISS (FEND 0xC0 / FESC 0xDB) framing.
/// </summary>
internal static class HdlcPacketFraming
{
    public const byte Flag = 0x7E;
    public const byte Escape = 0x7D;
    public const byte EscapeMask = 0x20;

    /// <summary>Worst case encoded size for a packet of <paramref name="packetLength"/> bytes.</summary>
    public static int MaxEncodedLength(int packetLength) => 2 + (2 * packetLength);

    /// <summary>Frames a complete, valid wire packet. Throws <see cref="ArgumentException"/> if it is not one.</summary>
    public static byte[] Encode(ReadOnlyMemory<byte> packet, int maxFrameLength = Packet.Mtu, bool validatePacket = true)
    {
        if (packet.IsEmpty || packet.Length > maxFrameLength || (validatePacket && !Packet.TryParse(packet, out _, maxFrameLength)))
        {
            throw new ArgumentException("Not a valid Reticulum packet (or larger than the MTU).", nameof(packet));
        }

        var span = packet.Span;
        var output = new byte[MaxEncodedLength(span.Length)];
        var n = 0;
        output[n++] = Flag;
        foreach (var b in span)
        {
            if (b is Flag or Escape)
            {
                output[n++] = Escape;
                output[n++] = (byte)(b ^ EscapeMask);
            }
            else
            {
                output[n++] = b;
            }
        }

        output[n++] = Flag;
        return output.AsSpan(0, n).ToArray();
    }
}

/// <summary>
/// Streaming HDLC decoder. Bytes before the first flag are ignored. A flag closes the current frame and also opens the
/// next one (a shared flag between back-to-back frames is accepted, as upstream's TCP reader does). Frames that are
/// empty, truncated (flag inside an escape), contain an invalid escape sequence, exceed the maximum length, or fail
/// <see cref="Packet.TryParse(ReadOnlyMemory{byte}, out Packet?, int)"/> are discarded; decoding resumes at the next flag.
/// Not thread-safe.
/// </summary>
internal sealed class HdlcFrameDecoder
{
    private readonly byte[] _buffer;
    private int _length;
    private bool _inFrame;
    private bool _escape;
    private bool _discard;
    private readonly bool _validatePackets;

    public HdlcFrameDecoder(int maxFrameLength = Packet.Mtu, bool validatePackets = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameLength, 1);
        MaxFrameLength = maxFrameLength;
        _buffer = new byte[maxFrameLength];
        _validatePackets = validatePackets;
    }

    public int MaxFrameLength { get; }

    /// <summary>Consumes <paramref name="data"/> and appends every complete valid packet to <paramref name="packets"/>.</summary>
    public void Feed(ReadOnlySpan<byte> data, List<byte[]> packets)
    {
        foreach (var b in data)
        {
            if (b == HdlcPacketFraming.Flag)
            {
                if (_inFrame && !_discard && !_escape && _length > 0)
                {
                    var frame = _buffer.AsSpan(0, _length).ToArray();
                    if (_validatePackets ? Packet.TryParse(frame, out _, MaxFrameLength) : frame.Length > 2)
                    {
                        packets.Add(frame);
                    }
                }

                _inFrame = true;
                _length = 0;
                _escape = false;
                _discard = false;
                continue;
            }

            if (!_inFrame || _discard)
            {
                continue;
            }

            byte value = b;
            if (_escape)
            {
                _escape = false;
                value = b switch
                {
                    HdlcPacketFraming.Flag ^ HdlcPacketFraming.EscapeMask => HdlcPacketFraming.Flag,
                    HdlcPacketFraming.Escape ^ HdlcPacketFraming.EscapeMask => HdlcPacketFraming.Escape,
                    _ => 0,
                };
                if (value == 0)
                {
                    _discard = true;
                    continue;
                }
            }
            else if (b == HdlcPacketFraming.Escape)
            {
                _escape = true;
                continue;
            }

            if (_length == _buffer.Length)
            {
                _discard = true;
                continue;
            }

            _buffer[_length++] = value;
        }
    }
}
