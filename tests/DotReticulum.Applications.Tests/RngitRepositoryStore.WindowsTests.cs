using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Applications.Rngit;

namespace DotReticulum.Applications.Tests;

public sealed class RngitWindowsPermissionFactAttribute : FactAttribute
{
    public RngitWindowsPermissionFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows PE .allowed resolver contract; Unix executable-bit behavior is covered separately.";
    }
}

public sealed partial class RngitRepositoryStoreTests
{
    [RngitWindowsPermissionFact]
    public async Task WindowsPeAllowedResolverRequiresAuthorizationAndOwnsFailureProcesses()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The fact attribute must skip Windows PE tests elsewhere.");
        using var backend = new PresentationProcessFixture();
        var store = Store();
        Success(await store.HandleCreateAsync(Request("public/new.git"), owner, CancellationToken.None));
        var group = Path.Combine(directory, "public");
        var allowed = store.Find("public/new.git")!.Path + ".allowed";
        foreach (var file in Directory.EnumerateFiles(backend.Directory))
            File.Copy(file, Path.Combine(group, Path.GetFileName(file)), overwrite: false);
        File.Move(Path.Combine(group, "RngitPresentationBackend.exe"), allowed, overwrite: true);
        var expectedImageHash = SHA256.HashData(File.ReadAllBytes(allowed));
        var modePath = Path.Combine(group, "rngit-backend-mode");
        var outputPath = Path.Combine(group, "rngit-backend-output");
        File.WriteAllText(modePath, "success");
        File.WriteAllText(outputPath, Convert.ToBase64String(Encoding.UTF8.GetBytes("adm:" + owner.HexHash)));
        Assert.False(store.ResolvePermission(owner, "public/new.git", "read"));

        var calls = 0;
        string Execute(string path)
        {
            calls++;
            var result = RngitProcess.RunAsync(path, [], workingDirectory: group,
                timeout: TimeSpan.FromSeconds(2), maxOutputBytes: RngitRepositoryStore.MaxPermissionBytes).GetAwaiter().GetResult();
            if (result.Code != 0) throw new IOException($"Owned resolver exited {result.Code}.");
            return Encoding.UTF8.GetString(result.Output);
        }
        var authorized = new RngitRepositoryStore(Config(), diagnostics.Add, permissionExecutor: Execute);
        Assert.True(authorized.ResolvePermission(owner, "public/new.git", "read"));
        Assert.True(authorized.ResolvePermission(owner, "public/new.git", "write"));
        Assert.True(calls >= 2);
        var response = Bytes(await authorized.HandlePermissionsAsync(
            Perms("public/new.git", false, "set", "r:all"), owner, CancellationToken.None));
        Assert.Equal(1, response[0]);
        Assert.Contains("Executable permission resolvers can only be modified node-side", Encoding.UTF8.GetString(response));
        Assert.Equal(expectedImageHash, SHA256.HashData(File.ReadAllBytes(allowed)));

        foreach (var mode in new[] { "fail", "timeout", "overflow" })
        {
            File.WriteAllText(modePath, mode);
            Assert.False(authorized.ResolvePermission(owner, "public/new.git", "read"));
            Assert.Equal(expectedImageHash, SHA256.HashData(File.ReadAllBytes(allowed)));
            using var exclusive = File.Open(allowed, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(exclusive.CanWrite);
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(group, ".rngit-store-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(group, "*.write-*"));

        File.WriteAllText(allowed, "w:none\n");
        var priorCalls = calls;
        Assert.False(authorized.ResolvePermission(owner, "public/new.git", "write"));
        Assert.True(authorized.ResolvePermission(owner, "public/new.git", "read"));
        Assert.Equal(priorCalls, calls);
    }

    [RngitWindowsPermissionFact]
    public async Task WindowsMalformedPeHeadersNeverInvokePermissionExecutor()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The fact attribute must skip Windows PE tests elsewhere.");
        var store = Store();
        Success(await store.HandleCreateAsync(Request("public/new.git"), owner, CancellationToken.None));
        var allowed = store.Find("public/new.git")!.Path + ".allowed";
        var calls = 0;
        store.PermissionExecutor = _ => { calls++; return "adm:" + owner.HexHash; };
        File.WriteAllText(allowed, "MZ ordinary static permission text\nr:all\n");
        Assert.True(store.ResolvePermission(owner, "public/new.git", "read"));
        Assert.Equal(0, calls);
        var cases = new List<byte[]> { "MZ ordinary static permission text\nr:all\n"u8.ToArray(), new byte[] { (byte)'M', (byte)'Z' } };
        foreach (var offset in new[] { int.MinValue, -1, 0, 63, 64, int.MaxValue })
        {
            var header = new byte[68];
            header[0] = (byte)'M';
            header[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60, 4), offset);
            cases.Add(header);
        }
        foreach (var bytes in cases)
        {
            File.WriteAllBytes(allowed, bytes);
            _ = store.ResolvePermission(owner, "public/new.git", "read");
        }
        Assert.Equal(0, calls);
    }
}
