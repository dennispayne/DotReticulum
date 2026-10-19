using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

/// <summary>
/// Binary RSG and rngcs SSHSIG contracts from RNS 1.5.7, commit
/// a29bfb51c468945c96a1ee22cae0114e993e2391, rnid.py and rngit/commitsigs.py.
/// Extraction is not authentication; validation proves key possession, not publisher trust.
/// </summary>
public static class RngitSignatures
{
    public const int MaximumSignatureBytes = 1024 * 1024;
    public const int MaximumMessageBytes = 16 * 1024 * 1024;
    public const int MaximumArmoredCharacters = MaximumSignatureBytes * 2;

    public sealed record SshSignature(byte[] PublicKey, byte[] Namespace, byte[] Reserved,
        byte[] HashAlgorithm, byte[] SignatureData);

    public sealed record RsgValidation(bool IsValid, Dictionary<object, object?>? SignedData,
        byte[]? SignerHash, byte[]? PublicKey);

    public sealed record RsgExtension(sbyte Type, byte[] Data);

    public static byte[] CreateRsg(Identity signer, ReadOnlySpan<byte> message, bool embed = false,
        IReadOnlyDictionary<object, object?>? metadata = null)
    {
        if (message.Length > MaximumMessageBytes) throw new ArgumentException("Message exceeds the 16 MiB limit.");
        return CreateRsgCore(signer, SHA256.HashData(message), embed ? message.ToArray() : null, metadata);
    }

    /// <summary>Signs a precomputed SHA-256 digest, allowing callers to hash bounded streams independently.</summary>
    public static byte[] CreateRsgFromHash(Identity signer, ReadOnlySpan<byte> sha256,
        IReadOnlyDictionary<object, object?>? metadata = null) => CreateRsgCore(signer, sha256, null, metadata);

    private static byte[] CreateRsgCore(Identity signer, ReadOnlySpan<byte> hash, byte[]? message,
        IReadOnlyDictionary<object, object?>? metadata)
    {
        if (hash.Length != 32) throw new ArgumentException("Expected a SHA-256 digest.", nameof(hash));
        var meta = new Dictionary<object, object?>
        {
            ["signer"] = signer.Hash.ToArray(),
            ["pubkey"] = signer.ExportPublicKey()
        };
        if (metadata is not null)
            foreach (var entry in metadata)
                meta.TryAdd(entry.Key, entry.Value);
        var data = new Dictionary<object, object?>
        {
            ["hashtype"] = "sha256",
            ["hash"] = hash.ToArray(),
            ["meta"] = meta
        };
        if (message is not null) data["message"] = message;
        var envelope = MsgPack.Pack(data);
        if (envelope.Length > MaximumSignatureBytes - 64)
            throw new ArgumentException("RSG exceeds the 1 MiB limit.");
        return [.. signer.Sign(envelope), .. envelope];
    }

    /// <summary>Extracts an untrusted MessagePack envelope without checking its signature or schema.</summary>
    public static object? ExtractSignedRsgData(ReadOnlySpan<byte> rsg)
    {
        if (rsg.Length is <= 64 or > MaximumSignatureBytes) return null;
        try
        {
            var reader = new EnvelopeReader(rsg[64..]);
            return reader.Read();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public static Dictionary<object, object?>? ExtractRsg(ReadOnlySpan<byte> rsg) =>
        ExtractSignedRsgData(rsg) as Dictionary<object, object?>;

    public static RsgValidation ValidateRsg(ReadOnlySpan<byte> rsg, ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> requiredSigner = default)
    {
        if (message.IsEmpty || message.Length > MaximumMessageBytes) return new(false, null, null, null);
        return ValidateRsgHash(rsg, SHA256.HashData(message), requiredSigner);
    }

    /// <summary>
    /// Verifies the signature and message digest. Optional requiredSigner is an independently trusted
    /// identity hash, not the envelope's untrusted meta.signer (which upstream does not compare).
    /// An embedded message, if present, is metadata; callers must supply the digest of the actual artifact.
    /// </summary>
    public static RsgValidation ValidateRsgHash(ReadOnlySpan<byte> rsg, ReadOnlySpan<byte> sha256,
        ReadOnlySpan<byte> requiredSigner = default)
    {
        if (sha256.Length != 32 || (!requiredSigner.IsEmpty && requiredSigner.Length != Identity.HashSize))
            return new(false, null, null, null);
        var data = ExtractRsg(rsg);
        if (data?.GetValueOrDefault("hashtype") is not string hashtype || hashtype != "sha256"
            || data.GetValueOrDefault("hash") is not byte[] hash
            || data.GetValueOrDefault("meta") is not Dictionary<object, object?> meta
            || !meta.ContainsKey("signer") || meta.GetValueOrDefault("pubkey") is not byte[] pubkey)
            return new(false, null, null, null);
        try
        {
            using var identity = Identity.FromPublicKey(pubkey);
            if ((!requiredSigner.IsEmpty && !CryptographicOperations.FixedTimeEquals(identity.Hash, requiredSigner))
                || !CryptographicOperations.FixedTimeEquals(hash, sha256))
                return new(false, null, identity.Hash.ToArray(), pubkey.ToArray());
            return new(identity.Verify(rsg[..64], rsg[64..]), data, identity.Hash.ToArray(), pubkey.ToArray());
        }
        catch (ArgumentException)
        {
            return new(false, null, null, null);
        }
    }

    public static byte[] CreateSshSignature(Identity signer, ReadOnlySpan<byte> rsg)
    {
        using var publicKey = new MemoryStream();
        WriteString(publicKey, "ssh-ed25519"u8);
        WriteString(publicKey, signer.ExportPublicKey().AsSpan(32));
        return EncodeSshSignature(new(publicKey.ToArray(), "git"u8.ToArray(), [], "sha256"u8.ToArray(), rsg.ToArray()));
    }

    public static byte[] EncodeSshSignature(SshSignature signature)
    {
        var total = 30L + signature.PublicKey.Length + signature.Namespace.Length + signature.Reserved.Length
            + signature.HashAlgorithm.Length + signature.SignatureData.Length;
        if (total > MaximumSignatureBytes) throw new ArgumentException("SSHSIG exceeds the 1 MiB limit.");
        using var stream = new MemoryStream((int)total);
        stream.Write("SSHSIG"u8);
        stream.Write([0, 0, 0, 1]);
        WriteString(stream, signature.PublicKey);
        WriteString(stream, signature.Namespace);
        WriteString(stream, signature.Reserved);
        WriteString(stream, signature.HashAlgorithm);
        WriteString(stream, signature.SignatureData);
        return stream.ToArray();
    }

    /// <summary>
    /// Parses bounded SSHSIG v1. Like rngcs, trailing bytes are ignored; outer public key, reserved
    /// and hash algorithm are transport fields, not authenticated RSG authority.
    /// </summary>
    public static SshSignature ParseSshSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumSignatureBytes) throw new FormatException("SSHSIG exceeds the 1 MiB limit.");
        if (!bytes.StartsWith("SSHSIG"u8)) throw new FormatException("Invalid SSH signature: missing SSHSIG magic");
        if (bytes.Length < 10) throw new FormatException("Invalid SSH signature: truncated");
        var version = BinaryPrimitives.ReadUInt32BigEndian(bytes[6..]);
        if (version != 1) throw new FormatException($"Unsupported SSH signature version: {version}");
        var offset = 10;
        return new(ReadString(bytes, ref offset), ReadString(bytes, ref offset), ReadString(bytes, ref offset),
            ReadString(bytes, ref offset), ReadString(bytes, ref offset));
    }

    public static string ArmorSshSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumSignatureBytes) throw new ArgumentException("SSHSIG exceeds the 1 MiB limit.");
        var encoded = Convert.ToBase64String(bytes);
        var result = new StringBuilder("-----BEGIN SSH SIGNATURE-----\n");
        for (var offset = 0; offset < encoded.Length; offset += 70)
            result.Append(encoded.AsSpan(offset, Math.Min(70, encoded.Length - offset))).Append('\n');
        return result.Append("-----END SSH SIGNATURE-----\n").ToString();
    }

    public static byte[] UnarmorSshSignature(string armor)
    {
        if (armor.Length > MaximumArmoredCharacters) throw new FormatException("Armored signature exceeds the 2 MiB limit.");
        var content = new StringBuilder();
        var inside = false;
        foreach (var line in armor.Trim().Split('\n'))
        {
            if (line.Contains("BEGIN SSH SIGNATURE", StringComparison.Ordinal)) { inside = true; continue; }
            if (line.Contains("END SSH SIGNATURE", StringComparison.Ordinal)) break;
            if (inside) content.Append(line.Trim());
        }
        if (content.Length == 0) throw new FormatException("No signature data found in armored input");
        var bytes = DecodeBase64(content.ToString());
        if (bytes.Length > MaximumSignatureBytes) throw new FormatException("SSHSIG exceeds the 1 MiB limit.");
        return bytes;
    }

    private static byte[] DecodeBase64(string content)
    {
        // Python b64decode(validate=False) discards non-alphabet ASCII, accepts
        // extra padding, and stops at the first complete padded quantum.
        if (content.Any(c => c > 127)) throw new FormatException("string argument should contain only ASCII characters");
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        using var result = new MemoryStream();
        var position = 0;
        var padding = 0;
        var bits = 0;
        var characters = 0;
        foreach (var c in content)
        {
            if (c == '=')
            {
                padding++;
                if (position >= 2 && position + padding >= 4) return result.ToArray();
                continue;
            }
            var value = alphabet.IndexOf(c);
            if (value < 0) continue;
            padding = 0;
            characters++;
            bits = (bits << 6) | value;
            position++;
            if (position >= 2) result.WriteByte((byte)(bits >> (2 * (4 - position))));
            if (position == 4) { position = 0; bits = 0; }
        }
        if (position == 1)
            throw new FormatException($"Invalid base64-encoded string: number of data characters ({characters}) cannot be 1 more than a multiple of 4");
        if (position != 0) throw new FormatException("Incorrect padding");
        return result.ToArray();
    }

    private static void WriteString(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)value.Length);
        stream.Write(size);
        stream.Write(value);
    }

    private static byte[] ReadString(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (bytes.Length - offset < 4) throw new FormatException("Not enough data for string length");
        var size = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
        offset += 4;
        if (size > bytes.Length - offset) throw new FormatException("Not enough data for string content");
        var value = bytes.Slice(offset, (int)size).ToArray();
        offset += (int)size;
        return value;
    }

    // umsgpack unpacks one value (ignoring trailing bytes), rejects duplicate keys
    // and rejects invalid UTF-8. Core.MsgPack intentionally has a different contract.
    // Keep these RSG-specific parsing rules local and always verify the full envelope.
    private ref struct EnvelopeReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _remaining = data;

        public object? Read(int depth = 0)
        {
            if (depth > 32) throw new FormatException("RSG nesting exceeds the 32-level limit.");
            var code = Take(1)[0];
            if (code <= 0x7f) return (long)code;
            if (code >= 0xe0) return (long)(sbyte)code;
            if (code is >= 0xa0 and <= 0xbf) return Text((uint)(code & 31));
            if (code is >= 0x90 and <= 0x9f) return Array((uint)(code & 15), depth);
            if (code is >= 0x80 and <= 0x8f) return Map((uint)(code & 15), depth);
            return code switch
            {
                0xc0 => null,
                0xc2 => false,
                0xc3 => true,
                0xc4 => Take(Length(1)).ToArray(),
                0xc5 => Take(Length(2)).ToArray(),
                0xc6 => Take(Length(4)).ToArray(),
                0xd9 => Text(Length(1)),
                0xda => Text(Length(2)),
                0xdb => Text(Length(4)),
                0xdc => Array(Length(2), depth),
                0xdd => Array(Length(4), depth),
                0xde => Map(Length(2), depth),
                0xdf => Map(Length(4), depth),
                0xc7 => Extension(Length(1)),
                0xc8 => Extension(Length(2)),
                0xc9 => Extension(Length(4)),
                0xd4 => Extension(1),
                0xd5 => Extension(2),
                0xd6 => Extension(4),
                0xd7 => Extension(8),
                0xd8 => Extension(16),
                0xcc or 0xd0 => Scalar(code, 1),
                0xcd or 0xd1 => Scalar(code, 2),
                0xce or 0xd2 or 0xca => Scalar(code, 4),
                0xcf or 0xd3 or 0xcb => Scalar(code, 8),
                _ => throw new FormatException("Invalid RSG MessagePack code.")
            };
        }

        private object? Scalar(byte code, uint size) => MsgPack.Unpack([code, .. Take(size)]);

        private string Text(uint length) => new UTF8Encoding(false, true).GetString(Take(length));

        private RsgExtension Extension(uint length)
        {
            var type = (sbyte)Take(1)[0];
            return new(type, Take(length).ToArray());
        }

        private object?[] Array(uint count, int depth)
        {
            if (count > _remaining.Length) throw new FormatException("Truncated RSG array.");
            var items = new object?[count];
            for (var index = 0; index < items.Length; index++) items[index] = Read(depth + 1);
            return items;
        }

        private Dictionary<object, object?> Map(uint count, int depth)
        {
            if (count > _remaining.Length / 2) throw new FormatException("Truncated RSG map.");
            var map = new Dictionary<object, object?>((int)count, EnvelopeKeyComparer.Instance);
            for (var index = 0; index < count; index++)
            {
                var key = Read(depth + 1) ?? throw new FormatException("Null RSG map key.");
                if (key is Dictionary<object, object?>) throw new FormatException("Unhashable RSG map key.");
                if (!map.TryAdd(key, Read(depth + 1))) throw new FormatException("Duplicate RSG map key.");
            }
            return map;
        }

        private uint Length(uint size) => size switch
        {
            1 => Take(1)[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
            4 => BinaryPrimitives.ReadUInt32BigEndian(Take(4)),
            _ => throw new InvalidOperationException()
        };

        private ReadOnlySpan<byte> Take(uint length)
        {
            if (length > _remaining.Length) throw new FormatException("Truncated RSG MessagePack.");
            var result = _remaining[..(int)length];
            _remaining = _remaining[(int)length..];
            return result;
        }
    }

    private sealed class EnvelopeKeyComparer : IEqualityComparer<object>
    {
        public static EnvelopeKeyComparer Instance { get; } = new();
        public new bool Equals(object? x, object? y)
        {
            if (x is byte[] a && y is byte[] b) return a.AsSpan().SequenceEqual(b);
            if (x is object?[] ax && y is object?[] ay)
                return ax.Length == ay.Length && ax.Zip(ay).All(pair => Equals(pair.First, pair.Second));
            if (x is bool or long or ulong or double && y is bool or long or ulong or double)
            {
                if (x is double dx && y is double dy) return dx == dy;
                if (x is double xd && (!double.IsFinite(xd) || Math.Truncate(xd) != xd)) return false;
                if (y is double yd && (!double.IsFinite(yd) || Math.Truncate(yd) != yd)) return false;
                return Integer(x) == Integer(y);
            }
            return object.Equals(x, y);
        }

        public int GetHashCode(object obj)
        {
            if (obj is byte[] bytes)
            {
                var hash = new HashCode();
                foreach (var value in bytes) hash.Add(value);
                return hash.ToHashCode();
            }
            if (obj is object?[] values)
            {
                var hash = new HashCode();
                foreach (var value in values) hash.Add(value is null ? 0 : GetHashCode(value));
                return hash.ToHashCode();
            }
            if (obj is bool or long or ulong or double)
                return Convert.ToDouble(obj, System.Globalization.CultureInfo.InvariantCulture).GetHashCode();
            return obj.GetHashCode();
        }

        private static BigInteger Integer(object value) => value switch
        {
            bool b => b ? BigInteger.One : BigInteger.Zero,
            long l => new(l),
            ulong ul => new(ul),
            double d => new(d),
            _ => throw new InvalidOperationException()
        };
    }
}
