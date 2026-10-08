using System.Threading.Channels;

namespace DotReticulum.Core;

/// <summary>Owns packet interfaces and forwards incoming packets through a bounded queue.</summary>
public sealed class PacketInterfaceManager : IAsyncDisposable
{
    private readonly IPacketInterface[] _interfaces;
    private readonly Channel<ReadOnlyMemory<byte>> _incoming;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task[] _receivers = [];
    private bool _started;
    private bool _disposed;

    public PacketInterfaceManager(IEnumerable<IPacketInterface> interfaces, int queueCapacity = 256)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        if (queueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));

        _interfaces = interfaces.ToArray();
        if (_interfaces.Any(packetInterface => packetInterface is null))
            throw new ArgumentException("Interfaces cannot contain null entries.", nameof(interfaces));

        _incoming = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    /// <summary>Starts all owned interfaces. A manager can only be started once.</summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                throw new InvalidOperationException("The interface manager has already been started.");

            _started = true;
            _lifetime = new CancellationTokenSource();
            try
            {
                foreach (var packetInterface in _interfaces)
                    await packetInterface.StartAsync(cancellationToken).ConfigureAwait(false);

                _receivers = _interfaces
                    .Select(packetInterface => ReceivePacketsAsync(packetInterface, _lifetime.Token))
                    .ToArray();
                _ = CompleteWhenReceiversStopAsync(_receivers);
            }
            catch
            {
                _lifetime.Cancel();
                await DisposeInterfacesAsync().ConfigureAwait(false);
                _incoming.Writer.TryComplete();
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Reads packets received by any managed interface.</summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        await foreach (var packet in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return packet;
    }

    /// <summary>Sends a packet through every managed interface.</summary>
    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        await Task.WhenAll(_interfaces.Select(
            packetInterface => packetInterface.SendAsync(packet, cancellationToken).AsTask()))
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            _lifetime?.Cancel();
        }
        finally
        {
            _lifecycle.Release();
        }

        await DisposeInterfacesAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_receivers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _incoming.Writer.TryComplete();
            _lifetime?.Dispose();
            _lifecycle.Dispose();
        }
    }

    private async Task ReceivePacketsAsync(IPacketInterface packetInterface, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var packet in packetInterface.ReceiveAsync(cancellationToken).ConfigureAwait(false))
                await _incoming.Writer.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _incoming.Writer.TryComplete(exception);
            _lifetime?.Cancel();
            throw;
        }
    }

    private async Task CompleteWhenReceiversStopAsync(Task[] receivers)
    {
        try
        {
            await Task.WhenAll(receivers).ConfigureAwait(false);
            _incoming.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            _incoming.Writer.TryComplete(exception);
        }
    }

    private async ValueTask DisposeInterfacesAsync()
    {
        List<Exception>? exceptions = null;
        foreach (var packetInterface in _interfaces)
        {
            try
            {
                await packetInterface.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        if (exceptions is not null)
            throw new AggregateException("One or more packet interfaces failed to stop.", exceptions);
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
            throw new InvalidOperationException("The interface manager has not been started.");
    }
}
