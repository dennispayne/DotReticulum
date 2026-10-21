namespace DotReticulum.Applications.Runtime;

internal static class FileSystemSharingRetry
{
    public static bool IsTransient(Exception exception) => OperatingSystem.IsWindows()
        && exception is IOException or UnauthorizedAccessException
        && (exception.HResult & 0xffff) is 5 or 32 or 33;

    public static async Task ExecuteAsync(Action operation, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { operation(); return; }
            catch (Exception exception) when (attempt < 8 && IsTransient(exception))
            {
                // Win32 handles and delete-pending names can outlive their last managed owner.
                await Task.Delay(Math.Min(200, 10 << attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
