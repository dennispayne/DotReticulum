using DotReticulum.Core;

namespace DotReticulum.Interfaces;

internal static class HdlcPacketFraming
{
    private const byte Flag = 0x7e;
    private const byte Escape = 0x7d;
    private const byte EscapeMask = 0x20;

    internal static byte[] Encode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < Packet.Header1Length + 1 || packet.Length > Packet.Mtu)
            throw new ArgumentException("Data must contain one complete Reticulum packet.", nameof(packet));
        if (!Packet.TryParse(packet.ToArray(), out _))
            throw new ArgumentException("Data must contain one complete Reticulum packet.", nameof(packet));

        var encoded = new byte[packet.Length * 2 + 2];
        var output = 0;
        encoded[output++] = Flag;
        foreach (var value in packet)
        {
            if (value is Flag or Escape)
            {
                encoded[output++] = Escape;
                encoded[output++] = (byte)(value ^ EscapeMask);
            }
            else
            {
                encoded[output++] = value;
            }
        }

        encoded[output++] = Flag;
        return encoded[..output];
    }

    internal sealed class Decoder
    {
        private readonly List<byte> _frame = [];
        private bool _insideFrame;
        private bool _escaped;
        private bool _discarding;

        internal void Push(ReadOnlySpan<byte> bytes, Action<byte[]> onPacket)
        {
            foreach (var value in bytes)
            {
                if (value == Flag)
                {
                    if (_insideFrame && !_discarding && !_escaped && _frame.Count > 0)
                    {
                        var packet = _frame.ToArray();
                        if (Packet.TryParse(packet, out _))
                            onPacket(packet);
                    }

                    _insideFrame = true;
                    _escaped = false;
                    _discarding = false;
                    _frame.Clear();
                    continue;
                }

                if (!_insideFrame || _discarding)
                    continue;

                if (_escaped)
                {
                    if (value is not (0x5e or 0x5d))
                    {
                        _discarding = true;
                        _frame.Clear();
                        continue;
                    }

                    _frame.Add((byte)(value ^ EscapeMask));
                    _escaped = false;
                }
                else if (value == Escape)
                {
                    _escaped = true;
                }
                else
                {
                    _frame.Add(value);
                }

                if (_frame.Count > Packet.Mtu)
                {
                    _discarding = true;
                    _frame.Clear();
                }
            }
        }
    }
}
