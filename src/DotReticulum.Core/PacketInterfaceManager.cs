using System.Threading.Channels;

namespace DotReticulum.Core;

/// <summary>Owns packet interfaces and forwards incoming packets through a bounded queue.</summary>
public sealed class PacketInterfaceManager : IAsyncDisposable
{
    private readonly IPacketInterface[] _interfaces;
    private readonly Channel<ReceivedPacket> _incoming;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task[] _receivers = [];
    private volatile bool _started;
    private volatile bool _disposed;

    public PacketInterfaceManager(IEnumerable<IPacketInterface> interfaces, int queueCapacity = 256)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        if (queueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));

        _interfaces = interfaces.ToArray();
        if (_interfaces.Any(packetInterface => packetInterface is null))
            throw new ArgumentException("Interfaces cannot contain null entries.", nameof(interfaces));

        _incoming = Channel.CreateBounded<ReceivedPacket>(new BoundedChannelOptions(queueCapacity)
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
            catch (Exception startException)
            {
                _lifetime.Cancel();
                _disposed = true;
                Exception? disposeException = null;
                try
                {
                    await DisposeInterfacesAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    disposeException = exception;
                }

                _incoming.Writer.TryComplete(startException);
                _lifetime.Dispose();
                if (disposeException is not null)
                    throw new AggregateException("Starting and cleaning up packet interfaces failed.",
                        startException, disposeException);
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
        await foreach (var received in ReceiveWithSourceAsync(cancellationToken).ConfigureAwait(false))
            yield return received.Packet;
    }

    /// <summary>Reads packets together with the interface they arrived on.</summary>
    public async IAsyncEnumerable<ReceivedPacket> ReceiveWithSourceAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        await foreach (var received in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return received;
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

    /// <summary>Sends a packet through every managed interface except its ingress interface.</summary>
    public async ValueTask SendExceptAsync(
        ReadOnlyMemory<byte> packet,
        IPacketInterface excludedInterface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(excludedInterface);
        EnsureStarted();
        if (!_interfaces.Any(packetInterface => ReferenceEquals(packetInterface, excludedInterface)))
            throw new ArgumentException("The excluded interface is not managed by this manager.",
                nameof(excludedInterface));

        await Task.WhenAll(_interfaces
            .Where(packetInterface => !ReferenceEquals(packetInterface, excludedInterface))
            .Select(packetInterface => packetInterface.SendAsync(packet, cancellationToken).AsTask()))
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

        Exception? disposeException = null;
        try
        {
            await DisposeInterfacesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposeException = exception;
        }

        try
        {
            await Task.WhenAll(_receivers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            disposeException = disposeException is null
                ? exception
                : new AggregateException(disposeException, exception);
        }
        finally
        {
            _incoming.Writer.TryComplete();
            _lifetime?.Dispose();
        }

        if (disposeException is not null)
            throw disposeException;
    }

    private async Task ReceivePacketsAsync(IPacketInterface packetInterface, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var packet in packetInterface.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Packet.TryParse(packet, out _))
                    await _incoming.Writer.WriteAsync(new ReceivedPacket(packetInterface, packet.ToArray()),
                        cancellationToken).ConfigureAwait(false);
            }
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
