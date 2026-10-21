using System.Buffers.Binary;
using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Runtime;

/// <summary>A detached identity policy record. Source is the published provenance; ObtainedFrom is the authenticated supplier.</summary>
public sealed record BlackholeEntry(byte[] IdentityHash, byte[] Source, double? Until, string? Reason, byte[]? ObtainedFrom);

internal sealed record BlackholeRecord(byte[] Hash, byte[] Source, object? Until, string? Reason, byte[]? ObtainedFrom)
{
    public BlackholeEntry Snapshot() => new(Hash.ToArray(), Source.ToArray(), Until is null ? null : MsgPack.ToDouble(Until), Reason, ObtainedFrom?.ToArray());
}

/// <summary>Bounded, binary-key-preserving codec for Transport.blackholed_identities.</summary>
internal static class BlackholeWire
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public const int MaximumEntries = 10_000;
    public const int MaximumReasonBytes = 4096;

    public static IReadOnlyList<KeyValuePair<object, object?>> Value(IEnumerable<BlackholeRecord> records) =>
        records.Select(r => new KeyValuePair<object, object?>(r.Hash.ToArray(),
            new Dictionary<object, object?> { ["source"] = r.Source.ToArray(), ["until"] = r.Until, ["reason"] = r.Reason })).ToArray();

    public static byte[] Pack(IEnumerable<BlackholeRecord> records) => MsgPack.Pack(Value(records));

    public static byte[] ExtractResponse(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> expectedRequestId)
    {
        if (envelope.Length > MaximumBytes + 32) throw new InvalidDataException("Blackhole response exceeds the byte limit.");
        var reader = new Reader(envelope);
        if (reader.ArrayLength() != 2) throw new FormatException("Invalid blackhole response envelope.");
        if (!reader.BinaryHash().AsSpan().SequenceEqual(expectedRequestId)) throw new FormatException("Blackhole response request ID mismatch.");
        // Core's general decoder normalises binary map keys to hex strings. Parse the
        // original response here so text keys cannot become effective identity hashes.
        var packed = reader.Remaining.ToArray();
        Read(packed);
        return packed;
    }

    public static List<BlackholeRecord> Read(ReadOnlySpan<byte> bytes, byte[]? fallbackSource = null)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Blackhole list exceeds the byte limit.");
        var reader = new Reader(bytes);
        var count = reader.MapLength();
        if (count > MaximumEntries) throw new InvalidDataException("Blackhole list exceeds the identity limit.");
        var records = new List<BlackholeRecord>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var hash = reader.BinaryHash();
            if (!seen.Add(Convert.ToHexString(hash))) throw new FormatException("Duplicate blackhole identity.");
            var fields = reader.MapLength();
            if (fields > 3) throw new FormatException("Unexpected blackhole entry fields.");
            byte[]? source = null;
            object? until = null;
            string? reason = null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < fields; j++)
            {
                var name = reader.String();
                if (!names.Add(name)) throw new FormatException("Duplicate blackhole entry field.");
                switch (name)
                {
                    case "source": source = reader.BinaryHash(); break;
                    case "until": until = reader.NumberOrNull(); break;
                    case "reason": reason = reader.Null() ? null : reader.String(); break;
                    default: throw new FormatException("Unknown blackhole entry field.");
                }
            }

            source ??= fallbackSource ?? throw new FormatException("Missing blackhole source.");
            records.Add(new BlackholeRecord(hash, source, until, reason, null));
        }

        if (!reader.End) throw new FormatException("Trailing blackhole list data.");
        return records;
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _remaining = bytes;
        public bool End => _remaining.IsEmpty;
        public ReadOnlySpan<byte> Remaining => _remaining;
        private byte Byte()
        {
            if (_remaining.IsEmpty) throw new FormatException("Truncated blackhole list.");
            var result = _remaining[0];
            _remaining = _remaining[1..];
            return result;
        }

        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || length > _remaining.Length) throw new FormatException("Truncated blackhole value.");
            var result = _remaining[..length];
            _remaining = _remaining[length..];
            return result;
        }

        private int Length(int size) => size switch
        {
            1 => Byte(),
            2 => BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
            4 => checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))),
            _ => throw new FormatException("Invalid blackhole length."),
        };

        public int MapLength()
        {
            var marker = Byte();
            return marker switch
            {
                >= 0x80 and <= 0x8f => marker & 15,
                0xde => Length(2),
                0xdf => Length(4),
                _ => throw new FormatException("Blackhole list and entries must be maps."),
            };
        }

        public int ArrayLength()
        {
            var marker = Byte();
            return marker switch
            {
                >= 0x90 and <= 0x9f => marker & 15,
                0xdc => Length(2),
                0xdd => Length(4),
                _ => throw new FormatException("Blackhole response envelope must be an array."),
            };
        }

        public bool Null()
        {
            if (_remaining.IsEmpty || _remaining[0] != 0xc0) return false;
            _remaining = _remaining[1..];
            return true;
        }

        public byte[] BinaryHash()
        {
            var length = Byte() switch
            {
                0xc4 => Length(1),
                0xc5 => Length(2),
                0xc6 => Length(4),
                _ => throw new FormatException("Blackhole identity keys and sources must be binary."),
            };
            if (length != Identity.HashSize) throw new FormatException("Blackhole identity hash must be 16 bytes.");
            return Take(length).ToArray();
        }

        public string String()
        {
            var marker = Byte();
            var length = marker switch
            {
                >= 0xa0 and <= 0xbf => marker & 31,
                0xd9 => Length(1),
                0xda => Length(2),
                0xdb => Length(4),
                _ => throw new FormatException("Blackhole field/reason must be a string."),
            };
            if (length > MaximumReasonBytes) throw new InvalidDataException("Blackhole reason exceeds the byte limit.");
            try { return new UTF8Encoding(false, true).GetString(Take(length)); }
            catch (DecoderFallbackException ex) { throw new FormatException("Invalid blackhole UTF-8.", ex); }
        }

        public object? NumberOrNull()
        {
            if (Null()) return null;
            if (_remaining.IsEmpty) throw new FormatException("Missing blackhole expiry.");
            var marker = _remaining[0];
            var length = marker switch
            {
                <= 0x7f or >= 0xe0 => 1,
                0xcc or 0xd0 => 2,
                0xcd or 0xd1 => 3,
                0xce or 0xd2 or 0xca => 5,
                0xcf or 0xd3 or 0xcb => 9,
                _ => throw new FormatException("Blackhole expiry must be a finite number or nil."),
            };
            var result = MsgPack.Unpack(Take(length));
            if (!double.IsFinite(MsgPack.ToDouble(result))) throw new FormatException("Blackhole expiry must be finite.");
            return result;
        }
    }
}
