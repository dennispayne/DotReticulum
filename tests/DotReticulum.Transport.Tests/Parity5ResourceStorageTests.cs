using System.Collections.Concurrent;
using System.Text.Json;

namespace DotReticulum.Transport.Tests;

internal sealed class ResourceUnixFactAttribute : FactAttribute
{
    public ResourceUnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix file modes are unavailable on Windows; resource files inherit directory ACLs.";
    }
}

[Trait("Category", "Unit")]
public class Parity5ResourceStorageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity5-resource-vectors.json"))).RootElement;

    private static string DirectoryPath() => Path.Combine(Environment.CurrentDirectory, "TestResults", "resource-scavenge-" + Guid.NewGuid().ToString("N"));
    private static string NativePath(string directory, string prefix = "resource-") => Path.Combine(directory, prefix + Guid.NewGuid().ToString("N") + ".bin");
    private static void WriteAged(string path, int age)
    {
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, Now.AddSeconds(-age).UtcDateTime);
    }

    [ResourceUnixFact]
    public void ReceiveSpoolAndOwnershipMarker_AreCreatedOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Unix-only resource test was not skipped.");
        var directory = DirectoryPath();
        try
        {
            using var storage = new ResourceStorage(new ResourcePolicy { StorageDirectory = directory, MemoryThreshold = 0 });
            storage.Write([1, 2, 3]);
            var path = Assert.Single(Directory.GetFiles(directory, "resource-*.bin"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(directory, ResourceStorageScavenger.OwnershipMarkerFileName)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("resource-")]
    [InlineData("input-")]
    public void Scavenge_MatchesExecutedReferenceAgeBoundary_AndRetainsUnrelatedFiles(string prefix)
    {
        var directory = DirectoryPath();
        try
        {
            ResourceStorageScavenger.Initialize(directory);
            Assert.Equal((double)Vectors.GetProperty("resourceCacheAge").GetInt32(), ResourceStorageScavenger.CacheLifetime.TotalSeconds);
            var paths = new Dictionary<string, string>();
            foreach (var row in Vectors.GetProperty("resourceCacheCleanup").EnumerateArray())
            {
                var label = row.GetProperty("label").GetString()!;
                var path = label == "unrelated" ? Path.Combine(directory, "user-file.bin") : NativePath(directory, prefix);
                paths[label] = path;
                WriteAged(path, row.GetProperty("age").GetInt32());
            }
            var nested = Path.Combine(directory, "nested");
            Directory.CreateDirectory(nested);
            var nestedFile = NativePath(nested);
            WriteAged(nestedFile, 86401);
            var namedDirectory = NativePath(directory);
            Directory.CreateDirectory(namedDirectory);
            var logs = new List<string>();
            var result = ResourceStorageScavenger.Scavenge(directory, logs.Add, Now);
            Assert.True(result.OwnedDirectory);
            Assert.Equal(1, result.Deleted);
            Assert.Equal(2, result.Fresh);
            Assert.Equal(0, result.Errors);
            foreach (var row in Vectors.GetProperty("resourceCacheCleanup").EnumerateArray())
                Assert.Equal(row.GetProperty("retained").GetBoolean(), File.Exists(paths[row.GetProperty("label").GetString()!]));
            Assert.True(File.Exists(nestedFile));
            Assert.True(Directory.Exists(namedDirectory));
            Assert.Contains(logs, message => message.Contains("Deleted expired resource cache file:", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("resource-", FileAccess.Read)]
    [InlineData("resource-", FileAccess.ReadWrite)]
    [InlineData("input-", FileAccess.Read)]
    [InlineData("input-", FileAccess.ReadWrite)]
    public void ActiveNativeFile_IsLeasedNotDeleted_ThenScavengedAfterItsHandleCloses(string prefix, FileAccess access)
    {
        var directory = DirectoryPath();
        try
        {
            ResourceStorageScavenger.Initialize(directory);
            var path = NativePath(directory, prefix);
            WriteAged(path, 86401);
            var logs = new List<string>();
            using (var active = new FileStream(path, FileMode.Open, access, FileShare.Read))
            {
                var result = ResourceStorageScavenger.Scavenge(directory, logs.Add, Now);
                Assert.Equal(0, result.Deleted);
                Assert.Equal(1, result.Active);
                Assert.True(File.Exists(path));
                Assert.Equal(3, active.Length);
                Assert.Equal(1, active.ReadByte());
            }
            Assert.Equal(1, ResourceStorageScavenger.Scavenge(directory, now: Now).Deleted);
            Assert.False(File.Exists(path));
            Assert.Contains(logs, message => message.Contains("skipped active or unavailable file", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("resource-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.bin")]
    [InlineData("resource-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bin")]
    [InlineData("resource-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bin.meta")]
    [InlineData("other-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bin")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void NonNativeNames_AreNeverDeleted(string name)
    {
        var directory = DirectoryPath();
        try
        {
            ResourceStorageScavenger.Initialize(directory);
            var path = Path.Combine(directory, name);
            WriteAged(path, 86401);
            var result = ResourceStorageScavenger.Scavenge(directory, now: Now);
            Assert.Equal(0, result.Deleted);
            Assert.True(File.Exists(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ActiveResourceStorage_RemainsReadableAndUndeleted()
    {
        var directory = DirectoryPath();
        try
        {
            using var storage = new ResourceStorage(new ResourcePolicy { MemoryThreshold = 0, StorageDirectory = directory });
            storage.Write([1, 2, 3]);
            var path = Assert.Single(Directory.GetFiles(directory, "resource-*.bin"));
            using var input = storage.OpenRead();
            File.SetLastWriteTimeUtc(path, Now.AddDays(-2).UtcDateTime);
            var result = ResourceStorageScavenger.Scavenge(directory, now: Now);
            Assert.Equal(1, result.Active);
            Assert.Equal(0, result.Deleted);
            Assert.True(File.Exists(path));
            Assert.Equal(1, input.ReadByte());
            storage.Write([4]);
            using var updated = storage.OpenRead();
            var data = new byte[4];
            updated.ReadExactly(data);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, data);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void UnownedOrInvalidMarker_DoesNotAuthorizeDeletingMatchingNames()
    {
        var directory = DirectoryPath();
        Directory.CreateDirectory(directory);
        try
        {
            var path = NativePath(directory);
            WriteAged(path, 86401);
            var logs = new List<string>();
            var unowned = ResourceStorageScavenger.Scavenge(directory, logs.Add, Now);
            Assert.False(unowned.OwnedDirectory);
            Assert.True(File.Exists(path));
            File.WriteAllText(Path.Combine(directory, ResourceStorageScavenger.OwnershipMarkerFileName), "unrelated");
            var invalid = ResourceStorageScavenger.Scavenge(directory, logs.Add, Now);
            Assert.False(invalid.OwnedDirectory);
            Assert.Equal(1, invalid.Errors);
            Assert.True(File.Exists(path));
            Assert.Contains(logs, message => message.Contains("unowned directory", StringComparison.Ordinal));
            Assert.Contains(logs, message => message.Contains("Unrecognized resource storage ownership marker", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ScanBudgetAndCancellation_AreExplicit_AndDiagnosticObserversCannotDisableCleanup()
    {
        var directory = DirectoryPath();
        try
        {
            ResourceStorageScavenger.Initialize(directory);
            for (var index = 0; index < 10; index++) WriteAged(NativePath(directory), 86401);
            var logs = new ConcurrentQueue<string>();
            var bounded = ResourceStorageScavenger.Scavenge(directory, logs.Enqueue, Now, maxEntries: 2);
            Assert.Equal(2, bounded.Examined);
            Assert.True(bounded.ReachedLimit);
            Assert.Contains(logs, message => message.Contains("entry limit", StringComparison.Ordinal));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => ResourceStorageScavenger.Scavenge(directory, cancellationToken: cancellation.Token));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceStorageScavenger.Scavenge(directory, maxEntries: 0));
            var remaining = ResourceStorageScavenger.Scavenge(directory,
                diagnostic: _ => throw new InvalidOperationException("Owned diagnostic observer failure."), now: Now);
            Assert.True(remaining.Deleted > 0);
            Assert.Empty(Directory.GetFiles(directory, "resource-*.bin"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
