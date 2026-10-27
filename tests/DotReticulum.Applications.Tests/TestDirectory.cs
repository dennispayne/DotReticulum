namespace DotReticulum.Applications.Tests;

internal static class TestDirectory
{
    /// <summary>Deletes a test directory tree, retrying while Windows scanners (Defender, the indexer) transiently hold freshly written files.</summary>
    public static void DeleteWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }
}
