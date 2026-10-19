using System.Buffers.Binary;
using System.Text;

namespace DotReticulum.Core;

/// <summary>
/// Minimal MessagePack reader/writer matching the subset upstream's bundled umsgpack emits:
/// null, bool, integers (smallest encoding), float64, str, bin, arrays and maps. No reflection.
/// Values: null, bool, long, ulong (only above long.MaxValue), double, string, byte[], object?[], Dictionary&lt;object, object?&gt;.
/// </summary>
public static class MsgPack
{
    private const int MaxDepth = 32;

    public static byte[] Pack(object? value)
    {
        using var ms = new MemoryStream();
        Write(ms, value, 0);
        return ms.ToArray();
    }

    public static object? Unpack(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var v = Read(data, ref pos, 0);
        if (pos != data.Length)
        {
            throw new FormatException("Trailing bytes after msgpack value.");
        }

        return v;
    }

    public static bool TryUnpack(ReadOnlySpan<byte> data, out object? value)
    {
        try
        {
            value = Unpack(data);
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
        double d => (long)d,
        _ => throw new FormatException("Not an integer."),
    };

    public static double ToDouble(object? v) => v switch
    {
        double d => d,
        long l => l,
        ulong u => u,
        _ => throw new FormatException("Not a number."),
    };

    private static void Write(Stream s, object? v, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ArgumentException("Nesting too deep.");
        }

        switch (v)
        {
            case null: s.WriteByte(0xC0); break;
            case bool b: s.WriteByte(b ? (byte)0xC3 : (byte)0xC2); break;
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
                    var bytes = Encoding.UTF8.GetBytes(str);
                    if (bytes.Length < 32) { s.WriteByte((byte)(0xA0 | bytes.Length)); }
                    else if (bytes.Length < 256) { s.WriteByte(0xD9); s.WriteByte((byte)bytes.Length); }
                    else if (bytes.Length < 65536) { s.WriteByte(0xDA); WriteBE(s, (ulong)bytes.Length, 2); }
                    else { s.WriteByte(0xDB); WriteBE(s, (ulong)bytes.Length, 4); }
                    s.Write(bytes);
                    break;
                }

            case byte[] bin: WriteBin(s, bin); break;
            case ReadOnlyMemory<byte> rom: WriteBin(s, rom.Span); break;
            case object?[] arr:
                if (arr.Length < 16) { s.WriteByte((byte)(0x90 | arr.Length)); }
                else if (arr.Length < 65536) { s.WriteByte(0xDC); WriteBE(s, (ulong)arr.Length, 2); }
                else { s.WriteByte(0xDD); WriteBE(s, (ulong)arr.Length, 4); }
                foreach (var e in arr) { Write(s, e, depth + 1); }
                break;
            case IReadOnlyList<KeyValuePair<object, object?>> map:
                WriteMapHeader(s, map.Count);
                foreach (var kv in map) { Write(s, kv.Key, depth + 1); Write(s, kv.Value, depth + 1); }
                break;
            case Dictionary<object, object?> dict:
                WriteMapHeader(s, dict.Count);
                foreach (var kv in dict) { Write(s, kv.Key, depth + 1); Write(s, kv.Value, depth + 1); }
                break;
            default:
                throw new ArgumentException("Unsupported msgpack type " + v.GetType().Name);
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

    private static byte[] ReadBytes(ReadOnlySpan<byte> d, ref int pos, ulong n)
    {
        if (n > (ulong)(d.Length - pos))
        {
            throw new FormatException("Truncated msgpack.");
        }

        var r = d.Slice(pos, (int)n).ToArray();
        pos += (int)n;
        return r;
    }

    private static object?[] ReadArray(ReadOnlySpan<byte> d, ref int pos, ulong n, int depth)
    {
        if (n > (ulong)(d.Length - pos))
        {
            throw new FormatException("Truncated msgpack.");
        }

        var arr = new object?[n];
        for (var i = 0; i < arr.Length; i++)
        {
            arr[i] = Read(d, ref pos, depth + 1);
        }

        return arr;
    }

    private static Dictionary<object, object?> ReadMap(ReadOnlySpan<byte> d, ref int pos, ulong n, int depth)
    {
        if (n > (ulong)(d.Length - pos))
        {
            throw new FormatException("Truncated msgpack.");
        }

        var map = new Dictionary<object, object?>((int)n);
        for (var i = 0; i < (int)n; i++)
        {
            var k = Read(d, ref pos, depth + 1) ?? throw new FormatException("Null map key.");
            map[k is byte[] kb ? Convert.ToHexString(kb) : k] = Read(d, ref pos, depth + 1);
        }

        return map;
    }

    private static object? Read(ReadOnlySpan<byte> d, ref int pos, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException("Nesting too deep.");
        }

        if (pos >= d.Length)
        {
            throw new FormatException("Truncated msgpack.");
        }

        int t = d[pos++];
        if (t <= 0x7F) { return (long)t; }
        if (t >= 0xE0) { return (long)(sbyte)t; }
        if (t is >= 0x80 and <= 0x8F) { return ReadMap(d, ref pos, (ulong)(t & 0x0F), depth); }
        if (t is >= 0x90 and <= 0x9F) { return ReadArray(d, ref pos, (ulong)(t & 0x0F), depth); }
        if (t is >= 0xA0 and <= 0xBF) { return Encoding.UTF8.GetString(ReadBytes(d, ref pos, (ulong)(t & 0x1F))); }

        switch (t)
        {
            case 0xC0: return null;
            case 0xC2: return false;
            case 0xC3: return true;
            case 0xC4: return ReadBytes(d, ref pos, ReadBE(d, ref pos, 1));
            case 0xC5: return ReadBytes(d, ref pos, ReadBE(d, ref pos, 2));
            case 0xC6: return ReadBytes(d, ref pos, ReadBE(d, ref pos, 4));
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
            case 0xD9: return Encoding.UTF8.GetString(ReadBytes(d, ref pos, ReadBE(d, ref pos, 1)));
            case 0xDA: return Encoding.UTF8.GetString(ReadBytes(d, ref pos, ReadBE(d, ref pos, 2)));
            case 0xDB: return Encoding.UTF8.GetString(ReadBytes(d, ref pos, ReadBE(d, ref pos, 4)));
            case 0xDC: return ReadArray(d, ref pos, ReadBE(d, ref pos, 2), depth);
            case 0xDD: return ReadArray(d, ref pos, ReadBE(d, ref pos, 4), depth);
            case 0xDE: return ReadMap(d, ref pos, ReadBE(d, ref pos, 2), depth);
            case 0xDF: return ReadMap(d, ref pos, ReadBE(d, ref pos, 4), depth);
            default: throw new FormatException("Unsupported msgpack type byte 0x" + t.ToString("X2"));
        }
    }
}
