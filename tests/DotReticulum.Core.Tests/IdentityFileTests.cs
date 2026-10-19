using DotReticulum.Core;

namespace DotReticulum.Core.Tests;

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
