using DotReticulum.Core;

namespace DotReticulum.Applications.Rnsd;

public sealed partial class RnsdHost
{
    public async ValueTask<bool?> AttachInterfaceAsync(string name, RnsdConfig? configuration = null,
        bool internalForced = false, CancellationToken cancellationToken = default)
    {
        if (!internalForced) ThrowIfDisposed();
        if (IsConnectedToSharedInstance && !internalForced)
            return await RemoteInterfaceOperationAsync("attach_interface", name, cancellationToken).ConfigureAwait(false);
        if (!internalForced && !_configuration.EnableInterfaceManagement) return false;
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!internalForced) ThrowIfDisposed();
            return await AttachCoreAsync(name, configuration ?? _configuration, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutation.Release(); }
    }

    public async ValueTask<bool?> DetachInterfaceAsync(string name, bool internalForced = false, CancellationToken cancellationToken = default)
    {
        if (!internalForced) ThrowIfDisposed();
        if (IsConnectedToSharedInstance && !internalForced)
            return await RemoteInterfaceOperationAsync("detach_interface", name, cancellationToken).ConfigureAwait(false);
        if (!internalForced && !_configuration.EnableInterfaceManagement) return false;
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!internalForced) ThrowIfDisposed();
            return await DetachCoreAsync(name, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutation.Release(); }
    }

    public async ValueTask<bool?> ReloadInterfaceAsync(string name, RnsdConfig? configuration = null,
        bool internalForced = false, CancellationToken cancellationToken = default)
    {
        if (!internalForced) ThrowIfDisposed();
        if (IsConnectedToSharedInstance && !internalForced)
            return await RemoteInterfaceOperationAsync("reload_interface", name, cancellationToken).ConfigureAwait(false);
        if (!internalForced && !_configuration.EnableInterfaceManagement) return false;
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!internalForced) ThrowIfDisposed();
            var replacement = (configuration ?? _configuration).Interfaces.SingleOrDefault(i => i.Name == name);
            if (replacement is not null) ValidateAdvertisement(replacement);
            var detached = await DetachCoreAsync(name, cancellationToken).ConfigureAwait(false);
            return detached == true ? await AttachCoreAsync(name, configuration ?? _configuration, cancellationToken).ConfigureAwait(false) : detached;
        }
        finally { _mutation.Release(); }
    }

    private async ValueTask<bool?> AttachCoreAsync(string name, RnsdConfig configuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (_interfaces.Any(i => i.Name == name)) return false;
        }
        var settings = configuration.Interfaces.SingleOrDefault(i => i.Name == name);
        if (settings is null)
        {
            _log(1, $"Cannot attach interface '{name}': no configuration entry exists.");
            return null;
        }
        ValidateAdvertisement(settings);
        var iface = CreateInterface(settings, _extensions);
        InterfaceAccessCode? accessCode = null;
        try
        {
            accessCode = settings.CreateAccessCode();
            var routing = settings.Enabled ? settings.Routing
                : RnsdConfigParser.ResolveInterfaceRouting(settings, configuration.Routing, configuration.EnableTransport);
            ConfigureMode(iface, settings.Mode, routing.ToAnnounceOptions);
            await _manager.AttachAsync(iface, cancellationToken, accessCode: accessCode).ConfigureAwait(false);
            lock (_gate) _interfaces.Add((name, iface));
            RegisterAdvertisement(settings);
        }
        catch (Exception original)
        {
            RemoveModeHandler(iface);
            try
            {
                if (_manager.Contains(iface)) await _manager.DetachAsync(iface).ConfigureAwait(false);
                else await iface.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup) { throw new AggregateException("Interface attach and cleanup failed.", original, cleanup); }
            finally
            {
                accessCode?.Dispose();
                lock (_gate) _interfaces.RemoveAll(i => ReferenceEquals(i.Interface, iface));
            }
            throw;
        }
        _log(4, $"Interface '{name}' was attached.");
        return true;
    }

    private async ValueTask<bool?> DetachCoreAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        IPacketInterface? iface;
        lock (_gate) iface = _interfaces.FirstOrDefault(i => i.Name == name).Interface;
        if (iface is null)
        {
            _log(2, $"Cannot detach interface '{name}': it is not attached.");
            return null;
        }
        if (ReferenceEquals(iface, _shared.Bearer))
        {
            _log(2, "The shared-instance interface cannot be detached through interface management.");
            return false;
        }
        // Tick holds the announcer gate while reading host metadata; never wait for it under _gate.
        cancellationToken.ThrowIfCancellationRequested();
        if (_announcer is not null) await _announcer.UnregisterAsync(name, cancellationToken).ConfigureAwait(false);
        try { await _manager.DetachAsync(iface).ConfigureAwait(false); }
        finally
        {
            RemoveModeHandler(iface);
            lock (_gate) _interfaces.RemoveAll(i => ReferenceEquals(i.Interface, iface));
        }
        _log(4, $"Interface '{name}' was detached.");
        return true;
    }

    private async ValueTask<bool?> RemoteInterfaceOperationAsync(string operation, string name, CancellationToken cancellationToken)
    {
        var result = await CallManagementAsync(new Dictionary<object, object?> { ["manage"] = operation, ["name"] = name }, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            null => null,
            bool value => value,
            _ => throw new InvalidDataException("The shared-instance interface operation returned an invalid result."),
        };
    }

    private void RemoveModeHandler(IPacketInterface iface)
    {
        lock (_gate)
        {
            _interfaceReconfigure.Remove(iface);
            _pendingTunnelSynthesis.Remove(iface);
        }
        if (iface is ISpawningPacketInterface spawner && _modeHandlers.Remove(spawner, out var handler))
            spawner.ChildConnected -= handler;
    }

    private void ThrowIfDisposed()
    {
        lock (_lifecycleGate) ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
    }
}
