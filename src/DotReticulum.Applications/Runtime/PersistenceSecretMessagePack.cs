using System.Buffers.Binary;
using System.Security.Cryptography;
using DotReticulum.Core;

namespace DotReticulum.Applications.Runtime;

/// <summary>Secret serialization uses one exactly sized output buffer, avoiding unowned growable-stream
/// copies of private keys. Public scalar encodings still use the established MessagePack codec.</summary>
internal static class PersistenceSecretMessagePack
{
    public static byte[] Pack(object? value)
    {
        var length = Size(value);
        if (length > RuntimeStateStore.MaximumRatchetBytes) throw new InvalidDataException("Secret persistence exceeds its byte limit.");
        var buffer = new byte[length];
        try
        {
            var position = 0;
            Write(value, buffer, ref position);
            return buffer;
        }
        catch { CryptographicOperations.ZeroMemory(buffer); throw; }
    }

    private static int HeaderSize(int count, bool binary) => binary
        ? count < 256 ? 2 : count < 65536 ? 3 : 5
        : count < 16 ? 1 : count < 65536 ? 3 : 5;

    private static int Size(object? value) => value switch
    {
        byte[] bytes => checked(HeaderSize(bytes.Length, true) + bytes.Length),
        object?[] array => checked(HeaderSize(array.Length, false) + array.Sum(Size)),
        Dictionary<object, object?> map => checked(HeaderSize(map.Count, false) + map.Sum(e => checked(Size(e.Key) + Size(e.Value)))),
        null or bool or int or long or string => MsgPack.Pack(value).Length,
        _ => throw new ArgumentException("Unsupported secret persistence value."),
    };

    private static void Header(Span<byte> buffer, ref int position, int count, byte compact, byte medium, byte large)
    {
        if (compact != 0 && count < 16) { buffer[position++] = (byte)(compact | count); return; }
        if (compact == 0 && count < 256)
        {
            buffer[position++] = 0xc4;
            buffer[position++] = (byte)count;
        }
        else if (count < 65536)
        {
            buffer[position++] = medium;
            BinaryPrimitives.WriteUInt16BigEndian(buffer[position..], (ushort)count);
            position += 2;
        }
        else
        {
            buffer[position++] = large;
            BinaryPrimitives.WriteUInt32BigEndian(buffer[position..], (uint)count);
            position += 4;
        }
    }

    private static void Write(object? value, Span<byte> buffer, ref int position)
    {
        switch (value)
        {
            case byte[] bytes:
                Header(buffer, ref position, bytes.Length, 0, 0xc5, 0xc6);
                bytes.CopyTo(buffer[position..]);
                position += bytes.Length;
                break;
            case object?[] array:
                Header(buffer, ref position, array.Length, 0x90, 0xdc, 0xdd);
                foreach (var item in array) Write(item, buffer, ref position);
                break;
            case Dictionary<object, object?> map:
                Header(buffer, ref position, map.Count, 0x80, 0xde, 0xdf);
                foreach (var (key, item) in map) { Write(key, buffer, ref position); Write(item, buffer, ref position); }
                break;
            default:
                var scalar = MsgPack.Pack(value);
                try { scalar.CopyTo(buffer[position..]); position += scalar.Length; }
                finally { CryptographicOperations.ZeroMemory(scalar); }
                break;
        }
    }
}
