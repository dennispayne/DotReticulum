using System.Security.Cryptography;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

internal static class RngitIdentityFile
{
    private static readonly object Gate = new();

    public static Identity LoadOrCreate(string path)
    {
        lock (Gate)
        {
            if (!File.Exists(path))
            {
                using var generated = Identity.Generate();
                var staging = path + "." + Guid.NewGuid().ToString("N") + ".new";
                try
                {
                    IdentityFile.SaveNew(generated, staging);
                    AtomicFilePublication.TryPublishNew(staging, path);
                }
                finally { File.Delete(staging); }
            }

            using var stream = File.OpenRead(path);
            if (stream.Length != Identity.KeySize)
                throw new InvalidDataException("Identity file must be exactly 64 bytes.");
            var key = new byte[Identity.KeySize];
            try
            {
                stream.ReadExactly(key);
                if (stream.ReadByte() != -1) throw new InvalidDataException("Identity file changed while being read.");
                return Identity.FromPrivateKey(key);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }
}
