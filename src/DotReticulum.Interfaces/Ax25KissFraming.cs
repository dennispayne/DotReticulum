namespace DotReticulum.Interfaces;

/// <summary>The fixed 16-byte upstream AX.25 header, without FCS or repeater addresses.</summary>
public static class Ax25KissFraming
{
    public const int HeaderSize = 16;

    public static void ValidateAddress(string callsign, int ssid)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        if (callsign.Length is < 3 or > 6 || callsign.Any(c => c > 127))
            throw new ArgumentException("Callsign must contain 3 to 6 ASCII characters.", nameof(callsign));
        if (ssid is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(ssid));
    }

    public static byte[] AddHeader(ReadOnlySpan<byte> payload, string callsign, int ssid)
    {
        ValidateAddress(callsign, ssid);
        if (payload.Length > 564) throw new ArgumentOutOfRangeException(nameof(payload));
        var result = new byte[HeaderSize + payload.Length];
        const string destination = "APZRNS";
        callsign = callsign.ToUpperInvariant();
        for (var i = 0; i < 6; i++)
        {
            result[i] = (byte)(destination[i] << 1);
            result[7 + i] = i < callsign.Length ? (byte)(callsign[i] << 1) : (byte)0x20;
        }
        result[6] = 0x60;
        result[13] = (byte)(0x61 | (ssid << 1));
        result[14] = 0x03;
        result[15] = 0xf0;
        payload.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }
}
