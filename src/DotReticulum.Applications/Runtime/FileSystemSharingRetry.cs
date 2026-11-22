namespace DotReticulum.Applications.Runtime;

internal static class FileSystemSharingRetry
{
    private const int MaximumRetries = 8;

    public static bool IsTransient(Exception exception) => OperatingSystem.IsWindows()
        && exception is IOException or UnauthorizedAccessException
        && (exception.HResult & 0xffff) is 5 or 32 or 33;

    public static void Execute(Action operation, Func<bool>? canRetry = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        for (var attempt = 0; ; attempt++)
        {
            try { operation(); return; }
            catch (Exception exception) when (attempt < MaximumRetries && IsTransient(exception) && (canRetry?.Invoke() ?? true))
            {
                Thread.Sleep(DelayMilliseconds(attempt));
            }
        }
    }

    public static async Task ExecuteAsync(Action operation, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { operation(); return; }
            catch (Exception exception) when (attempt < MaximumRetries && IsTransient(exception))
            {
                // Win32 handles and delete-pending names can outlive their last managed owner.
                await Task.Delay(DelayMilliseconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static int DelayMilliseconds(int attempt) => Math.Min(200, 10 << attempt);
}
