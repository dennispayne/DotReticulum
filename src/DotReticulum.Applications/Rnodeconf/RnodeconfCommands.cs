using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Interfaces;

namespace DotReticulum.Applications.Rnodeconf;

/// <summary>Maintenance serialization from pinned RNS Utilities/rnodeconf.py, not packet-interface startup.</summary>
public static class RnodeconfCommands
{
    public static byte[] Detect() =>
        [.. RNodeProtocol.Detect(), 0x47, 0, 0xc0, 0x56, 1, 0xc0, 0x60, 1, 0xc0, 0x60, 2, 0xc0];

    public static byte[] Encode(string operation, string? value = null)
    {
        // The reference deliberately does not escape these one-byte setting commands.
        byte[] Single(byte command, int number) => [0xc0, command, (byte)number, 0xc0];
        int Number(int min, int max) => Math.Clamp(int.Parse(value!, CultureInfo.InvariantCulture), min, max);
        byte[] Address(byte command)
        {
            if (value!.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return RNodeProtocol.Frame(command, new byte[4]);
            var octets = value.Split('.');
            if (octets.Length != 4 || octets.Any(o => !byte.TryParse(o, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                throw new ArgumentException("Invalid IPv4 address.");
            return RNodeProtocol.Frame(command, IPAddress.Parse(value).GetAddressBytes());
        }
        byte[] Text(byte command, bool psk)
        {
            if (value!.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return RNodeProtocol.Frame(command, [0]);
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            if (bytes.Length > 33 || (psk && bytes.Length < 8)) throw new ArgumentException("Invalid WiFi value length.");
            try { return RNodeProtocol.Frame(command, bytes); }
            finally { if (psk) CryptographicOperations.ZeroMemory(bytes); }
        }
        return operation switch
        {
            "display" => Single(0x45, Number(0, 255)),
            "timeout" => Single(0x64, Number(0, 255)),
            "rotation" => Single(0x67, Number(0, 3)),
            "np" => Single(0x65, Number(0, 255)),
            "display-addr" => Single(0x63, byte.Parse(value!.Replace("0x", "", StringComparison.Ordinal), NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
            "recondition-display" => Single(0x68, 1),
            "ia-enable" => Single(0x69, 0),
            "ia-disable" => Single(0x69, 1),
            "bluetooth-on" => Single(0x46, 1),
            "bluetooth-off" => Single(0x46, 0),
            "bluetooth-pair" => Single(0x46, 2),
            "wifi" => Single(0x6a, value!.ToUpperInvariant() switch { "OFF" => 0, "STATION" => 1, "AP" => 2, _ => throw new ArgumentException("WiFi mode must be OFF, AP or STATION.") }),
            "channel" => int.TryParse(value, out var channel) && channel is >= 1 and <= 14
                ? Single(0x6e, channel) : throw new ArgumentException("Invalid WiFi channel."),
            "ssid" => Text(0x6b, false),
            "psk" => Text(0x6c, true),
            "ip" => Address(0x84),
            "nm" => Address(0x85),
            "normal" => Single(0x54, 0),
            "eeprom-read" => Single(0x51, 0),
            "config-read" => Single(0x6d, 0),
            "reset" => Single(0x55, 0xf8),
            "eeprom-wipe" => Single(0x59, 0xf8),
            "update-indicator" => Single(0x61, 1),
            "leave" => Single(0x0a, 0xff),
            _ => throw new NotSupportedException($"Unsupported maintenance command: {operation}")
        };
    }

    public static IReadOnlyList<byte[]> Tnc(long frequency, long bandwidth, int power, int sf, int cr, byte platform)
    {
        if (frequency is < 137000000 or > 3000000000 || bandwidth is < 7800 or > 1625000 ||
            power is < 0 or > 17 || sf is < 7 or > 12 || cr is < 5 or > 8)
            throw new ArgumentException("Invalid TNC radio configuration.");
        byte[] UInt32(byte command, long value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
            return RNodeProtocol.Frame(command, bytes);
        }
        List<byte[]> result =
        [
            UInt32(1, frequency), UInt32(2, bandwidth),
            [0xc0, 3, (byte)power, 0xc0], [0xc0, 4, (byte)sf, 0xc0], [0xc0, 5, (byte)cr, 0xc0],
            [0xc0, 6, 1, 0xc0], [0xc0, 0x53, 0, 0xc0]
        ];
        if (platform == 0x80) result.Add(Encode("reset"));
        return result;
    }
}
