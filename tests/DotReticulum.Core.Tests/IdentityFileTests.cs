using DotReticulum.Core;

namespace DotReticulum.Core.Tests;

[Trait("Category", "Unit")]
public class IdentityFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotreticulum-idfile-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SaveThenLoadRoundTripsAndFileIsRawPrivateKey()
    {
        var path = Path.Combine(_dir, "id");
        using var id = Identity.Generate();
        IdentityFile.SaveNew(id, path);
        Assert.Equal(id.ExportPrivateKey(), File.ReadAllBytes(path));
        using var loaded = IdentityFile.Load(path);
        Assert.Equal(id.Hash, loaded.Hash);
    }

    [Fact]
    public void SaveNeverOverwrites()
    {
        var path = Path.Combine(_dir, "id");
        File.WriteAllBytes(path, [1, 2, 3]);
        using var id = Identity.Generate();
        Assert.Throws<IOException>(() => IdentityFile.SaveNew(id, path));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ConcurrentPublicationHasOneCompleteWinnerAndCannotReplaceIt()
    {
        var destination = Path.Combine(_dir, "published");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidates = Enumerable.Range(0, 32).Select(index =>
        {
            var path = Path.Combine(_dir, $"candidate-{index}");
            var contents = Enumerable.Repeat((byte)index, 65536).ToArray();
            File.WriteAllBytes(path, contents);
            return (Path: path, Contents: contents);
        }).ToArray();
        var attempts = candidates.Select(async candidate =>
        {
            await ready.Task;
            var won = AtomicFilePublication.TryPublishNew(candidate.Path, destination);
            Assert.Equal(65536, new FileInfo(destination).Length);
            return won;
        }).ToArray();
        ready.SetResult();
        var results = await Task.WhenAll(attempts);
        Assert.Single(results, won => won);
        Assert.Equal(candidates[Array.IndexOf(results, true)].Contents, File.ReadAllBytes(destination));
        var loser = candidates[Array.IndexOf(results, false)];
        using var reader = File.OpenRead(destination);
        Assert.False(AtomicFilePublication.TryPublishNew(loser.Path, destination));
        Assert.Equal(candidates[Array.IndexOf(results, true)].Contents, File.ReadAllBytes(destination));
    }

    [Fact]
    public void PublicationReportsMissingStagingFileWithoutCreatingDestination()
    {
        var destination = Path.Combine(_dir, "not-published");
        Assert.ThrowsAny<IOException>(() =>
            AtomicFilePublication.TryPublishNew(Path.Combine(_dir, "missing"), destination));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void PublicationRejectsDirectoryCollisionWithoutMovingStaging()
    {
        var staging = Path.Combine(_dir, "staging");
        File.WriteAllText(staging, "retained");
        var destination = Directory.CreateDirectory(Path.Combine(_dir, "directory")).FullName;
        Assert.ThrowsAny<IOException>(() => AtomicFilePublication.TryPublishNew(staging, destination));
        Assert.Equal("retained", File.ReadAllText(staging));
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    [Fact]
    public void LoadRejectsWrongSizes()
    {
        foreach (var size in new[] { 0, 32, 63, 65 })
        {
            var path = Path.Combine(_dir, $"bad{size}");
            File.WriteAllBytes(path, new byte[size]);
            Assert.Throws<InvalidDataException>(() => IdentityFile.Load(path));
        }
    }

    [Fact]
    public void UnixFileIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "id");
        using var id = Identity.Generate();
        IdentityFile.SaveNew(id, path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
