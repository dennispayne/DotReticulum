using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

internal static class RngitWire
{
    internal static byte[] Status(byte code, string text = "") => [code, .. Encoding.UTF8.GetBytes(text)];

    internal static object? Get(object? data, object key)
    {
        if (data is not Dictionary<object, object?> map) return null;
        if (map.TryGetValue(key, out var value)) return value;
        if (key is int integer && map.TryGetValue((long)integer, out value)) return value;
        return null;
    }

    internal static string? Text(object? data, object key) => Get(data, key) as string;
    internal static bool Flag(object? data, object key) => Get(data, key) is true;
    internal static bool ValidSha(string? sha) => sha is { Length: 40 or 64 } && sha.All(Uri.IsHexDigit);
    internal static byte[] Packed(object value) => [0, .. MsgPack.Pack(value)];
}
