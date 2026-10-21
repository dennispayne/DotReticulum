using System.Buffers.Binary;
using DotReticulum.Core;

namespace DotReticulum.Applications.Runtime;

/// <summary>Reads a binary-keyed persistence map without the request codec's binary-key-to-hex conversion.
/// Values are delegated to the existing codec after a bounded structural scan.</summary>
internal static class PersistenceMessagePack
{
    public static IReadOnlyList<KeyValuePair<byte[], object?>> BinaryMap(ReadOnlySpan<byte> packed, int keyLength)
    {
        var reader = new Reader(packed);
        var count = reader.MapLength();
        PersistenceFormats.Bound(count, "persistence map");
        var result = new List<KeyValuePair<byte[], object?>>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = reader.BinaryKey(keyLength);
            if (!seen.Add(Convert.ToHexString(key))) throw new InvalidDataException("Duplicate binary persistence key.");
            result.Add(new(key, reader.Value()));
        }
        if (!reader.End) throw new InvalidDataException("Trailing persistence map bytes.");
        return result;
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _remaining = data;
        public bool End => _remaining.IsEmpty;
        private byte Byte() => Take(1)[0];
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _remaining.Length) throw new InvalidDataException("Truncated persistence MessagePack.");
            var value = _remaining[..count];
            _remaining = _remaining[count..];
            return value;
        }
        private int Length(int bytes) => bytes switch
        {
            1 => Byte(),
            2 => BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
            4 => checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))),
            _ => throw new InvalidDataException("Invalid persistence length."),
        };
        public int MapLength()
        {
            var code = Byte();
            return code switch
            {
                >= 0x80 and <= 0x8f => code & 15,
                0xde => Length(2),
                0xdf => Length(4),
                _ => throw new InvalidDataException("Persistence data must be a map."),
            };
        }
        public byte[] BinaryKey(int length)
        {
            var size = Byte() switch
            {
                0xc4 => Length(1), 0xc5 => Length(2), 0xc6 => Length(4),
                _ => throw new InvalidDataException("Persistence key must be binary, not a string."),
            };
            if (size != length) throw new InvalidDataException($"Persistence key must be exactly {length} bytes.");
            return Take(size).ToArray();
        }
        public object? Value()
        {
            var start = _remaining;
            Skip(0);
            return MsgPack.Unpack(start[..(start.Length - _remaining.Length)]);
        }
        private void Skip(int depth)
        {
            if (depth > 16) throw new InvalidDataException("Persistence nesting exceeds limit.");
            var code = Byte();
            if (code <= 0x7f || code >= 0xe0 || code is 0xc0 or 0xc2 or 0xc3) return;
            if (code is >= 0xa0 and <= 0xbf) { Take(code & 31); return; }
            int count;
            if (code is >= 0x90 and <= 0x9f) count = code & 15;
            else if (code is >= 0x80 and <= 0x8f) count = 2 * (code & 15);
            else
            {
                switch (code)
                {
                    case 0xc4: case 0xd9: Take(Length(1)); return;
                    case 0xc5: case 0xda: Take(Length(2)); return;
                    case 0xc6: case 0xdb: Take(Length(4)); return;
                    case 0xca: case 0xce: case 0xd2: Take(4); return;
                    case 0xcb: case 0xcf: case 0xd3: Take(8); return;
                    case 0xcc: case 0xd0: Take(1); return;
                    case 0xcd: case 0xd1: Take(2); return;
                    case 0xdc: count = Length(2); break;
                    case 0xdd: count = Length(4); break;
                    case 0xde: count = checked(2 * Length(2)); break;
                    case 0xdf: count = checked(2 * Length(4)); break;
                    default: throw new InvalidDataException("Unsupported persistence MessagePack code.");
                }
            }
            if (count > 2 * PersistenceFormats.MaximumEntries) throw new InvalidDataException("Persistence collection exceeds limit.");
            for (var index = 0; index < count; index++) Skip(depth + 1);
        }
    }
}
