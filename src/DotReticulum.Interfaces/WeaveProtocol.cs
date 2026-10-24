using DotReticulum.Crypto;

namespace DotReticulum.Interfaces;

/// <summary>WDCL framing and discovery primitives from the pinned Weave reference.</summary>
public static class WeaveProtocol
{
    public const int MaximumFrameLength = 32768;
    public const int MaximumPacketLength = 1024 + 64;
    public const byte Discover = 0, Connect = 1, Command = 2, Log = 3, Display = 4, EndpointPacket = 5, Encapsulation = 6;

    public static byte[] Frame(ReadOnlySpan<byte> address, byte type, ReadOnlySpan<byte> payload)
    {
        if (address.Length != 4) throw new ArgumentException("A WDCL address is four bytes.", nameof(address));
        if (payload.Length > MaximumFrameLength - 5) throw new ArgumentOutOfRangeException(nameof(payload));
        var raw = new byte[5 + payload.Length];
        address.CopyTo(raw);
        raw[4] = type;
        payload.CopyTo(raw.AsSpan(5));
        return Encode(raw);
    }

    public static byte[] Encode(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > MaximumFrameLength) throw new ArgumentOutOfRangeException(nameof(raw));
        var result = new List<byte>(raw.Length + 2) { 0x7e };
        foreach (var b in raw)
        {
            if (b is 0x7d or 0x7e) { result.Add(0x7d); result.Add((byte)(b ^ 0x20)); }
            else result.Add(b);
        }
        result.Add(0x7e);
        return result.ToArray();
    }

    public static bool ValidateDiscovery(ReadOnlySpan<byte> raw, out byte[] publicKey)
    {
        publicKey = [];
        if (raw.Length != 101 || raw[4] != Discover ||
            !Ed25519.Verify(raw.Slice(5, 32), raw[..4], raw[37..])) return false;
        publicKey = raw.Slice(5, 32).ToArray();
        return true;
    }

    public sealed class Decoder
    {
        private readonly byte[] _buffer = new byte[MaximumFrameLength];
        private int _length;
        private bool _inside, _escape, _invalid;
        public long DroppedFrames { get; private set; }

        public void Feed(ReadOnlySpan<byte> bytes, Action<byte[]> receive)
        {
            foreach (var b in bytes)
            {
                if (b == 0x7e)
                {
                    if (_inside && _length > 0)
                    {
                        if (!_invalid && !_escape && _length > 5) receive(_buffer.AsSpan(0, _length).ToArray());
                        else DroppedFrames++;
                    }
                    _inside = true; _escape = false; _invalid = false; _length = 0;
                    continue;
                }
                if (!_inside || _invalid) continue;
                var value = b;
                if (_escape)
                {
                    _escape = false;
                    if (b is not (0x5d or 0x5e)) { _invalid = true; continue; }
                    value = (byte)(b ^ 0x20);
                }
                else if (b == 0x7d) { _escape = true; continue; }
                if (_length == _buffer.Length) { _invalid = true; continue; }
                _buffer[_length++] = value;
            }
        }
    }
}
