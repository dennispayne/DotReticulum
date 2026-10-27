using DotReticulum.Applications.Runtime;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Unit")]
public sealed class FileSystemSharingRetryTests
{
    [Fact]
    public async Task NonSharingFailuresPropagateWithoutRetry()
    {
        var failure = new IOException("not a sharing failure");
        var attempts = 0;
        var observed = await Assert.ThrowsAsync<IOException>(() => FileSystemSharingRetry.ExecuteAsync(() =>
        {
            attempts++;
            throw failure;
        }));
        Assert.Same(failure, observed);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AlreadyCanceledOperationDoesNotRun()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileSystemSharingRetry.ExecuteAsync(() => attempts++, stop.Token));
        Assert.Equal(0, attempts);
    }

    [WindowsPersistenceFact]
    public async Task PersistentWindowsFailureRetainsOriginalExceptionAndHasFiniteAttempts()
    {
        var failure = new IOException("busy", unchecked((int)0x80070020));
        var attempts = 0;
        var observed = await Assert.ThrowsAsync<IOException>(() => FileSystemSharingRetry.ExecuteAsync(() =>
        {
            attempts++;
            throw failure;
        }));
        Assert.Same(failure, observed);
        Assert.Equal(9, attempts);
    }

    [WindowsPersistenceFact]
    public async Task ActualWindowsFileLeaseIsReleasedBeforeAtomicReplacementSucceeds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dotreticulum-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "staging");
        var target = Path.Combine(directory, "committed");
        await File.WriteAllTextAsync(source, "new");
        await File.WriteAllTextAsync(target, "old");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            Task publication;
            var attempts = 0;
            using (var lease = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                publication = FileSystemSharingRetry.ExecuteAsync(() =>
                {
                    attempts++;
                    File.Move(source, target, overwrite: true);
                }, deadline.Token);
                Assert.Equal(1, attempts);
                Assert.False(publication.IsCompleted);
                Assert.True(File.Exists(source));
            }
            await publication;
            Assert.True(attempts >= 2);
            Assert.Equal("new", await File.ReadAllTextAsync(target));
            Assert.False(File.Exists(source));
        }
        finally
        {
            File.Delete(source);
            File.Delete(target);
            Directory.Delete(directory);
        }
    }
}
