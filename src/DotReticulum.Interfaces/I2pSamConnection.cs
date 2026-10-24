using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DotReticulum.Interfaces;

internal sealed class I2pSamException(string kind, string action, string result)
    : IOException($"SAM {kind} {action} failed ({result}).")
{
    internal string Result { get; } = result;
}

/// <summary>A single owned SAM socket. Byte-exact line reads leave coalesced stream bytes untouched.</summary>
internal sealed class I2pSamConnection : IAsyncDisposable
{
    internal const int MaximumLineLength = 16384;
    private readonly TcpClient _client;
    public NetworkStream Stream { get; }

    private I2pSamConnection(TcpClient client)
    {
        _client = client;
        Stream = client.GetStream();
    }

    internal static void ValidateToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumLineLength - 256 || value.Any(c => c is <= ' ' or > '~'))
            throw new ArgumentException("Invalid SAM protocol token.");
    }

    internal static async Task<I2pSamConnection> OpenAsync(I2pPacketInterfaceOptions options, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(options.SamHost!, options.SamPort, token).ConfigureAwait(false);
            var connection = new I2pSamConnection(client);
            var reply = await connection.CommandAsync("HELLO VERSION MIN=3.1 MAX=3.1\n", "HELLO", "REPLY", token).ConfigureAwait(false);
            if (!reply.TryGetValue("VERSION", out var version) || version != "3.1")
                throw new InvalidDataException("SAM bridge did not negotiate version 3.1.");
            return connection;
        }
        catch { client.Dispose(); throw; }
    }

    internal async Task<Dictionary<string, string>> CommandAsync(string command, string kind, string action, CancellationToken token, bool requireResult = true)
    {
        var encoded = Encoding.ASCII.GetBytes(command);
        try { await Stream.WriteAsync(encoded, token).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
        var line = await ReadLineAsync(token).ConfigureAwait(false);
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != kind || parts[1] != action)
            throw new InvalidDataException("Unexpected SAM reply type.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < parts.Length; index++)
        {
            var part = parts[index];
            var separator = part.IndexOf('=');
            if (separator > 0) values.TryAdd(part[..separator], part[(separator + 1)..]);
        }
        if (!values.TryGetValue("RESULT", out var result) && requireResult)
            throw new InvalidDataException("SAM reply is missing RESULT.");
        if (result is not null && result != "OK")
        {
            // Never include reply text: SESSION replies can contain private keys.
            var safeResult = result.Length <= 64 && result.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? result : "INVALID_RESULT";
            throw new I2pSamException(kind, action, safeResult);
        }
        return values;
    }

    internal async Task<string> ReadLineAsync(CancellationToken token)
    {
        var line = new byte[MaximumLineLength];
        var one = new byte[1];
        try
        {
            for (var count = 0; count < line.Length; count++)
            {
                if (await Stream.ReadAsync(one, token).ConfigureAwait(false) == 0)
                    throw new EndOfStreamException("SAM bridge closed before completing a response.");
                if (one[0] == '\n')
                    return Encoding.ASCII.GetString(line, 0, count > 0 && line[count - 1] == '\r' ? count - 1 : count);
                if ((one[0] < 32 || one[0] > 126) && one[0] != '\r')
                    throw new InvalidDataException("Non-ASCII SAM response.");
                line[count] = one[0];
            }
            throw new InvalidDataException("SAM response exceeds the line bound.");
        }
        finally { CryptographicOperations.ZeroMemory(line); CryptographicOperations.ZeroMemory(one); }
    }

    internal async Task WatchSessionAsync(CancellationToken token)
    {
        var buffer = new byte[1];
        while (await Stream.ReadAsync(buffer, token).ConfigureAwait(false) != 0) { }
        throw new EndOfStreamException("SAM session control socket closed.");
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
