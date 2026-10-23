using System.Security.Cryptography;
using System.Text;
using DotReticulum.Crypto;

namespace DotReticulum.Core;

/// <summary>
/// Interface authentication from RNS Reticulum.py::_add_interface and Transport.py::handle_outgoing_ifac/handle_ifac.
/// This is a network access code, not peer authentication or encryption.
/// </summary>
public sealed class InterfaceAccessCode : IDisposable
{
    public const int MaximumPacketSize = 524288;
    private static readonly byte[] Salt = Convert.FromHexString("adf54d882c9a9b80771eb4995d702d4a3e733391b2a0f53f416d9f907e55cff8");
    private readonly byte[] _key;
    private readonly Identity _identity;
    private readonly object _gate = new();
    private bool _disposed;

    private InterfaceAccessCode(byte[] key, int size, string? networkName)
    {
        _key = key;
        _identity = Identity.FromPrivateKey(key);
        Size = size;
        NetworkName = networkName;
        Signature = _identity.Sign(SHA256.HashData(key));
    }

    public int Size { get; }
    public string? NetworkName { get; }
    public ReadOnlyMemory<byte> Signature { get; }
    public byte[] ExportPublicKey()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _identity.ExportPublicKey();
        }
    }

    public static InterfaceAccessCode Derive(string? networkName, string? passphrase, int size = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 64);
        networkName = Normalize(networkName);
        passphrase = Normalize(passphrase);
        if (networkName is null && passphrase is null)
            throw new ArgumentException("An IFAC network name or passphrase is required.");
        Span<byte> origin = stackalloc byte[64];
        var length = 0;
        if (networkName is not null)
        {
            SHA256.HashData(Encoding.UTF8.GetBytes(networkName), origin);
            length += 32;
        }
        if (passphrase is not null)
        {
            var encoded = Encoding.UTF8.GetBytes(passphrase);
            try { SHA256.HashData(encoded, origin[length..]); }
            finally { CryptographicOperations.ZeroMemory(encoded); }
            length += 32;
        }
        Span<byte> hash = stackalloc byte[32];
        try
        {
            SHA256.HashData(origin[..length], hash);
            return new InterfaceAccessCode(KeyDerivation.HkdfSha256(hash, 64, Salt), size, networkName);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(origin);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string? Normalize(string? value) => value is null or "" or "None" ? null : value;

    public byte[] Protect(ReadOnlySpan<byte> packet)
    {
        if (packet.Length <= 2 || packet.Length > MaximumPacketSize || (packet[0] & 0x80) != 0)
            throw new ArgumentException("Invalid unprotected IFAC packet length or flag.", nameof(packet));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var signature = _identity.Sign(packet);
            var tag = signature.AsSpan(signature.Length - Size);
            var result = new byte[packet.Length + Size];
            var mask = ExpandMask(tag, result.Length);
            try
            {
                result[0] = (byte)((packet[0] ^ mask[0]) | 0x80);
                result[1] = (byte)(packet[1] ^ mask[1]);
                tag.CopyTo(result.AsSpan(2));
                for (var i = 2; i < packet.Length; i++) result[i + Size] = (byte)(packet[i] ^ mask[i + Size]);
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
                CryptographicOperations.ZeroMemory(mask);
            }
        }
    }

    public bool TryUnprotect(ReadOnlySpan<byte> frame, out byte[]? packet)
    {
        packet = null;
        if (frame.Length <= 2 + Size || frame.Length > MaximumPacketSize + Size || (frame[0] & 0x80) == 0) return false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var tag = frame.Slice(2, Size);
            var mask = ExpandMask(tag, frame.Length);
            var result = new byte[frame.Length - Size];
            byte[]? signature = null;
            try
            {
                result[0] = (byte)((frame[0] ^ mask[0]) & 0x7f);
                result[1] = (byte)(frame[1] ^ mask[1]);
                for (var i = 2; i < result.Length; i++) result[i] = (byte)(frame[i + Size] ^ mask[i + Size]);
                signature = _identity.Sign(result);
                if (!CryptographicOperations.FixedTimeEquals(tag, signature.AsSpan(signature.Length - Size)))
                {
                    CryptographicOperations.ZeroMemory(result);
                    return false;
                }
                packet = result;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(mask);
                if (signature is not null) CryptographicOperations.ZeroMemory(signature);
            }
        }
    }

    // Upstream IFAC deliberately wraps its HKDF counter at 256. Do not use this expansion for cryptographic keys:
    // the public Crypto.KeyDerivation API retains RFC 5869's strict 255-block limit.
    private byte[] ExpandMask(ReadOnlySpan<byte> tag, int length)
    {
        var prk = HMACSHA256.HashData(_key, tag);
        var result = new byte[length];
        Span<byte> input = stackalloc byte[33];
        Span<byte> block = stackalloc byte[32];
        var previousLength = 0;
        try
        {
            for (var offset = 0; offset < length; offset += 32)
            {
                block[..previousLength].CopyTo(input);
                input[previousLength] = unchecked((byte)(offset / 32 + 1));
                HMACSHA256.HashData(prk, input[..(previousLength + 1)], block);
                block[..Math.Min(32, length - offset)].CopyTo(result.AsSpan(offset));
                previousLength = 32;
            }
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(block);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _identity.Dispose();
            CryptographicOperations.ZeroMemory(_key);
        }
    }
}
