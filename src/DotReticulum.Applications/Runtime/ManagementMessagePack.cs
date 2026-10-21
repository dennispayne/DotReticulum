using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Runtime;

/// <summary>Allowlisted MessagePack values with binary map keys preserved, bounded allocations, and no object activation.</summary>
public static class ManagementMessagePack
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Pack(object? value)
    {
        long budget = ManagementConnection.MaximumMessageBytes;
        Validate(value, 0, ref budget);
        var bytes = MsgPack.Pack(value);
        if (bytes.Length > ManagementConnection.MaximumMessageBytes) throw new InvalidDataException("Oversized management value.");
        return bytes;
    }

    private static void Validate(object? value, int depth, ref long budget)
    {
        if (depth > 32) throw new ArgumentException("Management MessagePack nesting exceeds bound.");
        budget -= value switch
        {
            null or bool => 1,
            int or uint or long or ulong or double or float => 9,
            string text => 5L + Utf8.GetByteCount(text),
            byte[] bytes => 5L + bytes.Length,
            ReadOnlyMemory<byte> bytes => 5L + bytes.Length,
            object?[] => 5,
            Dictionary<object, object?> => 5,
            IReadOnlyList<KeyValuePair<object, object?>> => 5,
            _ => throw new ArgumentException("Unsupported management MessagePack type " + value.GetType().Name),
        };
        if (budget < 0) throw new InvalidDataException("Management value exceeds the bounded serialization budget.");
        if (value is object?[] array)
            foreach (var item in array) Validate(item, depth + 1, ref budget);
        if (value is Dictionary<object, object?> dictionary)
            foreach (var pair in dictionary)
            {
                ValidateKey(pair.Key);
                Validate(pair.Key, depth + 1, ref budget);
                Validate(pair.Value, depth + 1, ref budget);
            }
        if (value is IReadOnlyList<KeyValuePair<object, object?>> map)
            foreach (var pair in map)
            {
                ValidateKey(pair.Key);
                Validate(pair.Key, depth + 1, ref budget);
                Validate(pair.Value, depth + 1, ref budget);
            }
    }
    private static void ValidateKey(object key)
    {
        if (key is not (string or byte[] or ReadOnlyMemory<byte> or int or uint or long or ulong or float or double or bool))
            throw new ArgumentException("Unsupported management MessagePack map key.");
    }

    public static object? Unpack(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ManagementConnection.MaximumMessageBytes) throw new InvalidDataException("Oversized management value.");
        var reader = new Reader(bytes);
        var value = reader.Read(0);
        if (!reader.Done) throw new InvalidDataException("Trailing management MessagePack data.");
        return value;
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> remaining = bytes;
        public bool Done => remaining.IsEmpty;
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > remaining.Length) throw new InvalidDataException("Truncated management MessagePack.");
            var result = remaining[..count];
            remaining = remaining[count..];
            return result;
        }
        private ulong Number(int size)
        {
            var span = Take(size);
            ulong result = 0;
            foreach (var b in span) result = (result << 8) | b;
            return result;
        }
        private int Count(int size)
        {
            var count = Number(size);
            if (count > (ulong)remaining.Length) throw new InvalidDataException("Invalid management collection length.");
            return (int)count;
        }
        public object? Read(int depth)
        {
            if (depth > 32) throw new InvalidDataException("Management MessagePack nesting exceeds bound.");
            var code = Take(1)[0];
            if (code < 0x80) return (long)code;
            if (code >= 0xe0) return (long)(sbyte)code;
            if ((code & 0xe0) == 0xa0) return Utf8.GetString(Take(code & 31));
            if ((code & 0xf0) == 0x90) return Array(code & 15, depth);
            if ((code & 0xf0) == 0x80) return Map(code & 15, depth);
            return code switch
            {
                0xc0 => null, 0xc2 => false, 0xc3 => true,
                0xc4 => Take(Count(1)).ToArray(), 0xc5 => Take(Count(2)).ToArray(), 0xc6 => Take(Count(4)).ToArray(),
                0xca => (double)BitConverter.UInt32BitsToSingle((uint)Number(4)),
                0xcb => BitConverter.UInt64BitsToDouble(Number(8)),
                0xcc => (long)Number(1), 0xcd => (long)Number(2), 0xce => (long)Number(4),
                0xcf => Unsigned(),
                0xd0 => (long)(sbyte)Number(1), 0xd1 => (long)(short)Number(2),
                0xd2 => (long)(int)Number(4), 0xd3 => (long)Number(8),
                0xd9 => Utf8.GetString(Take(Count(1))), 0xda => Utf8.GetString(Take(Count(2))),
                0xdb => Utf8.GetString(Take(Count(4))),
                0xdc => Array(Count(2), depth), 0xdd => Array(Count(4), depth),
                0xde => Map(Count(2), depth), 0xdf => Map(Count(4), depth),
                _ => throw new InvalidDataException($"Unsupported management MessagePack opcode 0x{code:x2}; extensions/pickle are not accepted."),
            };
        }
        private object Unsigned()
        {
            var number = Number(8);
            return number <= long.MaxValue ? (object)(long)number : number;
        }
        private object?[] Array(int count, int depth)
        {
            if (count > remaining.Length) throw new InvalidDataException("Invalid management array size.");
            var array = new object?[count];
            for (var i = 0; i < count; i++) array[i] = Read(depth + 1);
            return array;
        }
        private Dictionary<object, object?> Map(int count, int depth)
        {
            if (count > remaining.Length / 2) throw new InvalidDataException("Invalid management map size.");
            var map = new Dictionary<object, object?>(ByteKeys.Instance);
            for (var i = 0; i < count; i++)
            {
                var key = Read(depth + 1);
                if (key is not (string or byte[] or long or ulong or double or bool))
                    throw new InvalidDataException("Unsupported management map key.");
                if (!map.TryAdd(key, Read(depth + 1))) throw new InvalidDataException("Duplicate management map key.");
            }
            return map;
        }
    }
    private sealed class ByteKeys : IEqualityComparer<object>
    {
        public static readonly ByteKeys Instance = new();
        public new bool Equals(object? x, object? y) => x is byte[] a && y is byte[] b ? a.AsSpan().SequenceEqual(b) : object.Equals(x, y);
        public int GetHashCode(object obj)
        {
            if (obj is not byte[] bytes) return obj.GetHashCode();
            var hash = new HashCode();
            foreach (var b in bytes) hash.Add(b);
            return hash.ToHashCode();
        }
    }
}
