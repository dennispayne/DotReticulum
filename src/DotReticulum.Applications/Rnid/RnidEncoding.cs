using System.Text;

namespace DotReticulum.Applications.Rnid;

/// <summary>RNS 1.5.7 rnid text encodings and 64-codepoint RSG wrapping.</summary>
public static class RnidEncoding
{
    private const string Alphabet32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const string Alphabet256 =
        "abcdefghijklmnopqrstuvxyzæø01234ABCDEFGHIJKLMNOPQRSTUWXYZÆØ56789"
        + "αβγδεζηθικλμνξπρστφχψωΓΔΘΛΞΠΣΦΨΩБДЖЗИЛПЦЧШЩЪЫЭЮЯбджзилпцчшщъыэюя"
        + "ԱԲԳԴԵԶԷԸԹԺԻԽԾԿՀՁՂՃՄՅՆՇՈՉՊՋՎՐՑՒՔՖᚠᚢᚦᚱᚹᚺᚾᛈᛇᛉᛊᛏᛒᛖᛗᛟ"
        + "ｲｳｵｶｷｹｻｼｽｾﾀﾁﾃﾄﾅﾇﾈﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾗﾘﾙﾚﾜ"
        + "𐑐𐑑𐑒𐑔𐑕𐑗𐑙𐑳𐑶𐑸𐑹𐑺𐑻𐑽𐑾𐑿᱑᱕᱘᱙ᱚᱝᱟᱣᱦᱨᱬᱭᱰᱳᱶᱷ𐌳𐌸𐌾𐐀𐐁𐐂𐐆𐐇𐐈𐐉𐐊𐐋𐐌𐐍𐐎𐐏";
    private static readonly string[] Points = Alphabet256.EnumerateRunes().Select(r => r.ToString()).ToArray();

    public static string Encode(ReadOnlySpan<byte> data, string format)
    {
        if (format == "base64") return Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_');
        if (format == "hex") return Convert.ToHexStringLower(data);
        var result = new StringBuilder();
        if (format == "base256")
        {
            foreach (var b in data) result.Append(Points[b]);
            return result.ToString();
        }
        if (format != "base32") throw new ArgumentException("Unknown encoding.", nameof(format));
        var bits = 0;
        var count = 0;
        foreach (var b in data)
        {
            bits = (bits << 8) | b;
            count += 8;
            while (count >= 5) { count -= 5; result.Append(Alphabet32[(bits >> count) & 31]); }
        }
        if (count > 0) result.Append(Alphabet32[(bits << (5 - count)) & 31]);
        while (result.Length % 8 != 0) result.Append('=');
        return result.ToString();
    }

    public static byte[] Decode(string text, string format)
    {
        if (text.Length > 4 * 1024 * 1024) throw new FormatException("Encoded input exceeds the 4 MiB limit.");
        if (format == "hex") return Convert.FromHexString(text);
        if (format == "base64") return Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/'));
        using var result = new MemoryStream();
        if (format == "base256")
        {
            foreach (var point in text.EnumerateRunes())
            {
                var index = Array.IndexOf(Points, point.ToString());
                if (index < 0) throw new FormatException("Invalid base256 character.");
                result.WriteByte((byte)index);
            }
            return result.ToArray();
        }
        if (format != "base32") throw new ArgumentException("Unknown encoding.", nameof(format));
        if (text.Length % 8 != 0) throw new FormatException("Incorrect base32 padding.");
        var bits = 0;
        var count = 0;
        var padding = false;
        var symbols = 0;
        foreach (var point in text)
        {
            if (point == '=') { padding = true; continue; }
            var index = Alphabet32.IndexOf(point);
            if (index < 0 || padding) throw new FormatException("Invalid base32 input.");
            symbols++;
            bits = (bits << 5) | index;
            count += 5;
            if (count >= 8) { count -= 8; result.WriteByte((byte)(bits >> count)); }
        }
        if (symbols % 8 is 1 or 3 or 6) throw new FormatException("Incorrect base32 padding.");
        return result.ToArray();
    }

    public static string Wrap(string text)
    {
        var result = new StringBuilder("#### Start of rsg data ".PadRight(64, '#') + "\n");
        var points = text.EnumerateRunes().ToArray();
        for (var offset = 0; offset < points.Length; offset += 64)
        {
            var length = Math.Min(64, points.Length - offset);
            for (var i = 0; i < length; i++) result.Append(points[offset + i].ToString());
            result.Append('=', 64 - length).Append('\n');
        }
        return result.Append(" End of rsg data ####".PadLeft(64, '#')).ToString();
    }

    public static byte[] DecodeSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > Rngit.RngitSignatures.MaximumSignatureBytes * 4)
            throw new FormatException("Signature exceeds the 4 MiB input limit.");
        if (bytes.Length == 64 || Rngit.RngitSignatures.ExtractRsg(bytes) is not null) return bytes.ToArray();
        var text = new UTF8Encoding(false, true).GetString(bytes);
        text = string.Concat(text.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#')));
        var unpadded = text.TrimEnd('=');
        foreach (var format in new[] { "hex", "base32", "base64", "base256" })
        {
            try
            {
                var candidate = format switch
                {
                    "base32" => unpadded.PadRight((unpadded.Length + 7) / 8 * 8, '='),
                    "base64" => unpadded.PadRight((unpadded.Length + 3) / 4 * 4, '='),
                    _ => unpadded
                };
                var decoded = Decode(candidate, format);
                if (decoded.Length == 64 || Rngit.RngitSignatures.ExtractRsg(decoded) is not null) return decoded;
            }
            catch (FormatException) { }
        }
        throw new FormatException("Invalid binary or encoded RSG data.");
    }
}
