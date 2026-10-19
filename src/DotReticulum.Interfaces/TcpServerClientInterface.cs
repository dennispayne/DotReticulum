using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>One accepted connection of a per-client <see cref="TcpServerPacketInterface"/>, handed out already connected.</summary>
internal sealed class TcpServerClientInterface(HdlcStreamEndpoint endpoint, Func<Task> onClosed) : IPacketInterface
{
    private int _disposed;

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) => endpoint.ReceiveAsync(cancellationToken);

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => endpoint.SendAsync(packet, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await onClosed().ConfigureAwait(false);
    }
}
