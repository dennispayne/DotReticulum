using System.Security.Cryptography;

namespace DotReticulum.Core;

/// <summary>Reads and writes identity files in upstream's format: the raw 64-byte private key.</summary>
public static class IdentityFile
{
    /// <summary>
    /// Writes the private key to a new file. Never overwrites (throws <see cref="IOException"/> if it exists)
    /// and, on Unix, creates the file with owner-only (0600) permissions. On Windows the file inherits the
    /// directory's ACLs; callers should tell users to protect it.
    /// </summary>
    public static void SaveNew(Identity identity, string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var key = identity.ExportPrivateKey();
        try
        {
            using var stream = new FileStream(path, options);
            stream.Write(key);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Loads an identity from a 64-byte private key file; throws <see cref="InvalidDataException"/> for any other size.</summary>
    public static Identity Load(string path)
    {
        var data = File.ReadAllBytes(path);
        try
        {
            if (data.Length != Identity.KeySize) throw new InvalidDataException($"Identity file must be exactly {Identity.KeySize} bytes.");
            return Identity.FromPrivateKey(data);
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}
