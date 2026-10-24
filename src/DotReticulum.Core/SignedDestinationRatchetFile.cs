using System.Security.Cryptography;

namespace DotReticulum.Core;

/// <summary>Upstream-compatible signed destination ratchet files. A writer owns its file for the ring lifetime.
/// Files are bounded, signatures are checked before decoding secrets, and replacement is atomic.
/// This is distinct from the application's versioned, destination-bound runtime-state format.</summary>
public static class SignedDestinationRatchetFile
{
    public const int MaximumBytes = 4 * 1024 * 1024;

    public static byte[] Encode(Identity identity, IReadOnlyList<byte[]> keys)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count > MaximumBytes / (Identity.RatchetSize + 2) ||
            keys.Any(key => key is not { Length: Identity.RatchetSize }))
            throw new InvalidDataException("Invalid or oversized destination ratchet key list.");
        var packed = MsgPack.Pack(keys.Cast<object?>().ToArray());
        try
        {
            var encoded = MsgPack.Pack(new Dictionary<object, object?>
            {
                ["signature"] = identity.Sign(packed),
                ["ratchets"] = packed,
            });
            if (encoded.Length <= MaximumBytes) return encoded;
            CryptographicOperations.ZeroMemory(encoded);
            throw new InvalidDataException("Destination ratchet file is oversized.");
        }
        finally { CryptographicOperations.ZeroMemory(packed); }
    }

    /// <summary>The caller owns and must zero the decoded private keys.</summary>
    public static IReadOnlyList<byte[]> Decode(Identity identity, ReadOnlySpan<byte> encoded)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (encoded.Length > MaximumBytes) throw new InvalidDataException("Destination ratchet file is oversized.");
        byte[]? packed = null;
        object? decoded = null;
        try
        {
            if (MsgPack.Unpack(encoded) is not Dictionary<object, object?> map)
                throw new InvalidDataException("Invalid signed destination ratchet envelope.");
            if (map.TryGetValue("ratchets", out var ratchetValue) && ratchetValue is byte[] bytes) packed = bytes;
            if (!map.TryGetValue("signature", out var signatureValue) || signatureValue is not byte[] { Length: 64 } signature ||
                packed is null)
                throw new InvalidDataException("Invalid signed destination ratchet envelope.");
            if (!identity.Verify(signature, packed)) throw new InvalidDataException("Invalid destination ratchet signature.");
            decoded = MsgPack.Unpack(packed);
            if (decoded is not object?[] keys || keys.Any(key => key is not byte[] { Length: Identity.RatchetSize }))
                throw new InvalidDataException("Invalid destination ratchet keys.");
            var result = keys.Cast<byte[]>().ToArray();
            decoded = null;
            return result;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Malformed signed destination ratchet file.", error);
        }
        finally
        {
            if (packed is not null) CryptographicOperations.ZeroMemory(packed);
            if (decoded is object?[] keys)
                foreach (var key in keys.OfType<byte[]>()) CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Loads or creates a signed file. Ownership is explicit: another writer using this API fails rather than losing keys.
    /// The caller retains ownership of identity; the ring owns an independent signer and file lease.</summary>
    public static DestinationRatchets Open(string path, Identity identity, int retained = DestinationRatchets.DefaultRetained)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfLessThan(retained, 1);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        FileStream? lease = null;
        Identity? signer = null;
        DestinationRatchets? ring = null;
        try
        {
            lease = new FileStream(fullPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var privateKey = identity.ExportPrivateKey();
            try { signer = Identity.FromPrivateKey(privateKey); }
            finally { CryptographicOperations.ZeroMemory(privateKey); }
            var owner = new FileOwner(fullPath, signer, lease);
            signer = null;
            lease = null;
            try
            {
                IReadOnlyList<byte[]> keys = File.Exists(fullPath) ? owner.Read() : [];
                try { ring = DestinationRatchets.FromPrivateKeys(keys, retained: retained); }
                finally { foreach (var key in keys) CryptographicOperations.ZeroMemory(key); }
                ring.EnablePersistence(snapshot => owner.Write(snapshot.PrivateKeys), owner);
                ring.EnableReload(owner.Read);
                return ring;
            }
            catch { owner.Dispose(); throw; }
        }
        catch
        {
            ring?.Dispose();
            signer?.Dispose();
            lease?.Dispose();
            throw;
        }
    }

    private sealed class FileOwner(string path, Identity identity, FileStream lease) : IDisposable
    {
        public IReadOnlyList<byte[]> Read()
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length > MaximumBytes) throw new InvalidDataException("Destination ratchet file is oversized.");
            var data = new byte[checked((int)input.Length)];
            try
            {
                input.ReadExactly(data);
                if (input.ReadByte() != -1) throw new InvalidDataException("Destination ratchet file grew while reading.");
                return Decode(identity, data);
            }
            finally { CryptographicOperations.ZeroMemory(data); }
        }

        public void Write(IReadOnlyList<byte[]> keys)
        {
            var encoded = Encode(identity, keys);
            var outputPath = path + "." + Guid.NewGuid().ToString("N") + ".out";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var output = new FileStream(outputPath, options))
                {
                    output.Write(encoded);
                    output.Flush(flushToDisk: true);
                }
                File.Move(outputPath, path, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
        }

        public void Dispose()
        {
            identity.Dispose();
            lease.Dispose();
        }
    }
}
