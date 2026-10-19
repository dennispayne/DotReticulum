using ICSharpCode.SharpZipLib.BZip2;

namespace DotReticulum.Transport;

/// <summary>bz2 as used by upstream Resource compression (bz2.compress / bz2.decompress). Backed by SharpZipLib.</summary>
public static class Bz2
{
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var bz = new BZip2OutputStream(output, 9) { IsStreamOwner = false })
        {
            bz.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>Decompresses, throwing InvalidDataException if the result would exceed <paramref name="maxSize"/>.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> data, long maxSize)
    {
        using var input = new MemoryStream(data.ToArray());
        using var output = new MemoryStream();
        try
        {
            using var bz = new BZip2InputStream(input);
            var buf = new byte[64 * 1024];
            int n;
            while ((n = bz.Read(buf, 0, buf.Length)) > 0)
            {
                output.Write(buf, 0, n);
                if (output.Length > maxSize)
                {
                    throw new InvalidDataException("Decompressed data exceeds the maximum size.");
                }
            }
        }
        catch (Exception ex) when (ex is BZip2Exception or EndOfStreamException or IndexOutOfRangeException or ArgumentException)
        {
            throw new InvalidDataException("Invalid bz2 data.", ex);
        }

        return output.ToArray();
    }
}
