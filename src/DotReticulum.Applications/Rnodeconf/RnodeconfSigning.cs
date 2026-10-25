using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace DotReticulum.Applications.Rnodeconf;

public static class RnodeconfSigning
{
    public static byte[] SignEepromChecksum(RSA key, ReadOnlySpan<byte> checksum)
    {
        if (key.KeySize != 1024 || checksum.Length != 16) throw new ArgumentException("EEPROM signing requires RSA-1024 and a 16-byte checksum.");
        var bytes = key.ExportPkcs8PrivateKey();
        try
        {
            var parameters = PrivateKeyFactory.CreateKey(bytes);
            var signer = new PssSigner(new RsaEngine(), new Sha256Digest(), new Sha256Digest(), 94);
            signer.Init(true, new ParametersWithRandom(parameters));
            var message = checksum.ToArray();
            signer.BlockUpdate(message, 0, message.Length);
            return signer.GenerateSignature();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static bool VerifyEeprom(byte[] eeprom, byte[] publicDer)
    {
        if (!RnodeconfClient.IsProvisioned(eeprom)) return false;
        try
        {
            var key = PublicKeyFactory.CreateKey(publicDer);
            if (key is not RsaKeyParameters rsa || rsa.IsPrivate) return false;
            var emLength = (rsa.Modulus.BitLength - 1 + 7) / 8;
            var signer = new PssSigner(new RsaEngine(), new Sha256Digest(), new Sha256Digest(), emLength - 34);
            signer.Init(false, rsa);
            signer.BlockUpdate(eeprom, 11, 16);
            return signer.VerifySignature(eeprom.AsSpan(27, 128).ToArray());
        }
        catch (Exception exception) when (exception is ArgumentException or Org.BouncyCastle.Crypto.CryptoException or IOException) { return false; }
    }

    public static string? VerifyKnownSigner(byte[] eeprom, string stateDirectory)
    {
        using var data = JsonDocument.Parse(RnodeconfReferenceData.Json);
        foreach (var vendor in data.RootElement.GetProperty("vendor_keys").EnumerateArray())
            if (VerifyEeprom(eeprom, Convert.FromHexString(vendor[1].GetString()!))) return vendor[0].GetString();
        var local = Path.Combine(stateDirectory, "firmware", "signing.key");
        if (File.Exists(local))
        {
            using var key = LoadRsa(local);
            if (VerifyEeprom(eeprom, key.ExportSubjectPublicKeyInfo())) return "LOCAL";
        }
        var trusted = Path.Combine(stateDirectory, "trusted_keys");
        if (Directory.Exists(trusted))
        {
            var files = Directory.EnumerateFiles(trusted, "*.pubkey").Take(257).ToArray();
            if (files.Length > 256) throw new IOException("Too many trusted keys.");
            foreach (var file in files)
            {
                if (new FileInfo(file).Length > 4096) continue;
                if (VerifyEeprom(eeprom, File.ReadAllBytes(file))) return "LOCAL";
            }
        }
        return null;
    }

    public static RSA LoadRsa(string path)
    {
        if (new FileInfo(path).Length > 16384) throw new IOException("Signing key exceeds size limit.");
        var bytes = File.ReadAllBytes(path);
        var key = RSA.Create();
        try
        {
            key.ImportPkcs8PrivateKey(bytes, out var consumed);
            if (consumed != bytes.Length) throw new CryptographicException("Trailing signing key data.");
            return key;
        }
        catch { key.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static IReadOnlyList<byte[]> Bootstrap(byte product, byte model, byte hardwareRevision, uint serial, uint timestamp, RSA signer)
    {
        if (hardwareRevision == 0 || serial == 0) throw new ArgumentException("Hardware revision and serial must be nonzero.");
        var image = new byte[156];
        image[0] = product;
        image[1] = model is 0x04 ? (byte)0xb4 : model is 0x09 ? (byte)0xb9 : model;
        image[2] = hardwareRevision;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(3), serial);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(7), timestamp);
        MD5.HashData(image.AsSpan(0, 11)).CopyTo(image, 11);
        SignEepromChecksum(signer, image.AsSpan(11, 16)).CopyTo(image, 27);
        image[155] = 0x73;
        return image.Select((value, address) => RNodeProtocol.Frame(0x52, [(byte)address, value])).ToArray();
    }

    public static async Task GenerateKeysAsync(string stateDirectory, CancellationToken token)
    {
        var folder = Path.Combine(stateDirectory, "firmware");
        Directory.CreateDirectory(folder);
        var devicePath = Path.Combine(folder, "device.key");
        if (!File.Exists(devicePath))
        {
            using var identity = Identity.Generate();
            var bytes = identity.ExportPrivateKey();
            try { await SaveNewAsync(devicePath, bytes, token).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else
        {
            if (new FileInfo(devicePath).Length != 64) throw new IOException("Invalid device signing key length.");
            var bytes = await File.ReadAllBytesAsync(devicePath, token).ConfigureAwait(false);
            try { using var identity = Identity.FromPrivateKey(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var rsaPath = Path.Combine(folder, "signing.key");
        if (!File.Exists(rsaPath))
        {
            using var rsa = RSA.Create(1024);
            var bytes = rsa.ExportPkcs8PrivateKey();
            try { await SaveNewAsync(rsaPath, bytes, token).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else { using var rsa = LoadRsa(rsaPath); }
    }

    internal static async Task SaveNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        try
        {
            await using (stream) { await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
        }
        catch { File.Delete(path); throw; }
    }
}
