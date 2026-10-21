using System.Buffers.Binary;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace DotReticulum.Applications.Runtime;

/// <summary>Bounded multiprocessing.connection byte framing and mutual authentication. Never unpickles data.</summary>
public static class ManagementConnection
{
    public const int MaximumMessageBytes = 4 * 1024 * 1024;
    private static readonly byte[] Challenge = "#CHALLENGE#"u8.ToArray();
    private static readonly byte[] Welcome = "#WELCOME#"u8.ToArray();
    private static readonly byte[] Failure = "#FAILURE#"u8.ToArray();

    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken token,
        int maximumBytes = MaximumMessageBytes)
    {
        if (data.Length > maximumBytes) throw new InvalidDataException("Management message exceeds the configured bound.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]> ReadAsync(Stream stream, CancellationToken token,
        int maximumBytes = MaximumMessageBytes)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header.AsMemory(0, 4), token).ConfigureAwait(false);
        long length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length == -1)
        {
            await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var extended = BinaryPrimitives.ReadUInt64BigEndian(header);
            if (extended > (ulong)maximumBytes) throw new InvalidDataException("Oversized management message.");
            length = (long)extended;
        }
        if (length < 0 || length > maximumBytes) throw new InvalidDataException("Invalid management message length.");
        var data = new byte[(int)length];
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return data;
    }

    public static async ValueTask AuthenticateAsync(Stream stream, ReadOnlyMemory<byte> key, bool server,
        CancellationToken token)
    {
        if (key.IsEmpty) throw new ArgumentException("An authentication key is required.", nameof(key));
        if (server)
        {
            await DeliverAsync(stream, key, token).ConfigureAwait(false);
            await AnswerAsync(stream, key, token).ConfigureAwait(false);
        }
        else
        {
            await AnswerAsync(stream, key, token).ConfigureAwait(false);
            await DeliverAsync(stream, key, token).ConfigureAwait(false);
        }
    }

    private static async ValueTask DeliverAsync(Stream stream, ReadOnlyMemory<byte> key, CancellationToken token)
    {
        var challenge = new byte[48];
        "{sha256}"u8.CopyTo(challenge);
        RandomNumberGenerator.Fill(challenge.AsSpan(8));
        byte[] payload = [.. Challenge, .. challenge];
        await WriteAsync(stream, payload, token, 256).ConfigureAwait(false);
        var response = await ReadAsync(stream, token, 256).ConfigureAwait(false);
        byte[]? expected = null;
        try
        {
            // Python 3.11 and earlier answer any challenge with a raw MD5 MAC.
            expected = response.Length == 16
                ? HMACMD5.HashData(key.Span, challenge)
                : CreateResponse(key.Span, challenge, response);
            var valid = CryptographicOperations.FixedTimeEquals(expected, response);
            await WriteAsync(stream, valid ? Welcome : Failure, token, 256).ConfigureAwait(false);
            if (!valid) throw new AuthenticationException("Management authentication failed.");
        }
        finally
        {
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(response);
        }
    }

    private static async ValueTask AnswerAsync(Stream stream, ReadOnlyMemory<byte> key, CancellationToken token)
    {
        var challenge = await ReadAsync(stream, token, 256).ConfigureAwait(false);
        if (!challenge.AsSpan().StartsWith(Challenge) || challenge.Length < Challenge.Length + 20)
            throw new AuthenticationException("Invalid management authentication challenge.");
        var message = challenge.AsMemory(Challenge.Length);
        var response = CreateResponse(key.Span, message.Span, message.Span);
        try { await WriteAsync(stream, response, token, 256).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(response); }
        var result = await ReadAsync(stream, token, 256).ConfigureAwait(false);
        if (!result.AsSpan().SequenceEqual(Welcome)) throw new AuthenticationException("Management authentication rejected.");
    }

    private static byte[] CreateResponse(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message, ReadOnlySpan<byte> selection)
    {
        if (selection.IsEmpty || selection[0] != (byte)'{') return HMACMD5.HashData(key, message);
        var end = selection.IndexOf((byte)'}');
        if (end is < 1 or > 16) throw new AuthenticationException("Invalid authentication digest prefix.");
        var name = Encoding.ASCII.GetString(selection[1..end]);
        var mac = name switch
        {
            "sha256" => HMACSHA256.HashData(key, message),
            "sha384" => HMACSHA384.HashData(key, message),
            "sha3_256" when HMACSHA3_256.IsSupported => HMACSHA3_256.HashData(key, message),
            "sha3_384" when HMACSHA3_384.IsSupported => HMACSHA3_384.HashData(key, message),
            "md5" => HMACMD5.HashData(key, message),
            _ => throw new AuthenticationException("Unsupported authentication digest: " + name),
        };
        try { return [.. selection[..(end + 1)], .. mac]; }
        finally { CryptographicOperations.ZeroMemory(mac); }
    }
}
