using System.ComponentModel;
using DotReticulum.Core;

namespace DotReticulum.Core.Tests;

public sealed class WindowsPublicationTheoryAttribute : TheoryAttribute
{
    public WindowsPublicationTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows namespace normalization requires Windows path semantics.";
    }
}

public sealed class WindowsPublicationFactAttribute : FactAttribute
{
    public WindowsPublicationFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "The extended-length namespace is Windows-specific.";
    }
}

[Trait("Category", "Integration")]
public sealed class AtomicFilePublicationTests : IDisposable
{
    private readonly string root = Path.Combine(Directory.GetCurrentDirectory(), "atomic-publication-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [WindowsPublicationTheory]
    [InlineData(@"C:\directory\..\escaped # % λ\file", @"\\?\C:\escaped # % λ\file")]
    [InlineData("C:/directory/../escaped # % λ/file", @"\\?\C:\escaped # % λ\file")]
    [InlineData(@"\\server\share\directory\..\escaped # % λ\file", @"\\?\UNC\server\share\escaped # % λ\file")]
    [InlineData(@"\\?\C:\escaped # % λ\file", @"\\?\C:\escaped # % λ\file")]
    [InlineData(@"\\?\UNC\server\share\escaped # % λ\file", @"\\?\UNC\server\share\escaped # % λ\file")]
    [InlineData(@"\\.\C:\escaped # % λ\file", @"\\.\C:\escaped # % λ\file")]
    public void WindowsNamespaceUsesCanonicalAbsolutePathsWithoutDoublePrefixing(string input, string expected) =>
        Assert.Equal(expected, AtomicFilePublication.WindowsPath(input));

    [Fact]
    public Task LongUnicodePublicationHasOneCompleteWinnerAndCallerOwnedCleanup() =>
        CheckLongUnicodePublication(extended: false);

    [WindowsPublicationFact]
    public Task ExtendedLongUnicodePublicationHasOneCompleteWinnerAndCallerOwnedCleanup() =>
        CheckLongUnicodePublication(extended: true);

    private async Task CheckLongUnicodePublication(bool extended)
    {
        var directory = root;
        while (directory.Length < 650) directory = Path.Combine(directory, new string('p', 60));
        directory = Path.Combine(directory, "escaped # % λ");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "published");
        var candidates = Enumerable.Range(0, 16).Select(index =>
        {
            var path = Path.Combine(directory, $"candidate-{index}");
            var bytes = Enumerable.Repeat((byte)index, 65536).ToArray();
            File.WriteAllBytes(path, bytes);
            return (Path: path, Bytes: bytes);
        }).ToArray();
        string NativePath(string path) => extended ? @"\\?\" + path : path;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = candidates.Select(async candidate =>
        {
            await ready.Task;
            return AtomicFilePublication.TryPublishNew(NativePath(candidate.Path), NativePath(destination));
        }).ToArray();
        ready.SetResult();
        var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, won => won);
        var expected = candidates[Array.IndexOf(results, true)].Bytes;
        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.All(candidates, candidate => Assert.Equal(candidate.Bytes, File.ReadAllBytes(candidate.Path)));
        foreach (var candidate in candidates) File.Delete(candidate.Path);
        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.Single(Directory.GetFiles(directory));
        var staging = Path.Combine(directory, "rejected");
        File.WriteAllText(staging, "not the winner");
        using var reader = File.OpenRead(destination);
        Assert.False(AtomicFilePublication.TryPublishNew(NativePath(staging), NativePath(destination)));
        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.Equal("not the winner", File.ReadAllText(staging));
    }

    [Fact]
    public void RelativePathsAreNormalizedBeforeEnteringExtendedNamespace()
    {
        Directory.CreateDirectory(Path.Combine(root, "subdirectory"));
        var staging = Path.Combine(root, "staging");
        File.WriteAllText(staging, "published");
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), root);
        Assert.True(AtomicFilePublication.TryPublishNew(
            Path.Combine(relative, "subdirectory", "..", "staging"),
            Path.Combine(relative, ".", "published")));
        Assert.Equal("published", File.ReadAllText(Path.Combine(root, "published")));
        Assert.Equal("published", File.ReadAllText(staging));
    }

    [Fact]
    public void NativeFailurePreservesBothPathContextsAndLeavesStagingOwnedByCaller()
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "staging");
        File.WriteAllText(staging, "retained");
        var destination = Path.Combine(root, "missing", "destination");
        var error = Assert.Throws<IOException>(() => AtomicFilePublication.TryPublishNew(staging, destination));
        Assert.Contains(staging, error.Message);
        Assert.Contains(destination, error.Message);
        Assert.NotEqual(0, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
        Assert.Equal("retained", File.ReadAllText(staging));
        Assert.False(File.Exists(destination));
        Assert.Single(Directory.GetFiles(root));
    }
}
