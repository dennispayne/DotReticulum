using System.Security.Cryptography;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnsd;

public sealed partial class RnsdHost
{
    private void ConfigureSharedRouting()
    {
        Router.ConfigureSharedClient(IsConnectedToSharedInstance);
        if (_shared.Server is { } server)
        {
            Action<IPacketInterface> handler = child => Router.RegisterLocalClientInterface(child);
            _modeHandlers.Add(server, handler);
            server.ChildConnected += handler;
        }
        if (_shared.Bearer is LocalSharedPacketInterface client)
            client.ConnectionChanged += connected =>
            {
                if (!connected) Router.DetachInterface(client);
                else _sharedReconnects.Writer.TryWrite(true);
                _log(connected ? 4 : 2, connected ? "Reconnected to shared instance." : "Shared instance disconnected; dependent paths and links were released.");
            };
    }

    private void InitializeServices()
    {
        if (_configuration.SharedInstance.RpcKey is { } explicitKey) _rpcKey = explicitKey.ToArray();
        else
        {
            var privateKey = _internalIdentity.ExportPrivateKey();
            try { _rpcKey = SHA256.HashData(privateKey); }
            finally { CryptographicOperations.ZeroMemory(privateKey); }
        }
        if (IsConnectedToSharedInstance)
        {
            _rpcClient = new ManagementRpcClient(_configuration.SharedInstance.RpcEndpoint, _rpcKey, _configuration.SharedInstance.OperationTimeout);
            return;
        }
        if (_shared.Role == SharedInstanceRole.Host)
            _rpcServer = new ManagementRpcServer(_configuration.SharedInstance.RpcEndpoint, _rpcKey, HandleManagementAsync,
                _configuration.SharedInstance.OperationTimeout, diagnostic: error => _log(1, $"Management RPC: {error.Message}"));
        if (_configuration.EnableRemoteManagement)
            new ManagementRemoteService(HandleReadOnlyManagement, _configuration.RemoteManagementAllowed,
                error => _log(1, $"Remote management: {error.Message}")).Register(Router.TransportIdentity, Router.RegisterLocalDestination);
        _blackholeService = new BlackholeService(_blackholes, _configuration.Blackholes, BlackholeService.NativeFetcher(Router));
        if (_configuration.Blackholes.Publish) _blackholeService.RegisterPublisher(Router.TransportIdentity, Router.RegisterLocalDestination);
        _blackholes.Changed += RemoveBlackholedPaths;
        if (_configuration.Discovery.Enabled)
        {
            _discovery = new DiscoveryService(Path.Combine(_storageDirectory, "discovery"), _configuration.Discovery,
                ConnectDiscoveredAsync, networkIdentity: _networkIdentity, isBlocked: _blackholes.IsBlackholed,
                diagnostic: message => _log(2, message),
                endpointExists: record => _configuration.Interfaces.Any(i => i.Enabled &&
                    i.Get("target_host") == record.ReachableOn && i.Get("target_port") == record.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                interfaceNames: () => Interfaces.Select(i => i.Name).ToArray(),
                bootstrap: AttachBootstrapAsync);
            foreach (var settings in _configuration.Interfaces.Where(i => i.Enabled && i.BootstrapOnly))
                if (Interfaces.FirstOrDefault(i => i.Name == settings.Name).Interface is { } bootstrap)
                    _discovery.RegisterBootstrap(CreateDiscoveryLease(settings.Name, bootstrap));
            _discoveryHandler = new CallbackAnnounceHandler(DiscoveryCodec.Aspect, QueueDiscovery, receivePathResponses: true);
            Router.Engine.RegisterHandler(_discoveryHandler);
        }
        ConfigureAdvertisements();
    }

    private readonly System.Threading.Channels.Channel<(byte[] PublicKey, byte[] Payload, int Hops)> _discoveryQueue =
        System.Threading.Channels.Channel.CreateBounded<(byte[], byte[], int)>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly System.Threading.Channels.Channel<bool> _sharedReconnects =
        System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private void QueueDiscovery(AnnounceReceived announce)
    {
        if (announce.AppData is null) return;
        if (!_discoveryQueue.Writer.TryWrite((announce.Identity.ExportPublicKey(), announce.AppData.ToArray(), announce.Hops)))
            _log(7, "Discovery validator is busy; advertisement was not queued.");
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await _manager.StartAsync(linked.Token).ConfigureAwait(false);
            if (!IsConnectedToSharedInstance) await _stateStore.RestoreAsync(new HostStateProvider(this), linked.Token).ConfigureAwait(false);
            if (_rpcServer is not null) await _rpcServer.StartAsync(linked.Token).ConfigureAwait(false);
            _startedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception original)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            var failures = new List<Exception>();
            async Task Release(Func<ValueTask> action)
            {
                try { await action().ConfigureAwait(false); }
                catch (Exception error) { _log(1, $"Failed-start cleanup: {error.Message}"); failures.Add(error); }
            }
            if (_rpcServer is not null) await Release(_rpcServer.DisposeAsync).ConfigureAwait(false);
            await Release(Router.DisposeAsync).ConfigureAwait(false);
            await Release(_manager.DisposeAsync).ConfigureAwait(false);
            await Release(_shared.DisposeAsync).ConfigureAwait(false);
            if (!ReferenceEquals(Router.TransportIdentity, _internalIdentity)) Router.TransportIdentity.Dispose();
            _internalIdentity.Dispose();
            _networkIdentity?.Dispose();
            CryptographicOperations.ZeroMemory(_rpcKey);
            if (failures.Count != 0) throw new AggregateException("Runtime startup and cleanup failed.", new[] { original }.Concat(failures));
            throw;
        }
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var routing = Router.RunAsync(linked.Token);
        var maintenance = MaintenanceAsync(linked.Token);
        var discovery = ProcessDiscoveryAsync(linked.Token);
        var reconnects = ProcessSharedReconnectsAsync(linked.Token);
        await Task.WhenAny(routing, maintenance, discovery, reconnects).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(routing, maintenance, discovery, reconnects).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !routing.IsFaulted && !maintenance.IsFaulted && !discovery.IsFaulted && !reconnects.IsFaulted) { }
    }

    private async Task ProcessSharedReconnectsAsync(CancellationToken token)
    {
        await foreach (var _ in _sharedReconnects.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            try { await Router.SharedConnectionReappearedAsync(token).ConfigureAwait(false); }
            catch (IOException error) { _log(2, $"Shared instance disconnected during destination reannouncement: {error.Message}"); }
        }
    }

    private async Task ProcessDiscoveryAsync(CancellationToken token)
    {
        await foreach (var (publicKey, payload, hops) in _discoveryQueue.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            using var identity = Identity.FromPublicKey(publicKey);
            if (_discovery is not null) await _discovery.ReceiveAsync(identity, payload, hops, token).ConfigureAwait(false);
        }
    }

    private async Task MaintenanceAsync(CancellationToken token)
    {
        var lastPersist = DateTimeOffset.UtcNow;
        var lastClean = lastPersist;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            if (_discovery is not null) await _discovery.TickAsync(token).ConfigureAwait(false);
            await TickDiscoveryAdvertisementsAsync(token).ConfigureAwait(false);
            if (_blackholeService is not null) await _blackholeService.TickAsync(token).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (now - lastClean >= TimeSpan.FromMinutes(15))
            {
                Router.Engine.Known.Cleanup(hash => Router.HasPath(hash));
                RuntimeHousekeeping.CleanCaches(_storageDirectory, now, message => _log(1, message));
                lastClean = now;
            }
            if (now - lastPersist >= TimeSpan.FromHours(12))
            {
                await PersistAsync(token).ConfigureAwait(false);
                lastPersist = now;
            }
        } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    public Task PersistAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return PersistCoreAsync(cancellationToken);
    }

    private Task PersistCoreAsync(CancellationToken cancellationToken = default) =>
        IsConnectedToSharedInstance ? Task.CompletedTask : _stateStore.SaveAsync(new HostStateProvider(this), cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        var errors = new List<Exception>();
        async Task Release(Func<Task> release)
        {
            try { await release().ConfigureAwait(false); }
            catch (Exception error) { _log(1, $"Runtime shutdown: {error.Message}"); errors.Add(error); }
        }
        if (_startTask is not null) await Release(() => _startTask).ConfigureAwait(false);
        if (_runTask is not null) await Release(() => _runTask).ConfigureAwait(false);
        if (_startTask?.IsCompletedSuccessfully == true) await Release(() => PersistCoreAsync()).ConfigureAwait(false);
        if (_discoveryHandler is not null) Router.Engine.DeregisterHandler(_discoveryHandler);
        if (_rpcServer is not null) await Release(() => _rpcServer.DisposeAsync().AsTask()).ConfigureAwait(false);
        _rpcClient?.Dispose();
        if (_discovery is not null) await Release(() => _discovery.DisposeAsync().AsTask()).ConfigureAwait(false);
        await _mutation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_announcer is not null) await Release(() => _announcer.DisposeAsync().AsTask()).ConfigureAwait(false);
            await Release(() => Router.DisposeAsync().AsTask()).ConfigureAwait(false);
            await Release(() => _manager.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        finally { _mutation.Release(); }
        await Release(() => _shared.DisposeAsync().AsTask()).ConfigureAwait(false);
        foreach (var (spawner, handler) in _modeHandlers) spawner.ChildConnected -= handler;
        _modeHandlers.Clear();
        _blackholes.Changed -= RemoveBlackholedPaths;
        if (!ReferenceEquals(Router.TransportIdentity, _internalIdentity)) Router.TransportIdentity.Dispose();
        _internalIdentity.Dispose();
        _networkIdentity?.Dispose();
        CryptographicOperations.ZeroMemory(_rpcKey);
        _lifetime.Dispose();
        if (errors.Count > 0) throw new AggregateException("Runtime shutdown failed.", errors);
    }
}
