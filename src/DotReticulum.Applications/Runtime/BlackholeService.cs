using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Runtime;

public sealed record BlackholeOptions
{
    public bool Publish { get; init; }
    public IReadOnlyList<byte[]> Sources { get; init; } = [];
    public TimeSpan UpdateInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>The supplier must be authenticated by a completed RNS link, not merely an announce or requested address.</summary>
public sealed record BlackholeFetchResponse(byte[] AuthenticatedSource, byte[] PackedList);
public sealed record BlackholeUpdateResult(bool Succeeded, int Added, string? Error);

/// <summary>Records whether a failed attempt created a link (and must wait the normal update interval before retrying).</summary>
public sealed class BlackholeFetchException(string message, bool linkEstablished, Exception? innerException = null) : IOException(message, innerException)
{
    public bool LinkEstablished { get; } = linkEstablished;
}

/// <summary>
/// The rnstransport.info.blackhole /list publisher and bounded update scheduler.
/// Call TickAsync from the owning runtime; no constructor-started tasks or owned node lifetimes.
/// </summary>
public sealed class BlackholeService
{
    public const string AppName = "rnstransport";
    public const string RequestPath = "/list";
    public static readonly TimeSpan InitialWait = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan JobInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(25);
    private readonly BlackholeStore _store;
    private readonly BlackholeOptions _options;
    private readonly Func<byte[], CancellationToken, Task<BlackholeFetchResponse>> _fetch;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _updates = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _lastUpdates = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _started;
    private DateTimeOffset? _lastTick;

    public BlackholeService(BlackholeStore store, BlackholeOptions options,
        Func<byte[], CancellationToken, Task<BlackholeFetchResponse>> fetch, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fetch);
        if (options.UpdateInterval < TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(options), "Blackhole update interval must be at least two minutes.");
        _store = store;
        _options = options with { Sources = options.Sources.Select(s => s.ToArray()).ToArray() };
        foreach (var source in _options.Sources)
            if (!store.IsSourceAuthorized(source)) throw new ArgumentException("Service sources must be authorized by the store.", nameof(options));
        _fetch = fetch;
        _time = timeProvider ?? TimeProvider.System;
        _started = _time.GetUtcNow();
    }

    public Action<string, Exception>? Diagnostic { get; set; }

    public static byte[] SourceDestinationHash(byte[] identityHash) =>
        Destination.ComputeHash(identityHash, AppName, ["info", "blackhole"]);

    /// <summary>The registration callback controls destination ownership. Returns null when publication is disabled.</summary>
    public LocalDestination? RegisterPublisher(Identity identity, Action<LocalDestination> register)
    {
        if (!_options.Publish) return null;
        if (!identity.Hash.AsSpan().SequenceEqual(_store.LocalIdentityHash)) throw new ArgumentException("Publisher identity must match the store.", nameof(identity));
        var local = new LocalDestination(identity, Destination.Create(DestinationType.Single, AppName, ["info", "blackhole"], identity))
        {
            AcceptLinks = true,
        };
        local.RegisterRequestHandler(RequestPath, _ => _store.PublicationValue(), RequestPolicy.All);
        register(local);
        return local;
    }

    public async Task<BlackholeUpdateResult> UpdateSourceAsync(byte[] sourceIdentityHash, CancellationToken cancellationToken = default)
    {
        if (!_store.IsSourceAuthorized(sourceIdentityHash)) throw new UnauthorizedAccessException("Unconfigured blackhole source.");
        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await UpdateCoreAsync(sourceIdentityHash.ToArray(), cancellationToken).ConfigureAwait(false); }
        finally { _updates.Release(); }
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (now < _started + InitialWait || (_lastTick is { } last && now <= last + JobInterval)) return;
            _lastTick = now;
            _store.RemoveExpired();
            foreach (var source in _options.Sources)
            {
                var key = Convert.ToHexStringLower(source);
                if (_lastUpdates.TryGetValue(key, out var previous) && now <= previous + _options.UpdateInterval) continue;
                await UpdateCoreAsync(source, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _updates.Release(); }
    }

    private async Task<BlackholeUpdateResult> UpdateCoreAsync(byte[] source, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(SourceTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            var response = await _fetch(source.ToArray(), linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            _lastUpdates[Convert.ToHexStringLower(source)] = _time.GetUtcNow();
            if (!response.AuthenticatedSource.AsSpan().SequenceEqual(source)) throw new UnauthorizedAccessException("Blackhole response supplier does not match the configured identity.");
            return new BlackholeUpdateResult(true, _store.ApplySourceUpdate(source, response.PackedList), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var error = new TimeoutException("Blackhole source update timed out after 25 seconds.");
            Report(source, error);
            return new BlackholeUpdateResult(false, 0, error.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException or InvalidOperationException or TimeoutException)
        {
            if (ex is BlackholeFetchException { LinkEstablished: true }) _lastUpdates[Convert.ToHexStringLower(source)] = _time.GetUtcNow();
            Report(source, ex);
            return new BlackholeUpdateResult(false, 0, ex.Message);
        }
    }

    private void Report(byte[] source, Exception error)
    {
        var message = $"Blackhole update from {Convert.ToHexStringLower(source)} failed.";
        if (Diagnostic is { } diagnostic)
        {
            try { diagnostic(message, error); return; }
            catch (Exception observerError) when (observerError is not OutOfMemoryException and not StackOverflowException)
            {
                System.Diagnostics.Trace.TraceError("Blackhole diagnostic observer failed: {0}", observerError);
            }
        }

        System.Diagnostics.Trace.TraceError("{0} {1}", message, error);
    }

    /// <summary>Real native-stack callback: resolves the exact named destination, authenticates the link and requests an unwrapped map.</summary>
    public static Func<byte[], CancellationToken, Task<BlackholeFetchResponse>> NativeFetcher(ReticulumNode node) => NativeFetcher(node.Router);

    public static Func<byte[], CancellationToken, Task<BlackholeFetchResponse>> NativeFetcher(TransportRouter router) => async (source, token) =>
    {
        var destinationHash = SourceDestinationHash(source);
        var identity = await ResolveAsync(router, destinationHash, token).ConfigureAwait(false)
            ?? throw new IOException("No path available for blackhole source.");
        if (!identity.Hash.AsSpan().SequenceEqual(source)) throw new UnauthorizedAccessException("Resolved blackhole destination identity mismatch.");
        var up = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = router.Links.Connect(identity, destinationHash, _ => up.TrySetResult(), _ => up.TrySetException(new IOException("Blackhole source link closed.")));
        try
        {
            await up.Task.WaitAsync(SourceTimeout, token).ConfigureAwait(false);
            if (link.Status != LinkStatus.Active || link.RemoteIdentity is not { } remote || !remote.Hash.AsSpan().SequenceEqual(source))
                throw new UnauthorizedAccessException("Blackhole source link identity mismatch.");
            var request = link.Request(RequestPath, timeout: SourceTimeout.TotalSeconds, maxResponseSize: BlackholeWire.MaximumBytes + 32, cancellationToken: token)
                ?? throw new IOException("Blackhole source rejected the list request.");
            await request.ResponseTask.WaitAsync(SourceTimeout, token).ConfigureAwait(false);
            var raw = request.RawResponse ?? throw new FormatException("Blackhole source returned no raw response envelope.");
            return new BlackholeFetchResponse(remote.Hash.ToArray(), BlackholeWire.ExtractResponse(raw, request.RequestId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException or InvalidOperationException or TimeoutException)
        {
            throw new BlackholeFetchException(ex.Message, linkEstablished: true, ex);
        }
        finally { link.Teardown(); }
    };

    private static async Task<Identity?> ResolveAsync(TransportRouter router, byte[] destinationHash, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(SourceTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var nextRequest = DateTimeOffset.MinValue;
        try
        {
            while (true)
            {
                if (router.Engine.Known.Recall(destinationHash) is { } identity) return identity;
                if (DateTimeOffset.UtcNow >= nextRequest)
                {
                    await router.RequestPathAsync(destinationHash, cancellationToken: linked.Token).ConfigureAwait(false);
                    nextRequest = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                }

                await Task.Delay(100, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested) { return null; }
    }
}
