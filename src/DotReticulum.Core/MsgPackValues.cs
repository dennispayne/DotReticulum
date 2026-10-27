using System.Collections;
using System.Numerics;

namespace DotReticulum.Core;

/// <summary>Per-operation allocation/work bounds. Depth counts edges from the root.</summary>
public sealed record MsgPackLimits
{
    public static MsgPackLimits Default { get; } = new();
    public int MaxEncodedBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxValueBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxContainerEntries { get; init; } = 1_000_000;
    public int MaxValues { get; init; } = 2_000_001;
    public int MaxDepth { get; init; } = 32;

    internal void Validate()
    {
        if (MaxEncodedBytes < 1 || MaxValueBytes < 0 || MaxContainerEntries < 0 ||
            MaxValues < 1 || MaxDepth is < 0 or > 128)
            throw new ArgumentOutOfRangeException(nameof(MsgPackLimits), "Invalid MessagePack limits (depth must be 0..128).");
    }
}

/// <summary>Opaque application extension. Type -1 is reserved for <see cref="MsgPackTimestamp"/>.</summary>
public sealed class MsgPackExtension : IEquatable<MsgPackExtension>
{
    private readonly byte[] _data;

    public MsgPackExtension(sbyte type, ReadOnlySpan<byte> data)
    {
        if (type == -1) throw new ArgumentException("Extension -1 requires MsgPackTimestamp.", nameof(type));
        Type = type;
        _data = data.ToArray();
    }

    public sbyte Type { get; }
    public ReadOnlySpan<byte> Data => _data;
    public bool Equals(MsgPackExtension? other) =>
        other is not null && Type == other.Type && Data.SequenceEqual(other.Data);
    public override bool Equals(object? obj) => obj is MsgPackExtension other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        hash.AddBytes(Data);
        return hash.ToHashCode();
    }
}

/// <summary>UTC Unix seconds and nanoseconds, without DateTime range or precision loss.</summary>
public readonly record struct MsgPackTimestamp
{
    public MsgPackTimestamp(long seconds, uint nanoseconds = 0)
    {
        if (nanoseconds >= 1_000_000_000) throw new ArgumentOutOfRangeException(nameof(nanoseconds));
        Seconds = seconds;
        Nanoseconds = nanoseconds;
    }

    public long Seconds { get; }
    public uint Nanoseconds { get; }
}

/// <summary>Ordered application map supporting nil, tuple-array and nonfinite numeric keys.</summary>
public sealed class MsgPackMap : IReadOnlyList<KeyValuePair<object?, object?>>
{
    private readonly KeyValuePair<object?, object?>[] _entries;

    public MsgPackMap(params KeyValuePair<object?, object?>[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToArray();
    }

    public int Count => _entries.Length;
    public KeyValuePair<object?, object?> this[int index] => _entries[index];
    public IEnumerator<KeyValuePair<object?, object?>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<object?, object?>>)_entries).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class MsgPackExtendedKeyComparer : IEqualityComparer<object?>
{
    internal static MsgPackExtendedKeyComparer Instance { get; } = new();

    internal static bool IsSupported(object? key) => key is null ||
        key is float or double || MsgPackKeyComparer.IsSupported(key) ||
        key is object?[] items && items.All(IsSupported);

    internal static bool ContainsNaN(object? key) => key is double d && double.IsNaN(d) ||
        key is float f && float.IsNaN(f) || key is object?[] items && items.Any(ContainsNaN);

    public new bool Equals(object? x, object? y)
    {
        if (x is object?[] left && y is object?[] right)
        {
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
                if (!Equals(left[i], right[i])) return false;
            return true;
        }
        return MsgPackKeyComparer.Instance.Equals(x, y);
    }

    public int GetHashCode(object? obj)
    {
        if (obj is null) return 0;
        if (obj is not object?[] items) return MsgPackKeyComparer.Instance.GetHashCode(obj);
        var hash = new HashCode();
        foreach (var item in items) hash.Add(GetHashCode(item));
        return hash.ToHashCode();
    }
}

internal sealed class MsgPackKeyComparer : IEqualityComparer<object>
{
    internal static MsgPackKeyComparer Instance { get; } = new();

    internal static bool IsSupported(object? key) => key is
        bool or sbyte or byte or short or ushort or int or uint or long or ulong or string or
        byte[] or ReadOnlyMemory<byte> or MsgPackExtension or MsgPackTimestamp ||
        key is double d && double.IsFinite(d) || key is float f && float.IsFinite(f);

    private static bool Integer(object value, out BigInteger integer)
    {
        switch (value)
        {
            case bool b: integer = b ? BigInteger.One : BigInteger.Zero; return true;
            case sbyte n: integer = n; return true;
            case byte n: integer = n; return true;
            case short n: integer = n; return true;
            case ushort n: integer = n; return true;
            case int n: integer = n; return true;
            case uint n: integer = n; return true;
            case long n: integer = n; return true;
            case ulong n: integer = n; return true;
            case double n when double.IsFinite(n) && Math.Truncate(n) == n:
                integer = new BigInteger(n); return true;
            case float n when float.IsFinite(n) && MathF.Truncate(n) == n:
                integer = new BigInteger(n); return true;
            default: integer = default; return false;
        }
    }

    private static bool Binary(object value, out ReadOnlyMemory<byte> bytes)
    {
        if (value is byte[] array) { bytes = array; return true; }
        if (value is ReadOnlyMemory<byte> memory) { bytes = memory; return true; }
        bytes = default;
        return false;
    }

    public new bool Equals(object? x, object? y)
    {
        if (x is null || y is null) return x is null && y is null;
        if (Integer(x, out var xi) && Integer(y, out var yi)) return xi == yi;
        if (Binary(x, out var xb) && Binary(y, out var yb)) return xb.Span.SequenceEqual(yb.Span);
        if (x is float or double && y is float or double) return Convert.ToDouble(x) == Convert.ToDouble(y);
        return x.Equals(y);
    }

    public int GetHashCode(object obj)
    {
        if (Integer(obj, out var integer)) return integer.GetHashCode();
        if (Binary(obj, out var bytes))
        {
            var hash = new HashCode();
            hash.AddBytes(bytes.Span);
            return hash.ToHashCode();
        }
        return obj is float or double ? Convert.ToDouble(obj).GetHashCode() : obj.GetHashCode();
    }
}
