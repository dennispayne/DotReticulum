namespace DotReticulum.Transport;

/// <summary>Runs caller-stream cancellation callbacks off protocol locks and joins them before disposing their source.</summary>
internal sealed class ResourceCancellationScope : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration _caller;
    private readonly CancellationTokenRegistration _link;
    private readonly Action<Exception> _diagnostic;
    private Task _cancellation = Task.CompletedTask;
    private bool _cancelled;
    private bool _disposed;

    public ResourceCancellationScope(CancellationToken caller, CancellationToken link, Action<Exception> diagnostic)
    {
        _diagnostic = diagnostic;
        _caller = caller.UnsafeRegister(static state => ((ResourceCancellationScope)state!).Cancel(), this);
        _link = link.UnsafeRegister(static state => ((ResourceCancellationScope)state!).Cancel(), this);
    }

    public CancellationToken Token => _source.Token;

    private void Cancel()
    {
        lock (_gate)
        {
            if (_cancelled || _disposed) return;
            _cancelled = true;
            _cancellation = _source.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _caller.DisposeAsync().ConfigureAwait(false);
        await _link.DisposeAsync().ConfigureAwait(false);
        Task cancellation;
        lock (_gate)
        {
            _disposed = true;
            cancellation = _cancellation;
        }
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception ex) { _diagnostic(ex); }
        finally { _source.Dispose(); }
    }
}
