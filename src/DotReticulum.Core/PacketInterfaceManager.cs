using System.Threading.Channels;

namespace DotReticulum.Core;

/// <summary>
/// Owns a set of <see cref="IPacketInterface"/>s: starts them once, merges their received packets into one bounded
/// channel (wait-on-full backpressure), drops malformed packets, and fans out sends. Disposing the manager disposes
/// every interface.
/// </summary>
public sealed class PacketInterfaceManager : IAsyncDisposable
{
    public const int DefaultChannelCapacity = 256;

    private readonly List<IPacketInterface> _interfaces;
    private readonly HashSet<IPacketInterface> _children = [];
    private bool _receiversLaunched;
    private readonly Channel<ReceivedPacket> _channel;
    private readonly CancellationTokenSource _receiversCts = new();
    private readonly object _gate = new();
    private readonly List<Task> _receiverTasks = [];
    private bool _started;
    private Task? _disposeTask;
    private int _activeReceivers;

    public PacketInterfaceManager(IEnumerable<IPacketInterface> interfaces, int channelCapacity = DefaultChannelCapacity)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);
        _interfaces = [.. interfaces];
        foreach (var iface in _interfaces)
        {
            ArgumentNullException.ThrowIfNull(iface);
        }

        if (_interfaces.Distinct().Count() != _interfaces.Count)
        {
            throw new ArgumentException("The same interface was supplied more than once.", nameof(interfaces));
        }

        foreach (var spawner in _interfaces.OfType<ISpawningPacketInterface>())
        {
            spawner.ChildConnected += AdoptChild;
        }

        _channel = Channel.CreateBounded<ReceivedPacket>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>A snapshot of the current interfaces, including children spawned by <see cref="ISpawningPacketInterface"/>s.</summary>
    public IReadOnlyList<IPacketInterface> Interfaces
    {
        get
        {
            lock (_gate)
            {
                return [.. _interfaces];
            }
        }
    }

    /// <summary>Whether <paramref name="iface"/> is currently owned by (and not yet removed from) this manager.</summary>
    public bool Contains(IPacketInterface iface)
    {
        lock (_gate)
        {
            return _interfaces.Contains(iface);
        }
    }

    private void AdoptChild(IPacketInterface child)
    {
        lock (_gate)
        {
            if (_disposeTask is null && !_interfaces.Contains(child))
            {
                _interfaces.Add(child);
                _children.Add(child);
                if (_receiversLaunched)
                {
                    Interlocked.Increment(ref _activeReceivers);
                    _receiverTasks.Add(Task.Run(() => ReceiveLoopAsync(child), CancellationToken.None));
                }

                return;
            }
        }

        _ = child.DisposeAsync().AsTask();
    }

    /// <summary>
    /// Starts every interface in order, then begins receiving. If any start fails (or is cancelled) the manager disposes
    /// all interfaces, becomes unusable, and the original exception is rethrown. May be called only once.
    /// </summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started)
            {
                throw new InvalidOperationException("The manager has already been started.");
            }

            _started = true;
        }

        try
        {
            foreach (var iface in Interfaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(IsDisposing, this);
                if (Contains(iface) && !IsChild(iface))
                {
                    await iface.StartAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                _activeReceivers = _interfaces.Count;
                _receiversLaunched = true;
                foreach (var iface in _interfaces)
                {
                    _receiverTasks.Add(Task.Run(() => ReceiveLoopAsync(iface), CancellationToken.None));
                }

                if (_interfaces.Count == 0)
                {
                    _channel.Writer.TryComplete();
                }
            }
        }
        catch (Exception ex)
        {
            _channel.Writer.TryComplete(ex);
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeEx)
            {
                throw new AggregateException(ex, disposeEx);
            }

            throw;
        }
    }

    /// <summary>Yields valid packets from all interfaces. The sequence faults if a receiver faults.</summary>
    public async IAsyncEnumerable<Packet> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var received in ReceiveWithSourceAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return received.Packet;
        }
    }

    /// <summary>Like <see cref="ReceiveAsync"/> but includes the arrival interface.</summary>
    public IAsyncEnumerable<ReceivedPacket> ReceiveWithSourceAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Sends to every interface. Failures on one interface do not prevent delivery to the others.</summary>
    public ValueTask SendAsync(Packet packet, CancellationToken cancellationToken = default) =>
        SendCoreAsync(packet, null, cancellationToken);

    /// <summary>Sends to every interface except <paramref name="excluded"/> (typically the ingress interface).</summary>
    public ValueTask SendExceptAsync(Packet packet, IPacketInterface excluded, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        if (!Contains(excluded))
        {
            throw new ArgumentException("The excluded interface is not owned by this manager.", nameof(excluded));
        }

        return SendCoreAsync(packet, excluded, cancellationToken);
    }

    /// <summary>Sends to exactly one interface (a routed unicast hop). Throws if the interface is not (or no longer) owned by this manager.</summary>
    public ValueTask SendToAsync(Packet packet, IPacketInterface target, CancellationToken cancellationToken = default) =>
        SendRawToAsync(packet?.ToArray() ?? throw new ArgumentNullException(nameof(packet)), target, cancellationToken);

    /// <summary>As <see cref="SendToAsync"/> for already serialised wire bytes.</summary>
    public async ValueTask SendRawToAsync(ReadOnlyMemory<byte> raw, IPacketInterface target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!_started)
            {
                throw new InvalidOperationException("The manager has not been started.");
            }

            if (!_interfaces.Contains(target))
            {
                throw new ArgumentException("The target interface is not owned by this manager.", nameof(target));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await target.SendAsync(raw, cancellationToken).ConfigureAwait(false);
    }

    private bool IsChild(IPacketInterface iface)
    {
        lock (_gate)
        {
            return _children.Contains(iface);
        }
    }

    private bool IsDisposing
    {
        get
        {
            lock (_gate)
            {
                return _disposeTask is not null;
            }
        }
    }

    private async ValueTask SendCoreAsync(Packet packet, IPacketInterface? excluded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!_started)
            {
                throw new InvalidOperationException("The manager has not been started.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = packet.ToArray();
        var targets = Interfaces;
        var pending = new List<Task>(targets.Count);
        foreach (var iface in targets)
        {
            if (ReferenceEquals(iface, excluded))
            {
                continue;
            }

            pending.Add(SendOneAsync(iface, bytes, cancellationToken));
        }

        var all = Task.WhenAll(pending);
        try
        {
            await all.ConfigureAwait(false);
        }
        catch
        {
            if (all.Exception is { InnerExceptions.Count: > 1 } aggregate)
            {
                throw aggregate;
            }

            throw;
        }
    }

    private static async Task SendOneAsync(IPacketInterface iface, byte[] bytes, CancellationToken cancellationToken) =>
        await iface.SendAsync(bytes, cancellationToken).ConfigureAwait(false);
    private async Task ReceiveLoopAsync(IPacketInterface iface)
    {
        var token = _receiversCts.Token;
        try
        {
            await foreach (var raw in iface.ReceiveAsync(token).ConfigureAwait(false))
            {
                if (!Packet.TryParse(raw, out var packet))
                {
                    continue;
                }

                await _channel.Writer.WriteAsync(new ReceivedPacket(raw, packet, iface), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception) when (IsChild(iface))
        {
            // A dynamically spawned connection failing (e.g. a client reset) only removes that connection.
        }
        catch (Exception ex)
        {
            _channel.Writer.TryComplete(ex);
            try
            {
                await _receiversCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            if (IsChild(iface))
            {
                lock (_gate)
                {
                    _interfaces.Remove(iface);
                    _children.Remove(iface);
                }

                try
                {
                    await iface.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The child's connection is already gone.
                }
            }

            if (Interlocked.Decrement(ref _activeReceivers) == 0)
            {
                _channel.Writer.TryComplete();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            task = _disposeTask;
        }

        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync()
    {
        // Yield so the caller's lock is released before any real work runs.
        await Task.Yield();
        try
        {
            await _receiversCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        _channel.Writer.TryComplete();

        var errors = new List<Exception>();
        IPacketInterface[] toDispose;
        lock (_gate)
        {
            toDispose = [.. _interfaces];
        }

        foreach (var spawner in toDispose.OfType<ISpawningPacketInterface>())
        {
            spawner.ChildConnected -= AdoptChild;
        }

        foreach (var iface in toDispose)
        {
            try
            {
                await iface.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        Task[] receivers;
        lock (_gate)
        {
            receivers = [.. _receiverTasks];
        }

        // Receiver loops never throw (they convert faults to channel completion).
        await Task.WhenAll(receivers).ConfigureAwait(false);
        _receiversCts.Dispose();

        if (errors.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }
        else if (errors.Count > 1)
        {
            throw new AggregateException(errors);
        }
    }
}
