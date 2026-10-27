using System.Buffers.Binary;
using System.Text;

namespace DotReticulum.Core;

/// <summary>
/// Bounded, reflection-free MessagePack application values. See docs/application-values.md.
/// Unpack retains the legacy binary-map-key hex projection; UnpackApplicationValue preserves binary keys.
/// </summary>
public static class MsgPack
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Pack(object? value) => Pack(value, MsgPackLimits.Default);

    public static byte[] Pack(object? value, MsgPackLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        using var ms = new BoundedStream(limits.MaxEncodedBytes);
        Write(ms, value, 0, new Operation(limits, false));
        return ms.ToArray();
    }

    public static object? Unpack(ReadOnlySpan<byte> data) => Unpack(data, MsgPackLimits.Default);

    public static object? Unpack(ReadOnlySpan<byte> data, MsgPackLimits limits) => Decode(data, limits, false);

    /// <summary>Preserves binary map keys, using content and exact numeric key equality.</summary>
    public static object? UnpackApplicationValue(ReadOnlySpan<byte> data, MsgPackLimits? limits = null,
        bool allowExtendedMapKeys = false) =>
        Decode(data, limits ?? MsgPackLimits.Default, true, allowExtendedMapKeys);

    private static object? Decode(ReadOnlySpan<byte> data, MsgPackLimits limits, bool binaryKeys,
        bool extendedMapKeys = false)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (data.Length > limits.MaxEncodedBytes) throw new FormatException("MessagePack encoded-byte limit exceeded.");
        var pos = 0;
        var v = Read(data, ref pos, 0, new Operation(limits, binaryKeys, extendedMapKeys));
        if (pos != data.Length)
        {
            throw new FormatException("Trailing bytes after msgpack value.");
        }

        return v;
    }

    public static bool TryUnpack(ReadOnlySpan<byte> data, out object? value)
        => TryDecode(data, out value, false, MsgPackLimits.Default);

    public static bool TryUnpackApplicationValue(ReadOnlySpan<byte> data, out object? value, MsgPackLimits? limits = null,
        bool allowExtendedMapKeys = false)
        => TryDecode(data, out value, true, limits ?? MsgPackLimits.Default, allowExtendedMapKeys);

    private static bool TryDecode(ReadOnlySpan<byte> data, out object? value, bool binaryKeys, MsgPackLimits limits,
        bool extendedMapKeys = false)
    {
        limits.Validate();
        try
        {
            value = Decode(data, limits, binaryKeys, extendedMapKeys);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or InvalidOperationException)
        {
            value = null;
            return false;
        }
    }

    public static long ToInt64(object? v) => v switch
    {
        long l => l,
        ulong u when u <= long.MaxValue => (long)u,
        double d when double.IsFinite(d) && d >= long.MinValue && d < 9223372036854775808d => (long)d,
        _ => throw new FormatException("Not an integer."),
    };

    public static double ToDouble(object? v) => v switch
    {
        double d => d,
        long l => l,
        ulong u => u,
        _ => throw new FormatException("Not a number."),
    };

    private static void Write(Stream s, object? v, int depth, Operation operation)
    {
        operation.Visit(depth, false);

        switch (v)
        {
            case null: s.WriteByte(0xC0); break;
            case bool b: s.WriteByte(b ? (byte)0xC3 : (byte)0xC2); break;
            case sbyte signedByte: WriteInt(s, signedByte); break;
            case byte unsignedByte: WriteUInt(s, unsignedByte); break;
            case short signedShort: WriteInt(s, signedShort); break;
            case ushort unsignedShort: WriteUInt(s, unsignedShort); break;
            case int i: WriteInt(s, i); break;
            case uint ui: WriteInt(s, ui); break;
            case long l: WriteInt(s, l); break;
            case ulong ul: WriteUInt(s, ul); break;
            case double d:
                s.WriteByte(0xCB);
                WriteBE(s, BitConverter.DoubleToUInt64Bits(d), 8);
                break;
            case float f:
                s.WriteByte(0xCB);
                WriteBE(s, BitConverter.DoubleToUInt64Bits(f), 8);
                break;
            case string str:
                {
                    int length;
                    try { length = Utf8.GetByteCount(str); }
                    catch (EncoderFallbackException ex) { throw new ArgumentException("MessagePack string is not valid UTF-16.", ex); }
                    operation.Bytes((ulong)length, false);
                    if (length > operation.Limits.MaxEncodedBytes - s.Length)
                        throw new ArgumentException("MessagePack encoded-byte limit exceeded.");
                    var bytes = Utf8.GetBytes(str);
                    if (bytes.Length < 32) { s.WriteByte((byte)(0xA0 | bytes.Length)); }
                    else if (bytes.Length < 256) { s.WriteByte(0xD9); s.WriteByte((byte)bytes.Length); }
                    else if (bytes.Length < 65536) { s.WriteByte(0xDA); WriteBE(s, (ulong)bytes.Length, 2); }
                    else { s.WriteByte(0xDB); WriteBE(s, (ulong)bytes.Length, 4); }
                    s.Write(bytes);
                    break;
                }

            case byte[] bin: operation.Bytes((ulong)bin.Length, false); WriteBin(s, bin); break;
            case ReadOnlyMemory<byte> rom: operation.Bytes((ulong)rom.Length, false); WriteBin(s, rom.Span); break;
            case MsgPackExtension extension:
                operation.Bytes((ulong)extension.Data.Length, false);
                WriteExtension(s, extension.Type, extension.Data);
                break;
            case MsgPackTimestamp timestamp:
                operation.Bytes(timestamp.Seconds is >= 0 and <= uint.MaxValue && timestamp.Nanoseconds == 0
                    ? 4UL : timestamp.Seconds is >= 0 and < (1L << 34) ? 8UL : 12UL, false);
                WriteTimestamp(s, timestamp);
                break;
            case object?[] arr:
                operation.Container((ulong)arr.Length, false, false);
                if (arr.Length < 16) { s.WriteByte((byte)(0x90 | arr.Length)); }
                else if (arr.Length < 65536) { s.WriteByte(0xDC); WriteBE(s, (ulong)arr.Length, 2); }
                else { s.WriteByte(0xDD); WriteBE(s, (ulong)arr.Length, 4); }
                foreach (var e in arr) { Write(s, e, depth + 1, operation); }
                break;
            case MsgPackMap extendedMap:
                operation.Container((ulong)extendedMap.Count, true, false);
                WriteMapHeader(s, extendedMap.Count);
                var extendedKeys = new HashSet<object?>(MsgPackExtendedKeyComparer.Instance);
                foreach (var (key, value) in extendedMap)
                {
                    Write(s, key, depth + 1, operation);
                    if (!MsgPackExtendedKeyComparer.IsSupported(key))
                        throw new ArgumentException("MessagePack map keys cannot contain maps or unsupported values.");
                    // NaNs never compare equal; avoid quadratic work from their identical hash codes.
                    if (!MsgPackExtendedKeyComparer.ContainsNaN(key) && !extendedKeys.Add(key))
                        throw new ArgumentException("Duplicate MessagePack map key.");
                    Write(s, value, depth + 1, operation);
                }
                break;
            case IReadOnlyList<KeyValuePair<object, object?>> map:
                operation.Container((ulong)map.Count, true, false);
                WriteMapHeader(s, map.Count);
                WriteEntries(s, map, map.Count, depth, operation);
                break;
            case Dictionary<object, object?> dict:
                operation.Container((ulong)dict.Count, true, false);
                WriteMapHeader(s, dict.Count);
                WriteEntries(s, dict, dict.Count, depth, operation);
                break;
            default:
                throw new ArgumentException("Unsupported msgpack type " + v.GetType().Name);
        }
    }

    private static void WriteEntries(Stream s, IEnumerable<KeyValuePair<object, object?>> entries, int count, int depth, Operation operation)
    {
        var keys = new HashSet<object>(MsgPackKeyComparer.Instance);
        var written = 0;
        foreach (var (key, value) in entries)
        {
            if (written >= count) throw new ArgumentException("MessagePack map changed length while packing.");
            if (!MsgPackKeyComparer.IsSupported(key))
                throw new ArgumentException("MessagePack map keys must be non-null finite scalars, binary, extensions or timestamps.");
            if (!keys.Add(key)) throw new ArgumentException("Duplicate MessagePack map key.");
            Write(s, key, depth + 1, operation);
            Write(s, value, depth + 1, operation);
            written++;
        }
        if (written != count) throw new ArgumentException("MessagePack map changed length while packing.");
    }

    private static void WriteExtension(Stream s, sbyte type, ReadOnlySpan<byte> bytes)
    {
        switch (bytes.Length)
        {
            case 1: s.WriteByte(0xD4); break;
            case 2: s.WriteByte(0xD5); break;
            case 4: s.WriteByte(0xD6); break;
            case 8: s.WriteByte(0xD7); break;
            case 16: s.WriteByte(0xD8); break;
            default:
                if (bytes.Length < 256) { s.WriteByte(0xC7); WriteBE(s, (ulong)bytes.Length, 1); }
                else if (bytes.Length < 65536) { s.WriteByte(0xC8); WriteBE(s, (ulong)bytes.Length, 2); }
                else { s.WriteByte(0xC9); WriteBE(s, (ulong)bytes.Length, 4); }
                break;
        }
        s.WriteByte(unchecked((byte)type));
        s.Write(bytes);
    }

    private static void WriteTimestamp(Stream s, MsgPackTimestamp timestamp)
    {
        Span<byte> bytes = stackalloc byte[12];
        if (timestamp.Nanoseconds == 0 && timestamp.Seconds is >= 0 and <= uint.MaxValue)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)timestamp.Seconds);
            WriteExtension(s, -1, bytes[..4]);
        }
        else if (timestamp.Seconds is >= 0 and < (1L << 34))
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes, ((ulong)timestamp.Nanoseconds << 34) | (ulong)timestamp.Seconds);
            WriteExtension(s, -1, bytes[..8]);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, timestamp.Nanoseconds);
            BinaryPrimitives.WriteInt64BigEndian(bytes[4..], timestamp.Seconds);
            WriteExtension(s, -1, bytes);
        }
    }

    private static void WriteMapHeader(Stream s, int n)
    {
        if (n < 16) { s.WriteByte((byte)(0x80 | n)); }
        else if (n < 65536) { s.WriteByte(0xDE); WriteBE(s, (ulong)n, 2); }
        else { s.WriteByte(0xDF); WriteBE(s, (ulong)n, 4); }
    }

    private static void WriteBin(Stream s, ReadOnlySpan<byte> bin)
    {
        if (bin.Length < 256) { s.WriteByte(0xC4); s.WriteByte((byte)bin.Length); }
        else if (bin.Length < 65536) { s.WriteByte(0xC5); WriteBE(s, (ulong)bin.Length, 2); }
        else { s.WriteByte(0xC6); WriteBE(s, (ulong)bin.Length, 4); }
        s.Write(bin);
    }

    private static void WriteInt(Stream s, long v)
    {
        if (v >= 0) { WriteUInt(s, (ulong)v); }
        else if (v >= -32) { s.WriteByte((byte)v); }
        else if (v >= sbyte.MinValue) { s.WriteByte(0xD0); s.WriteByte((byte)v); }
        else if (v >= short.MinValue) { s.WriteByte(0xD1); WriteBE(s, (ushort)(short)v, 2); }
        else if (v >= int.MinValue) { s.WriteByte(0xD2); WriteBE(s, (uint)(int)v, 4); }
        else { s.WriteByte(0xD3); WriteBE(s, (ulong)v, 8); }
    }

    private static void WriteUInt(Stream s, ulong v)
    {
        if (v < 128) { s.WriteByte((byte)v); }
        else if (v < 256) { s.WriteByte(0xCC); s.WriteByte((byte)v); }
        else if (v < 65536) { s.WriteByte(0xCD); WriteBE(s, v, 2); }
        else if (v <= uint.MaxValue) { s.WriteByte(0xCE); WriteBE(s, v, 4); }
        else { s.WriteByte(0xCF); WriteBE(s, v, 8); }
    }

    private static void WriteBE(Stream s, ulong v, int bytes)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, v);
        s.Write(buf[(8 - bytes)..]);
    }

    private static ulong ReadBE(ReadOnlySpan<byte> d, ref int pos, int n)
    {
        if (d.Length - pos < n)
        {
            throw new FormatException("Truncated msgpack.");
        }

        ulong v = 0;
        for (var i = 0; i < n; i++)
        {
            v = (v << 8) | d[pos + i];
        }

        pos += n;
        return v;
    }

    private static ReadOnlySpan<byte> ReadSpan(ReadOnlySpan<byte> d, ref int pos, ulong n, Operation operation)
    {
        operation.Bytes(n, true);
        if (n > (ulong)(d.Length - pos))
        {
            throw new FormatException("Truncated msgpack.");
        }

        var r = d.Slice(pos, (int)n);
        pos += (int)n;
        return r;
    }

    private static string ReadString(ReadOnlySpan<byte> d, ref int pos, ulong n, Operation operation)
    {
        var bytes = ReadSpan(d, ref pos, n, operation);
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new FormatException("Invalid UTF-8 MessagePack string.", ex); }
    }

    private static object? ReadExtension(ReadOnlySpan<byte> d, ref int pos, ulong n, Operation operation)
    {
        var type = unchecked((sbyte)ReadBE(d, ref pos, 1));
        var bytes = ReadSpan(d, ref pos, n, operation);
        if (type != -1) return new MsgPackExtension(type, bytes);
        long seconds;
        uint nanos;
        switch (bytes.Length)
        {
            case 4:
                seconds = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                nanos = 0;
                break;
            case 8:
                var packed = BinaryPrimitives.ReadUInt64BigEndian(bytes);
                seconds = (long)(packed & 0x3FFFFFFFF);
                nanos = (uint)(packed >> 34);
                break;
            case 12:
                nanos = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                seconds = BinaryPrimitives.ReadInt64BigEndian(bytes[4..]);
                break;
            default: throw new FormatException("MessagePack timestamp must contain 4, 8 or 12 bytes.");
        }
        if (nanos >= 1_000_000_000) throw new FormatException("MessagePack timestamp nanoseconds must be below 1000000000.");
        return new MsgPackTimestamp(seconds, nanos);
    }

    private static object?[] ReadArray(ReadOnlySpan<byte> d, ref int pos, ulong n, int depth, Operation operation)
    {
        operation.Container(n, false, true);
        if (n > (ulong)(d.Length - pos))
        {
            throw new FormatException("Truncated msgpack.");
        }

        var arr = new object?[(int)n];
        for (var i = 0; i < arr.Length; i++)
        {
            arr[i] = Read(d, ref pos, depth + 1, operation);
        }

        return arr;
    }

    private static object ReadMap(ReadOnlySpan<byte> d, ref int pos, ulong n, int depth, Operation operation)
    {
        operation.Container(n, true, true);
        if (n > (ulong)(d.Length - pos) / 2)
        {
            throw new FormatException("Truncated msgpack.");
        }

        if (operation.ExtendedMapKeys)
        {
            var entries = new KeyValuePair<object?, object?>[(int)n];
            var keys = new HashSet<object?>(MsgPackExtendedKeyComparer.Instance);
            var extended = false;
            for (var i = 0; i < entries.Length; i++)
            {
                var key = Read(d, ref pos, depth + 1, operation);
                if (!MsgPackExtendedKeyComparer.IsSupported(key))
                    throw new FormatException("MessagePack map keys cannot contain maps or unsupported values.");
                if (!MsgPackExtendedKeyComparer.ContainsNaN(key) && !keys.Add(key))
                    throw new FormatException("Duplicate MessagePack map key.");
                extended |= !MsgPackKeyComparer.IsSupported(key);
                entries[i] = new(key, Read(d, ref pos, depth + 1, operation));
            }
            if (extended) return new MsgPackMap(entries);
            var ordinary = new Dictionary<object, object?>(entries.Length, MsgPackKeyComparer.Instance);
            foreach (var (key, value) in entries) ordinary.Add(key!, value);
            return ordinary;
        }

        var map = new Dictionary<object, object?>((int)n, MsgPackKeyComparer.Instance);
        for (var i = 0; i < (int)n; i++)
        {
            var k = Read(d, ref pos, depth + 1, operation);
            if (!MsgPackKeyComparer.IsSupported(k))
                throw new FormatException("MessagePack map keys must be non-null finite scalars, binary, extensions or timestamps.");
            var key = !operation.BinaryKeys && k is byte[] kb ? Convert.ToHexString(kb) : k!;
            if (map.ContainsKey(key)) throw new FormatException("Duplicate MessagePack map key (or legacy binary/text key collision).");
            map.Add(key, Read(d, ref pos, depth + 1, operation));
        }

        return map;
    }

    private static object? Read(ReadOnlySpan<byte> d, ref int pos, int depth, Operation operation)
    {
        operation.Visit(depth, true);

        if (pos >= d.Length)
        {
            throw new FormatException("Truncated msgpack.");
        }

        int t = d[pos++];
        if (t <= 0x7F) { return (long)t; }
        if (t >= 0xE0) { return (long)(sbyte)t; }
        if (t is >= 0x80 and <= 0x8F) { return ReadMap(d, ref pos, (ulong)(t & 0x0F), depth, operation); }
        if (t is >= 0x90 and <= 0x9F) { return ReadArray(d, ref pos, (ulong)(t & 0x0F), depth, operation); }
        if (t is >= 0xA0 and <= 0xBF) { return ReadString(d, ref pos, (ulong)(t & 0x1F), operation); }

        switch (t)
        {
            case 0xC0: return null;
            case 0xC2: return false;
            case 0xC3: return true;
            case 0xC4: return ReadSpan(d, ref pos, ReadBE(d, ref pos, 1), operation).ToArray();
            case 0xC5: return ReadSpan(d, ref pos, ReadBE(d, ref pos, 2), operation).ToArray();
            case 0xC6: return ReadSpan(d, ref pos, ReadBE(d, ref pos, 4), operation).ToArray();
            case 0xC7: return ReadExtension(d, ref pos, ReadBE(d, ref pos, 1), operation);
            case 0xC8: return ReadExtension(d, ref pos, ReadBE(d, ref pos, 2), operation);
            case 0xC9: return ReadExtension(d, ref pos, ReadBE(d, ref pos, 4), operation);
            case 0xCA: return (double)BitConverter.UInt32BitsToSingle((uint)ReadBE(d, ref pos, 4));
            case 0xCB: return BitConverter.UInt64BitsToDouble(ReadBE(d, ref pos, 8));
            case 0xCC: return (long)ReadBE(d, ref pos, 1);
            case 0xCD: return (long)ReadBE(d, ref pos, 2);
            case 0xCE: return (long)ReadBE(d, ref pos, 4);
            case 0xCF:
                {
                    var u = ReadBE(d, ref pos, 8);
                    return u <= long.MaxValue ? (object)(long)u : u;
                }

            case 0xD0: return (long)(sbyte)ReadBE(d, ref pos, 1);
            case 0xD1: return (long)(short)ReadBE(d, ref pos, 2);
            case 0xD2: return (long)(int)ReadBE(d, ref pos, 4);
            case 0xD3: return (long)ReadBE(d, ref pos, 8);
            case 0xD4: return ReadExtension(d, ref pos, 1, operation);
            case 0xD5: return ReadExtension(d, ref pos, 2, operation);
            case 0xD6: return ReadExtension(d, ref pos, 4, operation);
            case 0xD7: return ReadExtension(d, ref pos, 8, operation);
            case 0xD8: return ReadExtension(d, ref pos, 16, operation);
            case 0xD9: return ReadString(d, ref pos, ReadBE(d, ref pos, 1), operation);
            case 0xDA: return ReadString(d, ref pos, ReadBE(d, ref pos, 2), operation);
            case 0xDB: return ReadString(d, ref pos, ReadBE(d, ref pos, 4), operation);
            case 0xDC: return ReadArray(d, ref pos, ReadBE(d, ref pos, 2), depth, operation);
            case 0xDD: return ReadArray(d, ref pos, ReadBE(d, ref pos, 4), depth, operation);
            case 0xDE: return ReadMap(d, ref pos, ReadBE(d, ref pos, 2), depth, operation);
            case 0xDF: return ReadMap(d, ref pos, ReadBE(d, ref pos, 4), depth, operation);
            default: throw new FormatException("Unsupported msgpack type byte 0x" + t.ToString("X2"));
        }
    }

    private sealed class Operation(MsgPackLimits limits, bool binaryKeys, bool extendedMapKeys = false)
    {
        private int _values;
        internal MsgPackLimits Limits { get; } = limits;
        internal bool BinaryKeys { get; } = binaryKeys;
        internal bool ExtendedMapKeys { get; } = extendedMapKeys;

        internal void Visit(int depth, bool reading)
        {
            if (depth > Limits.MaxDepth) Fail("MessagePack nesting-depth limit exceeded.", reading);
            if (_values >= Limits.MaxValues) Fail("MessagePack total-value limit exceeded.", reading);
            _values++;
        }

        internal void Bytes(ulong count, bool reading)
        {
            if (count > (ulong)Limits.MaxValueBytes) Fail("MessagePack value-byte limit exceeded.", reading);
        }

        internal void Container(ulong count, bool map, bool reading)
        {
            if (count > (ulong)Limits.MaxContainerEntries) Fail("MessagePack container-entry limit exceeded.", reading);
            if (count * (map ? 2UL : 1UL) > (ulong)(Limits.MaxValues - _values))
                Fail("MessagePack total-value limit exceeded.", reading);
        }

        private static void Fail(string message, bool reading)
        {
            if (reading) throw new FormatException(message);
            throw new ArgumentException(message);
        }
    }

    private sealed class BoundedStream(int maximum) : MemoryStream
    {
        private void Check(int length)
        {
            if (length > maximum - Length) throw new ArgumentException("MessagePack encoded-byte limit exceeded.");
        }

        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
    }
}
