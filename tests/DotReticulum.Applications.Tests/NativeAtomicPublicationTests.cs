using System.Text.RegularExpressions;
using DotReticulum.Core;

namespace DotReticulum.Applications.Tests;

public sealed class WindowsNativePublicationFactAttribute : FactAttribute
{
    public WindowsNativePublicationFactAttribute(bool unc = false)
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows namespace publication requires Windows.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_NATIVE_EXE")))
            Skip = "Set DOTRETICULUM_NATIVE_EXE to a published NativeAOT Applications executable.";
        else if (unc && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_PUBLICATION_UNC_ROOT")))
            Skip = "Set DOTRETICULUM_PUBLICATION_UNC_ROOT to an authorized writable UNC directory.";
    }
}

[Trait("Category", "Integration")]
public sealed class NativeAtomicPublicationTests
{
    [NativeFact]
    public Task NativeIdentityPublicationAtLongUnicodePathSurvivesConcurrentCreationAndRestart() =>
        CheckPublication(Path.Combine(Directory.GetCurrentDirectory(), "native-publication-" + Guid.NewGuid().ToString("N")));

    [WindowsNativePublicationFact]
    public Task NativeIdentityPublicationAtExtendedLongUnicodePathSurvivesConcurrentCreationAndRestart() =>
        CheckPublication(@"\\?\" + Path.Combine(Directory.GetCurrentDirectory(), "native-publication-" + Guid.NewGuid().ToString("N")));

    [WindowsNativePublicationFact(unc: true)]
    public Task NativeIdentityPublicationAtUncLongUnicodePathSurvivesConcurrentCreationAndRestart() =>
        CheckPublication(UncRoot(extended: false));

    [WindowsNativePublicationFact(unc: true)]
    public Task NativeIdentityPublicationAtExtendedUncLongUnicodePathSurvivesConcurrentCreationAndRestart() =>
        CheckPublication(UncRoot(extended: true));

    private static string UncRoot(bool extended)
    {
        var root = Environment.GetEnvironmentVariable("DOTRETICULUM_PUBLICATION_UNC_ROOT")!;
        Assert.StartsWith(@"\\", root);
        Assert.False(root.StartsWith(@"\\?\", StringComparison.Ordinal));
        return Path.Combine(extended ? @"\\?\UNC\" + root[2..] : root,
            "native-publication-" + Guid.NewGuid().ToString("N"));
    }

    private static async Task CheckPublication(string root)
    {
        var directory = root;
        while (directory.Length < 650) directory = Path.Combine(directory, new string('p', 60));
        directory = Path.Combine(directory, "escaped # % λ", "profile");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config"),
            "[reticulum]\nshare_instance = no\n[interfaces]\n");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var attempts = new List<NativeResourceTests.NativePeer>();
            string destination;
            try
            {
                for (var index = 0; index < 8; index++)
                    attempts.Add(new NativeResourceTests.NativePeer("rncp", "--config", directory, "-p"));
                await Task.WhenAll(attempts.Select(attempt => attempt.WaitForSuccessAsync(timeout.Token)));
                var destinations = attempts.Select(attempt => ListeningDestination(attempt.Output)).ToArray();
                destination = Assert.Single(destinations.Distinct());
            }
            finally
            {
                foreach (var attempt in attempts) attempt.Dispose();
            }

            var transport = Path.Combine(directory, "storage", "transport_identity");
            Assert.True(transport.Length > 600);
            using var identity = IdentityFile.Load(transport);
            var transportHash = identity.HexHash;
            using (var restarted = new NativeResourceTests.NativePeer("rncp", "--config", directory, "-p"))
            {
                await restarted.WaitForSuccessAsync(timeout.Token);
                Assert.Equal(destination, ListeningDestination(restarted.Output));
            }
            using var reloaded = IdentityFile.Load(transport);
            Assert.Equal(transportHash, reloaded.HexHash);
            Assert.Empty(Directory.GetFiles(directory, "*.out.*", SearchOption.AllDirectories));
            Assert.Equal(64, new FileInfo(transport).Length);
            Assert.Equal(64, new FileInfo(Path.Combine(directory, "storage", "identities", "rncp")).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ListeningDestination(string output)
    {
        var match = Regex.Match(output, @"Listening on\s*:\s*<([0-9a-f]{32})>");
        Assert.True(match.Success, output);
        return match.Groups[1].Value;
    }
}
