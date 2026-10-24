using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rngit;

/// <summary>An identified management link; object transfer and Git ref ownership remain separate.</summary>
public sealed class RngitManagementClient : IDisposable
{
    private readonly Link link;
    private readonly double timeout;
    public long MaxResponseBytes { get; init; } = RngitClient.DefaultMaxResponseBytes;

    private RngitManagementClient(Link link, TimeSpan timeout)
    {
        this.link = link;
        this.timeout = timeout.TotalSeconds;
    }

    public static async Task<RngitManagementClient> ConnectAsync(ReticulumNode node, Identity identity,
        byte[] destination, TimeSpan timeout, CancellationToken stop = default)
    {
        var connection = await ClientLinks.ConnectAsync(node, destination, timeout, stop).ConfigureAwait(false);
        var link = connection.Link ?? throw new IOException(connection.Error);
        try
        {
            link.Identify(identity);
            return new RngitManagementClient(link, timeout);
        }
        catch { link.Teardown(); throw; }
    }

    public async Task<(byte[] Data, object? Metadata)> RequestAsync(string path, object data, CancellationToken stop = default)
    {
        var receipt = link.Request(path, data, timeout: timeout, maxResponseSize: MaxResponseBytes, cancellationToken: stop) ?? throw new IOException("Management link closed.");
        object? response;
        try { response = await receipt.ResponseTask.WaitAsync(stop).ConfigureAwait(false); }
        catch (TimeoutException ex) when (link.Status == LinkStatus.Closed)
        {
            throw new IOException("Management peer disconnected before the response completed; the remote outcome may be uncertain.", ex);
        }
        if (response is not byte[] bytes || bytes.LongLength > MaxResponseBytes)
            throw new InvalidDataException("Invalid or oversized management response.");
        return (bytes, receipt.Metadata);
    }

    internal static object? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) throw new InvalidDataException("Empty management response.");
        if (bytes[0] != 0) throw new IOException(System.Text.Encoding.UTF8.GetString(bytes.AsSpan(1)));
        if (bytes.Length == 1) return null;
        if (!MsgPack.TryUnpack(bytes.AsSpan(1), out var value)) throw new InvalidDataException("Malformed management response.");
        return value;
    }

    public void Dispose() => link.Teardown();
}
